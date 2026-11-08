using System.Security.Claims;
using BoSoFa.Server.Btdb;
using BoSoFa.Server.Domain;
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
                Tokens.ApiTokens.IsPresented(context.Request) ? Tokens.ApiTokens.Scheme : CookieAuthenticationDefaults.AuthenticationScheme)
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
                o.Events.OnTokenValidated = async context =>
                {
                    var principal = context.Principal!;
                    var subject = principal.FindFirstValue("sub") ?? throw new InvalidOperationException("No subject.");
                    var issuer = principal.FindFirstValue("iss") ?? oidc.Authority;
                    var email = principal.FindFirstValue("email") ?? "";
                    var name = principal.FindFirstValue("name") ?? principal.FindFirstValue("preferred_username") ?? email;
                    var node = context.HttpContext.RequestServices.GetRequiredService<BtdbNode>();
                    context.Principal = await SignInAsync(node, issuer, subject, name, email, context.HttpContext.RequestAborted);
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
    internal static async Task<ClaimsPrincipal> SignInAsync(BtdbNode node, string issuer, string subject, string name,
        string email, CancellationToken cancellation)
    {
        var publisher = await node.Ready.WaitAsync(cancellation);
        var result = await publisher.PublishAndWaitAsync(
            new UserSignedIn { Issuer = issuer, Subject = subject, Name = name, Email = email }, cancellation);
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
