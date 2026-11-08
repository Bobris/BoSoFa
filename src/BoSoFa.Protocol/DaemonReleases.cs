using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BoSoFa.Protocol;

public sealed record DaemonArtifact(string Rid, string Sha256, long Size);

/// <summary>What a release contains. Its JSON text is what the offline release key signs (plan §5.7).</summary>
public sealed record DaemonManifest(string Version, DaemonArtifact[] Artifacts);

public sealed record PublishDaemonRelease(string Manifest, string Signature, string Channel);

/// <summary>A release with what is needed to check it.</summary>
public sealed record SignedDaemonRelease(string Version, string Manifest, string Signature);

public sealed record DaemonReleaseDto(string Version, string Channel, DateTime PublishedAt, string[] Rids);

[JsonConverter(typeof(JsonStringEnumConverter<DaemonRolloutStatus>))]
public enum DaemonRolloutStatus
{
    Running,
    Done,
    Halted
}

/// <summary><c>WaveSize</c> machines update at a time. One that is not back on the new version after
/// <c>FailAfterSeconds</c> halts the rollout.</summary>
public sealed record StartDaemonRollout(string Version, int WaveSize = 1, int? FailAfterSeconds = null);

public sealed record DaemonRolloutDto(ulong Id, string Version, string Channel, DaemonRolloutStatus Status, DateTime StartedAt, int Updated, int InFlight, string Detail);

/// <summary>The default channel is <see cref="DaemonReleases.Stable"/>. A pin holds a machine at one version.</summary>
public sealed record SetMachineUpdatePolicy(string Channel, string? PinnedVersion);

public static partial class DaemonReleases
{
    public const string Stable = "stable";
    public const string Beta = "beta";

    /// <summary>A version names a folder on every machine, so it must be plain.</summary>
    public static bool IsValidVersion(string version) => version.Length is > 0 and <= 100 && version[0] != '.' && VersionPattern().IsMatch(version);

    public static bool IsValidChannel(string channel) => channel is Stable or Beta;

    /// <summary>The .NET runtime identifier of a machine, from what its daemon reports.</summary>
    public static string RidOf(string os, string arch) => (os == "macos" ? "osx" : os == "windows" ? "win" : os) + "-" + arch;

    /// <summary>ECDSA P-256, IEEE P1363 signature over the manifest's UTF-8 bytes; the key is SubjectPublicKeyInfo, base64.</summary>
    public static bool Verify(string publicKey, string manifest, string signature)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return key.VerifyData(Encoding.UTF8.GetBytes(manifest), Convert.FromBase64String(signature), HashAlgorithmName.SHA256);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            return false;
        }
    }

    [GeneratedRegex("^[0-9A-Za-z.+_-]+$")]
    private static partial Regex VersionPattern();
}
