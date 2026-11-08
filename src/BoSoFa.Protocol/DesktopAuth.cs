namespace BoSoFa.Protocol;

/// <summary>A signed-in person lets a desktop app on this computer use their account (RFC 8252, with PKCE).</summary>
public sealed record DesktopApproveRequest(string RedirectUri, string CodeChallenge, string State);

/// <summary>Where the browser goes next: the desktop app's loopback address, with the one-time code.</summary>
public sealed record DesktopApproveResponse(string Redirect);

public sealed record DesktopTokenRequest(string Code, string CodeVerifier, string RedirectUri, string Name);

public sealed record DesktopRefreshRequest(string RefreshToken);

public sealed record DesktopTokens(string AccessToken, DateTime ExpiresAt, string RefreshToken);

/// <summary>Opens <c>/api/v1/live?ticket=…</c> once; a browser cannot put a bearer on a WebSocket.</summary>
public sealed record LiveTicket(string Ticket);
