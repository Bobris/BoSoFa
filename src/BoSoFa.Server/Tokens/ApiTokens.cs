using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using BoSoFa.Protocol;
using BoSoFa.Server.Btdb;
using BoSoFa.Server.Domain;
using BTDB.Replication.EventSourcing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace BoSoFa.Server.Tokens;

/// <summary>Bearer tokens for scripts, acting with their owner's rights. Managed only from a signed-in browser, so a
/// leaked token cannot mint another one.</summary>
public static class ApiTokens
{
    public const string Scheme = "ApiToken";
    public const string Prefix = "bsf_";
    public const string ReadOnlyClaim = "bosofa:readonly";

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool IsPresented(HttpRequest request) =>
        request.Headers.Authorization.ToString().StartsWith("Bearer " + Prefix, StringComparison.Ordinal);

    public static bool IsReadOnly(this ClaimsPrincipal principal) => principal.HasClaim(claim => claim.Type == ReadOnlyClaim);

    public static void MapApiTokenEndpoints(this WebApplication app)
    {
        var tokens = app.MapGroup("/api/v1/tokens").RequireAuthorization();
        tokens.MapGet("", ListAsync);
        tokens.MapPost("", CreateAsync);
        tokens.MapDelete("/{id}", RevokeAsync);
    }

    static bool ViaToken(HttpContext http) => http.User.Identity?.AuthenticationType == Scheme;

    static ApiTokenDto ToDto(ApiToken token) => new(token.Id, token.Name, token.Scope, token.CreatedAt, token.ExpiresAt);

    static async Task<Results<Ok<ApiTokenDto[]>, ForbidHttpResult>> ListAsync(BtdbNode node, HttpContext http)
    {
        if (ViaToken(http)) return TypedResults.Forbid();
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using var query = publisher.StartQuery();
        var userId = http.User.UserId() ?? 0;
        return TypedResults.Ok(Ambient.Relation<IApiTokenTable>().Where(token => token.OwnerId == userId)
            .OrderBy(token => token.Name, StringComparer.OrdinalIgnoreCase).Select(ToDto).ToArray());
    }

    static async Task<Results<Ok<CreatedApiToken>, ForbidHttpResult, BadRequest<string>>> CreateAsync(CreateApiTokenRequest request, BtdbNode node,
        HttpContext http)
    {
        if (ViaToken(http)) return TypedResults.Forbid();
        var secret = Prefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        var result = await publisher.PublishAndWaitAsync(new ApiTokenCreated
        {
            OwnerId = http.User.UserId() ?? 0, Name = request.Name, Scope = request.Scope, Hash = Hash(secret), ExpiresInDays = request.ExpiresInDays
        }, http.RequestAborted);
        if (result.Outcome != CommandOutcome.Ok) return TypedResults.BadRequest("A token needs a name, and at most ten years to live.");
        using var query = publisher.StartQuery();
        return TypedResults.Ok(new CreatedApiToken(secret, ToDto(Ambient.Relation<IApiTokenTable>().FindByIdOrDefault(result.TokenId)!)));
    }

    static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static async Task<Results<NoContent, NotFound, ForbidHttpResult>> RevokeAsync(ulong id, BtdbNode node, HttpContext http)
    {
        if (ViaToken(http)) return TypedResults.Forbid();
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        var result = await publisher.PublishAndWaitAsync(new ApiTokenRevoked { TokenId = id, UserId = http.User.UserId() ?? 0 }, http.RequestAborted);
        return result.Outcome switch
        {
            CommandOutcome.Ok => TypedResults.NoContent(),
            CommandOutcome.Forbidden => TypedResults.Forbid(),
            _ => TypedResults.NotFound()
        };
    }
}

/// <summary>Accepts <c>Authorization: Bearer bsf_…</c>. A read-only token is turned away from anything but reads.</summary>
public sealed class ApiTokenAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    BtdbNode node, TimeProvider time) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    bool _readOnlyWrite;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!ApiTokens.IsPresented(Request)) return AuthenticateResult.NoResult();
        var hash = ApiTokens.Hash(Request.Headers.Authorization.ToString()["Bearer ".Length..]);
        var publisher = await node.Ready.WaitAsync(Context.RequestAborted);
        ApiToken? token;
        using (publisher.StartQuery()) token = Ambient.Relation<IApiTokenTable>().FindByHashOrDefault(hash);
        if (token == null || token.ExpiresAt <= time.GetUtcNow().UtcDateTime) return AuthenticateResult.Fail("Unknown or expired token.");
        var readOnly = token.Scope == ApiTokenScope.Read;
        if (readOnly && !HttpMethods.IsGet(Request.Method) && !HttpMethods.IsHead(Request.Method))
        {
            _readOnlyWrite = true;
            return AuthenticateResult.Fail("This token can only read.");
        }
        List<Claim> claims = [new(Auth.UserIdClaim, token.OwnerId.ToString())];
        if (readOnly) claims.Add(new(ApiTokens.ReadOnlyClaim, "true"));
        return AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims, ApiTokens.Scheme)), ApiTokens.Scheme));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = _readOnlyWrite ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}
