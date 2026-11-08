using System.Text.Json.Serialization;

namespace BoSoFa.Protocol;

/// <summary>A daemon asks to join: it generated its ECDSA P-256 key pair and sends the public key (SubjectPublicKeyInfo,
/// base64). A signed-in person approves the returned user code in the web UI; the daemon polls until then.</summary>
public sealed record EnrollStartRequest(string Name, string Os, string Arch, string DaemonVersion, string PublicKey);

public sealed record EnrollStartResponse(string EnrollmentId, string UserCode, string VerificationUri,
    DateTime ExpiresAt, int PollIntervalSeconds);

public sealed record EnrollPollRequest(string EnrollmentId);

[JsonConverter(typeof(JsonStringEnumConverter<EnrollStatus>))]
public enum EnrollStatus
{
    Pending,
    Approved,
    Expired
}

public sealed record EnrollPollResponse(EnrollStatus Status, ulong? MachineId);

/// <summary>What the approver sees before approving a user code.</summary>
public sealed record EnrollmentDto(string UserCode, string Name, string Os, string Arch, string DaemonVersion,
    DateTime ExpiresAt, EnrollStatus Status);

/// <summary>An enrolled daemon proves its identity with a signature over <see cref="DaemonToken.SignedText"/> and gets
/// a short-lived bearer token for the daemon API.</summary>
public sealed record DaemonTokenRequest(ulong MachineId, long Timestamp, string Nonce, string Signature);

public sealed record DaemonTokenResponse(string AccessToken, DateTime ExpiresAt);

public static class DaemonToken
{
    /// <summary>The text a daemon signs (ECDSA P-256, SHA-256, IEEE P1363 signature, base64).</summary>
    public static string SignedText(ulong machineId, long timestamp, string nonce) =>
        $"bosofa-daemon-token|{machineId}|{timestamp}|{nonce}";
}

public sealed record MachineDto(ulong Id, string Name, string Os, string Arch, ulong OwnerId, string OwnerName,
    string DaemonVersion, bool Online, DateTime? ConnectedAt, InstalledHarness[] Harnesses);
