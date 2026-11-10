using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using BoSoFa.Protocol;
using BoSoFa.Server.Btdb;
using BoSoFa.Server.Domain;
using BTDB.Replication.EventSourcing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace BoSoFa.Server.Machines;

/// <summary>
/// Daemons authenticate with their machine key: a signed, timestamped and nonce-bound request buys a short-lived
/// bearer token (a time-limited Data Protection payload holding the machine ID). The server stores only public keys.
/// </summary>
public sealed class DaemonTokens(IDataProtectionProvider protection, TimeProvider time)
{
    public const string Scheme = "Daemon";
    public const string MachineIdClaim = "bosofa:mid";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    readonly ITimeLimitedDataProtector _protector =
        protection.CreateProtector("BoSoFa.DaemonToken.v1").ToTimeLimitedDataProtector();
    readonly ConcurrentDictionary<string, DateTimeOffset> _nonces = new(StringComparer.Ordinal);

    /// <summary>Null when the signature, timestamp or nonce is not acceptable.</summary>
    public DaemonTokenResponse? Issue(DaemonTokenRequest request, string publicKey)
    {
        var now = time.GetUtcNow();
        var signedAt = DateTimeOffset.FromUnixTimeMilliseconds(request.Timestamp);
        if ((now - signedAt).Duration() > ClockSkew || request.Nonce.Length is < 16 or > 128) return null;
        if (!Verify(publicKey, DaemonToken.SignedText(request.MachineId, request.Timestamp, request.Nonce), request.Signature))
            return null;
        foreach (var (nonce, expiry) in _nonces)
            if (expiry < now) _nonces.TryRemove(nonce, out _);
        if (!_nonces.TryAdd(request.Nonce, now + 2 * ClockSkew)) return null; // replayed
        var expiresAt = now + Lifetime;
        return new(_protector.Protect(request.MachineId.ToString(), expiresAt), expiresAt.UtcDateTime);
    }

    public ulong? Validate(string token)
    {
        try
        {
            return ulong.TryParse(_protector.Unprotect(token, out _), out var id) ? id : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    static bool Verify(string publicKey, string text, string signature)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return key.VerifyData(Encoding.UTF8.GetBytes(text), Convert.FromBase64String(signature), HashAlgorithmName.SHA256);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            return false;
        }
    }
}

public sealed class DaemonAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, DaemonTokens tokens, BtdbNode node) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal)) return AuthenticateResult.NoResult();
        if (tokens.Validate(header["Bearer ".Length..].Trim()) is not { } machineId)
            return AuthenticateResult.Fail("Invalid or expired daemon token.");
        // A token outlives its machine by up to its lifetime; a removed machine is refused at once.
        if (!Exists(await node.Ready.WaitAsync(Context.RequestAborted), machineId)) return AuthenticateResult.Fail("This machine was removed.");
        var identity = new ClaimsIdentity([new Claim(DaemonTokens.MachineIdClaim, machineId.ToString())], Scheme.Name);
        return AuthenticateResult.Success(new(new ClaimsPrincipal(identity), Scheme.Name));
    }

    internal static bool Exists(EventPublisher publisher, ulong machineId)
    {
        using var query = publisher.StartQuery();
        return Ambient.Relation<IMachineTable>().FindByIdOrDefault(machineId) != null;
    }
}

public static class DaemonPrincipal
{
    public static ulong? MachineId(this ClaimsPrincipal principal) =>
        ulong.TryParse(principal.FindFirstValue(DaemonTokens.MachineIdClaim), out var id) ? id : null;
}
