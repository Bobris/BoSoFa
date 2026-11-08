using System.Text.Json.Serialization;

namespace BoSoFa.Protocol;

/// <summary>Content stored in the blob store.</summary>
public sealed record BlobRef(string Sha256, long Size);

[JsonConverter(typeof(JsonStringEnumConverter<ArtifactKind>))]
public enum ArtifactKind
{
    Binary,
    TarGz,
    Zip
}

/// <summary>One platform's download of a harness release. <c>Platform</c> is <c>os-arch</c> as machines report them
/// (<c>macos-arm64</c>, <c>linux-x64</c>, <c>windows-x64</c>). <c>EntryPoint</c> is the executable's path in the archive.</summary>
public sealed record HarnessArtifact(string Platform, string Url, string Sha256, ArtifactKind Kind, string EntryPoint);

/// <summary>A catalog entry (plan §5.4): the exact artifacts of one harness version, pinned by hash.</summary>
public sealed record HarnessReleaseDto(string Harness, string Version, HarnessArtifact[] Artifacts, DateTime PublishedAt);

public sealed record PublishHarnessRelease(string Harness, string Version, HarnessArtifact[] Artifacts);

public sealed record InstallHarnessRequest(string Harness, string Version);

/// <summary>A harness a machine has, or the error of its last install attempt.</summary>
public sealed record InstalledHarness(string Harness, string Version, string? Error);
