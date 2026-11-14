using System.Security.Claims;
using BoSoFa.Server.Btdb;
using BoSoFa.Server.Domain;
using BoSoFa.Server.SkillShare;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace BoSoFa.Server;

/// <summary>
/// The server is the OIDC client (backend-for-frontend): the SPA only ever holds an HttpOnly cookie carrying the
/// BoSoFa user ID. Every sign-in, through OIDC or the development login, is a <see cref="UserSignedIn"/> event.
/// </summary>
public static class Auth
{
    public const string UserIdClaim = "bosofa:uid";
    const string OidcScheme = OpenIdConnectDefaults.AuthenticationScheme;
    const string PersonOrScript = "PersonOrScript";

    public static IServiceCollection AddBoSoFaAuthentication(this IServiceCollection services, ServerOptions options)
    {
        var authentication = services.AddAuthentication(PersonOrScript)
            .AddPolicyScheme(PersonOrScript, null, policy => policy.ForwardDefaultSelector = context =>
                Tokens.ApiTokens.IsPresented(context.Request) ? Tokens.ApiTokens.Scheme
                : Desktop.DesktopAuthenticationHandler.IsPresented(context.Request) ? Desktop.DesktopTokenService.Scheme
                : Desktop.LiveTicketAuthenticationHandler.IsPresented(context.Request) ? Desktop.LiveTicketAuthenticationHandler.SchemeName
                : CookieAuthenticationDefaults.AuthenticationScheme)
            .AddScheme<AuthenticationSchemeOptions, Desktop.DesktopAuthenticationHandler>(Desktop.DesktopTokenService.Scheme, null)
            .AddScheme<AuthenticationSchemeOptions, Desktop.LiveTicketAuthenticationHandler>(Desktop.LiveTicketAuthenticationHandler.SchemeName, null)
            .AddScheme<AuthenticationSchemeOptions, Tokens.ApiTokenAuthenticationHandler>(Tokens.ApiTokens.Scheme, null)
            .AddCookie(cookie =>
            {
                cookie.Cookie.Name = "bosofa";
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Lax; // the OIDC callback is a cross-site top-level navigation
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                cookie.ExpireTimeSpan = TimeSpan.FromDays(14);
                cookie.SlidingExpiration = true;
                // An API caller gets a status code, never a redirect to a login page.
                cookie.Events.OnRedirectToLogin = context => Deny(context, StatusCodes.Status401Unauthorized);
                cookie.Events.OnRedirectToAccessDenied = context => Deny(context, StatusCodes.Status403Forbidden);
            })
            .AddScheme<AuthenticationSchemeOptions, Machines.DaemonAuthenticationHandler>(Machines.DaemonTokens.Scheme, null);
        if (options.Oidc is { Authority.Length: > 0 } oidc)
            authentication.AddOpenIdConnect(OidcScheme, oidc.DisplayName, o =>
            {
                o.Authority = oidc.Authority;
                o.ClientId = oidc.ClientId;
                o.ClientSecret = oidc.ClientSecret;
                o.ResponseType = OpenIdConnectResponseType.Code;
                o.UsePkce = true;
                o.Scope.Add("email");
                o.Scope.Add("profile");
                o.SaveTokens = false;
                o.MapInboundClaims = false;
                // A provider on this computer (a test, or SkillShare in development) serves plain HTTP.
                o.RequireHttpsMetadata = !Uri.TryCreate(oidc.Authority, UriKind.Absolute, out var authority) || !authority.IsLoopback;
                o.Events.OnRedirectToIdentityProvider = async context =>
                {
                    // SkillShare grants this server a refresh token for the person's skills and MCP servers (docs/skillshare.md §2.4).
                    var skillShare = context.HttpContext.RequestServices.GetRequiredService<SkillShareClient>();
                    if (await skillShare.InfoAsync(context.HttpContext.RequestAborted) != null)
                        context.ProtocolMessage.Scope = string.Join(' ', context.ProtocolMessage.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                            .Concat(SkillShareClient.Scopes).Distinct());
                };
                o.Events.OnTokenValidated = async context =>
                {
                    var principal = context.Principal!;
                    var services = context.HttpContext.RequestServices;
                    var skillShare = await services.GetRequiredService<SkillShareClient>().InfoAsync(context.HttpContext.RequestAborted);
                    var signedIn = new UserSignedIn
                    {
                        Subject = principal.FindFirstValue("sub") ?? throw new InvalidOperationException("No subject."),
                        Issuer = principal.FindFirstValue("iss") ?? oidc.Authority,
                        Email = principal.FindFirstValue("email") ?? "",
                        ObjectId = principal.FindFirstValue("oid") ?? "",
                        TenantId = principal.FindFirstValue("tid") ?? "",
                        MigrateFromIssuer = oidc.MigrateFromIssuer.Trim()
                    };
                    signedIn.Name = principal.FindFirstValue("name") ?? principal.FindFirstValue("preferred_username") ?? signedIn.Email;
                    if (skillShare != null)
                    {
                        if (oidc.AdminFromSkillShare && principal.FindFirstValue("skillshare_admin") is { } admin)
                            (signedIn.ProviderSaysRole, signedIn.ProviderSaysAdmin) = (true, string.Equals(admin, "true", StringComparison.OrdinalIgnoreCase));
                        if (context.TokenEndpointResponse?.RefreshToken is { Length: > 0 } refreshToken)
                        {
                            signedIn.GrantId = Guid.NewGuid().ToString("N");
                            signedIn.ProtectedGrant = services.GetRequiredService<IDataProtectionProvider>()
                                .CreateProtector(SkillShareClient.GrantPurpose, signedIn.GrantId).Protect(refreshToken);
                            signedIn.GrantScopes = context.TokenEndpointResponse.Scope ?? string.Join(' ', SkillShareClient.Scopes);
                        }
                    }
                    var node = services.GetRequiredService<BtdbNode>();
                    context.Principal = await SignInAsync(node, signedIn, context.HttpContext.RequestAborted);
                    if (skillShare != null) await SkillShareEndpoints.SyncOnceAsync(node, services.GetRequiredService<BTDB.BackendActions.BackendActions>(),
                        context.HttpContext.RequestAborted);
                };
            });
        services.AddAuthorization(authorization => authorization.AddPolicy(Machines.DaemonTokens.Scheme, policy => policy
            .AddAuthenticationSchemes(Machines.DaemonTokens.Scheme).RequireAuthenticatedUser()));
        return services;
    }

    static Task Deny(RedirectContext<CookieAuthenticationOptions> context, int status)
    {
        context.Response.StatusCode = status;
        return Task.CompletedTask;
    }

    /// <summary>Records the sign-in and returns the cookie principal for the BoSoFa user.</summary>
    internal static Task<ClaimsPrincipal> SignInAsync(BtdbNode node, string issuer, string subject, string name,
        string email, CancellationToken cancellation) =>
        SignInAsync(node, new UserSignedIn { Issuer = issuer, Subject = subject, Name = name, Email = email }, cancellation);

    static async Task<ClaimsPrincipal> SignInAsync(BtdbNode node, UserSignedIn signedIn, CancellationToken cancellation)
    {
        var publisher = await node.Ready.WaitAsync(cancellation);
        var result = await publisher.PublishAndWaitAsync(signedIn, cancellation);
        var identity = new ClaimsIdentity([new Claim(UserIdClaim, result.UserId.ToString())],
            CookieAuthenticationDefaults.AuthenticationScheme, ClaimTypes.Name, ClaimTypes.Role);
        return new ClaimsPrincipal(identity);
    }

    public static ulong? UserId(this ClaimsPrincipal principal) =>
        ulong.TryParse(principal.FindFirstValue(UserIdClaim), out var id) ? id : null;

    public static void MapAuthEndpoints(this WebApplication app, ServerOptions options)
    {
        var auth = app.MapGroup("/auth");
        auth.MapGet("/providers", () => new AuthProviders(options.Oidc is { Authority.Length: > 0 } oidc ? oidc.DisplayName : null,
            options.DevLogin));
        if (options.Oidc is { Authority.Length: > 0 })
            auth.MapGet("/login", (string? returnUrl) => Results.Challenge(
                new AuthenticationProperties { RedirectUri = LocalUrl(returnUrl) }, [OidcScheme]));
        if (options.DevLogin)
            auth.MapPost("/dev-login", async (DevLoginRequest request, BtdbNode node, HttpContext http) =>
            {
                if (string.IsNullOrWhiteSpace(request.Email)) return Results.BadRequest("An email is required.");
                var email = request.Email.Trim();
                var name = string.IsNullOrWhiteSpace(request.Name) ? email : request.Name.Trim();
                var principal = await SignInAsync(node, "dev", email.ToLowerInvariant(), name, email, http.RequestAborted);
                await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
                return Results.NoContent();
            });
        auth.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });
    }

    static string LocalUrl(string? url) => url is { Length: > 0 } && url[0] == '/' && !url.StartsWith("//") ? url : "/";
}

public sealed record DevLoginRequest(string Email, string? Name);

/// <summary>What the login page offers: the OIDC provider's display name, and whether the development login exists.</summary>
public sealed record AuthProviders(string? Oidc, bool DevLogin);
