using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using BoSoFa.Protocol;
using BoSoFa.Server.Btdb;
using BoSoFa.Server.Domain;
using BoSoFa.Server.Tokens;
using BTDB.Replication.EventSourcing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace BoSoFa.Server.Desktop;

/// <summary>
/// Sign-in for desktop apps, which cannot use cookies from <c>app://</c> (spike 03): the system browser approves, a
/// one-time code comes back on a loopback address (RFC 8252, PKCE), and the app holds a short access token and a rotating
/// refresh token. Short-lived values are Data Protection payloads, so any node can check them.
/// </summary>
public sealed class DesktopTokenService(IDataProtectionProvider protection, TimeProvider time)
{
    public const string Scheme = "Desktop";
    public const string AccessPrefix = "bsd_";
    public const string RefreshPrefix = "bsr_";
    public const string SessionClaim = "bosofa:desktop";
    public static readonly TimeSpan AccessLifetime = TimeSpan.FromMinutes(15);
    static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(2);
    static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(30);

    readonly ITimeLimitedDataProtector _access = protection.CreateProtector("BoSoFa.DesktopAccess.v1").ToTimeLimitedDataProtector();
    readonly ITimeLimitedDataProtector _codes = protection.CreateProtector("BoSoFa.DesktopCode.v1").ToTimeLimitedDataProtector();
    readonly ITimeLimitedDataProtector _tickets = protection.CreateProtector("BoSoFa.LiveTicket.v1").ToTimeLimitedDataProtector();

    public static string Random(string prefix) => prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string ChallengeOf(string verifier) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Loopback only, over plain HTTP, on any port: the one kind of address a desktop app can listen on.</summary>
    public static bool IsLoopbackRedirect(string redirect) =>
        Uri.TryCreate(redirect, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp && uri.Host is "127.0.0.1" or "localhost" or "[::1]" &&
        uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;

    public string IssueCode(ulong userId, string challenge, string redirect, string state) =>
        _codes.Protect($"{userId}\n{challenge}\n{redirect}\n{state}\n{Guid.NewGuid():N}", time.GetUtcNow() + CodeLifetime);

    public (ulong UserId, string Challenge, string Redirect)? ReadCode(string code)
    {
        try
        {
            var parts = _codes.Unprotect(code, out _).Split('\n');
            return (ulong.Parse(parts[0]), parts[1], parts[2]);
        }
        catch (CryptographicException) { return null; }
    }

    public DesktopTokens Issue(ulong sessionId, ulong userId, string refreshToken)
    {
        var expiresAt = time.GetUtcNow() + AccessLifetime;
        return new(AccessPrefix + _access.Protect($"{sessionId}\n{userId}", expiresAt), expiresAt.UtcDateTime, refreshToken);
    }

    public (ulong SessionId, ulong UserId)? ReadAccess(string token)
    {
        try
        {
            var parts = _access.Unprotect(token[AccessPrefix.Length..], out _).Split('\n');
            return (ulong.Parse(parts[0]), ulong.Parse(parts[1]));
        }
        catch (CryptographicException) { return null; }
    }

    public string IssueTicket(ulong userId, bool readOnly) => _tickets.Protect($"{userId}\n{readOnly}", time.GetUtcNow() + TicketLifetime);

    public (ulong UserId, bool ReadOnly)? ReadTicket(string ticket)
    {
        try
        {
            var parts = _tickets.Unprotect(ticket, out _).Split('\n');
            return (ulong.Parse(parts[0]), bool.Parse(parts[1]));
        }
        catch (CryptographicException) { return null; }
    }
}

public static class DesktopEndpoints
{
    public static void MapDesktopEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/auth/desktop");
        auth.MapPost("/approve", ApproveAsync).RequireAuthorization();
        auth.MapPost("/token", TokenAsync);
        auth.MapPost("/refresh", RefreshAsync);
        auth.MapPost("/revoke", RevokeAsync);
        app.MapPost(LiveSocket.Path + "/ticket", (HttpContext http, DesktopTokenService tokens) =>
            new LiveTicket(tokens.IssueTicket(http.User.UserId() ?? 0, http.User.IsReadOnly()))).RequireAuthorization();
    }

    /// <summary>Only a person at a browser may approve: not a script's token, and not an already signed-in app.</summary>
    static Results<Ok<DesktopApproveResponse>, ForbidHttpResult, BadRequest<string>> ApproveAsync(DesktopApproveRequest request, DesktopTokenService tokens, HttpContext http)
    {
        if (http.User.Identity?.AuthenticationType != CookieAuthenticationDefaults.AuthenticationScheme) return TypedResults.Forbid();
        if (!DesktopTokenService.IsLoopbackRedirect(request.RedirectUri)) return TypedResults.BadRequest("A desktop app must listen on a loopback address.");
        if (request.CodeChallenge.Length != 43 || request.State.Length is 0 or > 200) return TypedResults.BadRequest("Not a valid sign-in request.");
        var code = tokens.IssueCode(http.User.UserId() ?? 0, request.CodeChallenge, request.RedirectUri, request.State);
        var separator = request.RedirectUri.Contains('?') ? '&' : '?';
        return TypedResults.Ok(new DesktopApproveResponse(
            $"{request.RedirectUri}{separator}code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(request.State)}"));
    }

    static async Task<Results<Ok<DesktopTokens>, BadRequest<string>>> TokenAsync(DesktopTokenRequest request, DesktopTokenService tokens, BtdbNode node, HttpContext http)
    {
        if (tokens.ReadCode(request.Code) is not { } code || code.Redirect != request.RedirectUri ||
            !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(DesktopTokenService.ChallengeOf(request.CodeVerifier)), Encoding.ASCII.GetBytes(code.Challenge)))
            return TypedResults.BadRequest("That sign-in is not valid.");
        var refresh = DesktopTokenService.Random(DesktopTokenService.RefreshPrefix);
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        var result = await publisher.PublishAndWaitAsync(new DesktopSessionStarted
        {
            UserId = code.UserId, CodeHash = DesktopTokenService.Hash(request.Code), RefreshHash = DesktopTokenService.Hash(refresh), Name = request.Name
        }, http.RequestAborted);
        return result.Outcome == CommandOutcome.Ok
            ? TypedResults.Ok(tokens.Issue(result.SessionId, result.UserId, refresh))
            : TypedResults.BadRequest("That sign-in is not valid.");
    }

    static async Task<Results<Ok<DesktopTokens>, UnauthorizedHttpResult>> RefreshAsync(DesktopRefreshRequest request, DesktopTokenService tokens, BtdbNode node, HttpContext http)
    {
        var refresh = DesktopTokenService.Random(DesktopTokenService.RefreshPrefix);
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        var result = await publisher.PublishAndWaitAsync(new DesktopSessionRefreshed
        {
            RefreshHash = DesktopTokenService.Hash(request.RefreshToken), NewRefreshHash = DesktopTokenService.Hash(refresh)
        }, http.RequestAborted);
        return result.Outcome == CommandOutcome.Ok ? TypedResults.Ok(tokens.Issue(result.SessionId, result.UserId, refresh)) : TypedResults.Unauthorized();
    }

    static async Task<NoContent> RevokeAsync(DesktopRefreshRequest request, BtdbNode node, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        await publisher.PublishAndWaitAsync(new DesktopSessionRevoked { RefreshHash = DesktopTokenService.Hash(request.RefreshToken) }, http.RequestAborted);
        return TypedResults.NoContent();
    }
}

/// <summary>Accepts a desktop access token, while its session still exists.</summary>
public sealed class DesktopAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    DesktopTokenService tokens, BtdbNode node) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public static bool IsPresented(HttpRequest request) =>
        request.Headers.Authorization.ToString().StartsWith("Bearer " + DesktopTokenService.AccessPrefix, StringComparison.Ordinal);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!IsPresented(Request) || tokens.ReadAccess(Request.Headers.Authorization.ToString()["Bearer ".Length..]) is not { } access)
            return AuthenticateResult.Fail("Unknown or expired token.");
        var publisher = await node.Ready.WaitAsync(Context.RequestAborted);
        using (publisher.StartQuery())
            if (Ambient.Relation<IDesktopSessionTable>().FindByIdOrDefault(access.SessionId) == null)
                return AuthenticateResult.Fail("Signed out.");
        var identity = new ClaimsIdentity([new(Auth.UserIdClaim, access.UserId.ToString()), new(DesktopTokenService.SessionClaim, access.SessionId.ToString())],
            DesktopTokenService.Scheme);
        return AuthenticateResult.Success(new(new ClaimsPrincipal(identity), DesktopTokenService.Scheme));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}

/// <summary>Authenticates <c>/api/v1/live?ticket=…</c>.</summary>
public sealed class LiveTicketAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    DesktopTokenService tokens, LiveTicketsUsed used) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "LiveTicket";

    public static bool IsPresented(HttpRequest request) => request.Path == LiveSocket.Path && request.Query.ContainsKey("ticket");

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string ticket = Request.Query["ticket"].ToString();
        if (tokens.ReadTicket(ticket) is not { } read || !used.TryUse(ticket)) return Task.FromResult(AuthenticateResult.Fail("Not a valid ticket."));
        List<Claim> claims = [new(Auth.UserIdClaim, read.UserId.ToString())];
        if (read.ReadOnly) claims.Add(new(ApiTokens.ReadOnlyClaim, "true"));
        return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}

/// <summary>Tickets seen by this node. Per node: a ticket is unguessable and lasts seconds, so one replay elsewhere is not worth a write.</summary>
public sealed class LiveTicketsUsed(TimeProvider time)
{
    static readonly TimeSpan Remember = TimeSpan.FromMinutes(1);
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _used = new(StringComparer.Ordinal);

    public bool TryUse(string ticket)
    {
        var now = time.GetUtcNow();
        foreach (var (old, at) in _used)
            if (now - at > Remember) _used.TryRemove(old, out _);
        return _used.TryAdd(ticket, now);
    }
}
