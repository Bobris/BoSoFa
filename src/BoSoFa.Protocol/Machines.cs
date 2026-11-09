using System.Text.Json.Serialization;

namespace BoSoFa.Protocol;

/// <summary>A daemon asks to join: it generated its ECDSA P-256 key pair and sends the public key (SubjectPublicKeyInfo,
/// base64). A signed-in person approves the returned user code in the web UI; the daemon polls until then.</summary>
/// <param name="Name">The computer's host name; the approver may name the machine differently.</param>
/// <param name="InstanceId">The daemon's own identity, made once and kept in its data folder: enrolling it again for
/// the same owner gives back the same machine.</param>
/// <param name="Resources">What the computer has, for the approver to recognize it.</param>
public sealed record EnrollStartRequest(string Name, string Os, string Arch, string DaemonVersion, string PublicKey,
    Guid? InstanceId = null, MachineResources? Resources = null);

/// <summary>What a computer has, as its daemon measured it at <c>MeasuredAt</c>.</summary>
/// <param name="Platform">The operating system and its version, such as "macOS 26.1" or "Ubuntu 24.04.1 LTS".</param>
/// <param name="Processors">Logical processors.</param>
/// <param name="DiskPath">Where the disk figures were measured: the volume holding the user's home folder.</param>
public sealed record MachineResources(string Platform, int Processors, long MemoryBytes, long MemoryAvailableBytes,
    string DiskPath, long DiskBytes, long DiskFreeBytes, DateTime MeasuredAt);

public sealed record RenameMachineRequest(string Name);

public static class MachineNames
{
    public const int MaximumLength = 100;

    /// <summary>The name to keep, trimmed; null when it is empty, too long or has control characters.</summary>
    public static string? Clean(string? name) => name?.Trim() is { Length: > 0 and <= MaximumLength } trimmed &&
                                                 !trimmed.Any(char.IsControl) ? trimmed : null;
}

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
    DateTime ExpiresAt, EnrollStatus Status, Guid? InstanceId = null, MachineResources? Resources = null);

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

/// <param name="InstanceId">The daemon's own identity (<see cref="EnrollStartRequest.InstanceId"/>); null for a daemon
/// too old to have one.</param>
/// <param name="Resources">As last measured: at enrollment or when the daemon last connected.</param>
public sealed record MachineDto(ulong Id, string Name, string Os, string Arch, ulong OwnerId, string OwnerName,
    string DaemonVersion, bool Online, DateTime? ConnectedAt, InstalledHarness[] Harnesses, bool CanControl = false,
    Guid? InstanceId = null, MachineResources? Resources = null);
