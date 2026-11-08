using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using BoSoFa.Protocol;

namespace BoSoFa.Daemon.Harnesses;

/// <summary>What is installed for a harness: harnesses/&lt;id&gt;/current.json.</summary>
sealed record HarnessManifest(string Version, string EntryPoint);

[JsonSerializable(typeof(HarnessManifest))]
sealed partial class HarnessJsonContext : JsonSerializerContext;

/// <summary>
/// Installs catalog releases into <c>harnesses/&lt;id&gt;/&lt;version&gt;/</c> (plan §5.4). Nothing runs from a download whose
/// SHA-256 differs from the pinned one. A version is unpacked aside and moved into place in one step, so a failed
/// install leaves the previous version in use; older versions stay on disk for rollback.
/// </summary>
public sealed class HarnessInstaller(string dataDirectory, HttpMessageHandler? handler = null)
{
    readonly string _root = Path.Combine(Path.GetFullPath(dataDirectory), "harnesses");
    readonly ConcurrentDictionary<string, InstalledHarness> _failures = new();
    readonly HttpClient _http = new(handler ?? new SocketsHttpHandler()) { Timeout = TimeSpan.FromMinutes(30) };

    /// <summary>Raised after every install attempt, so the server learns the outcome.</summary>
    public event Action? Changed;

    string ManifestPath(string harness) => Path.Combine(_root, harness, "current.json");

    /// <summary>The installed executable of a harness, or null.</summary>
    public string? Find(string harness)
    {
        if (!File.Exists(ManifestPath(harness))) return null;
        var manifest = JsonSerializer.Deserialize(File.ReadAllText(ManifestPath(harness)), HarnessJsonContext.Default.HarnessManifest);
        if (manifest == null) return null;
        var path = Path.Combine(_root, harness, manifest.Version, manifest.EntryPoint);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Installed harnesses, and the failure of the last attempt where there was one.</summary>
    public InstalledHarness[] Inventory()
    {
        var installed = new Dictionary<string, InstalledHarness>(StringComparer.Ordinal);
        if (Directory.Exists(_root))
            foreach (var directory in Directory.EnumerateDirectories(_root))
            {
                var harness = Path.GetFileName(directory);
                if (!File.Exists(ManifestPath(harness))) continue;
                var manifest = JsonSerializer.Deserialize(File.ReadAllText(ManifestPath(harness)), HarnessJsonContext.Default.HarnessManifest);
                if (manifest != null) installed[harness] = new(harness, manifest.Version, null);
            }
        foreach (var (harness, failure) in _failures) installed[harness] = failure;
        return installed.Values.OrderBy(harness => harness.Harness, StringComparer.Ordinal).ToArray();
    }

    public async Task InstallAsync(InstallHarnessCommand command, CancellationToken cancellation)
    {
        try
        {
            await DownloadAndUnpackAsync(command, cancellation);
            _failures.TryRemove(command.Harness, out _);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or
                                              InvalidOperationException or UnauthorizedAccessException or TaskCanceledException)
        {
            _failures[command.Harness] = new(command.Harness, command.Version, exception.Message);
        }
        Changed?.Invoke();
    }

    async Task DownloadAndUnpackAsync(InstallHarnessCommand command, CancellationToken cancellation)
    {
        var harnessDirectory = Path.Combine(_root, command.Harness);
        Directory.CreateDirectory(harnessDirectory);
        var staging = Path.Combine(harnessDirectory, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var download = staging + ".download";
        try
        {
            var artifact = command.Artifact;
            using (var response = await _http.GetAsync(artifact.Url, HttpCompletionOption.ResponseHeadersRead, cancellation))
            {
                response.EnsureSuccessStatusCode();
                await using var file = File.Create(download);
                await response.Content.CopyToAsync(file, cancellation);
            }
            string actual;
            await using (var file = File.OpenRead(download))
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellation));
            if (!actual.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The download's SHA-256 is {actual}, not the pinned {artifact.Sha256}.");

            var entry = Path.GetFullPath(artifact.EntryPoint, staging);
            if (!entry.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException($"The entry point {artifact.EntryPoint} leaves the install folder.");
            switch (artifact.Kind)
            {
                case ArtifactKind.Binary:
                    Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
                    File.Move(download, entry);
                    break;
                case ArtifactKind.TarGz:
                    await using (var file = File.OpenRead(download))
                    await using (var gzip = new GZipStream(file, CompressionMode.Decompress))
                        await TarFile.ExtractToDirectoryAsync(gzip, staging, overwriteFiles: false, cancellation);
                    break;
                case ArtifactKind.Zip:
                    ZipFile.ExtractToDirectory(download, staging);
                    break;
            }
            if (!File.Exists(entry)) throw new InvalidDataException($"The artifact has no {artifact.EntryPoint}.");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(entry, File.GetUnixFileMode(entry) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute);

            var target = Path.Combine(harnessDirectory, command.Version);
            if (Directory.Exists(target)) Directory.Delete(target, true); // reinstalling the same version
            Directory.Move(staging, target);
            var manifest = ManifestPath(command.Harness);
            await File.WriteAllTextAsync(manifest + ".tmp", JsonSerializer.Serialize(new HarnessManifest(command.Version, artifact.EntryPoint),
                HarnessJsonContext.Default.HarnessManifest), cancellation);
            File.Move(manifest + ".tmp", manifest, true);
        }
        finally
        {
            if (File.Exists(download)) File.Delete(download);
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }
}
