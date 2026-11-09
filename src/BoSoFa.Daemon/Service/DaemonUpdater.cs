using System.Text.Json;
using BoSoFa.Daemon.Sessions;
using BoSoFa.Protocol;

namespace BoSoFa.Daemon.Service;

/// <summary>
/// Applies a release (plan §5.7): checks its signature against the key compiled into this build, fetches the binary for
/// this machine next to the others, and once no session is busy hands over to the launcher.
/// </summary>
public sealed class DaemonUpdater(string dataDirectory, IBlobDownloader? blobs, string? publicKey, bool launched, Func<bool> isBusy, TextWriter log,
    TimeSpan? busyPoll = null)
{
    /// <summary>A session that never ends must not block an update for good.</summary>
    static readonly TimeSpan MaximumWaitForIdle = TimeSpan.FromMinutes(30);

    readonly VersionStore _versions = new(dataDirectory);
    readonly TimeSpan _busyPoll = busyPoll ?? TimeSpan.FromSeconds(5);
    int _switching;

    /// <summary>The new version is ready and may take over: this process should exit with code 75.</summary>
    public event Action? SwitchRequested;

    /// <summary>Why the last release was refused; for logs and tests.</summary>
    public string? LastRefusal { get; private set; }

    public static string CurrentRid => Releases.RidOf(DaemonStore.OsName(), DaemonStore.ArchName());

    void Refuse(string reason)
    {
        LastRefusal = reason;
        log.WriteLine("Update refused: " + reason);
    }

    /// <summary>Returns once the binary is in place; the switch itself follows when this machine is idle.</summary>
    public async Task ApplyAsync(UpdateDaemonCommand update, CancellationToken cancellation)
    {
        if (!launched)
        {
            Refuse($"this daemon does not run under the launcher ({Brand.Daemon} install).");
            return;
        }
        if (publicKey == null || blobs == null)
        {
            Refuse("this build has no release key.");
            return;
        }
        if (!Releases.Verify(publicKey, update.Manifest, update.Signature))
        {
            Refuse($"the signature of {update.Version} does not match.");
            return;
        }
        var manifest = JsonSerializer.Deserialize(update.Manifest, ProtocolJsonContext.Default.ReleaseManifest);
        if (manifest == null || manifest.Version != update.Version || !Releases.IsValidVersion(manifest.Version) || !Releases.IsFor(manifest, Releases.Daemon))
        {
            Refuse("the manifest does not describe this version.");
            return;
        }
        var state = _versions.Load();
        if (state.Current == manifest.Version) return; // a repeated command, or already there
        if (state.Failed.Contains(manifest.Version))
        {
            Refuse($"{manifest.Version} already failed on this machine.");
            return;
        }
        if (manifest.Artifacts.FirstOrDefault(artifact => artifact.Rid == CurrentRid) is not { } artifact)
        {
            Refuse($"{manifest.Version} has no build for {CurrentRid}.");
            return;
        }
        var download = Path.Combine(Path.GetTempPath(), $"bosofad-{Guid.NewGuid():N}");
        try
        {
            await blobs.DownloadBlobAsync(artifact.Sha256, download, cancellation);
            _versions.Add(manifest.Version, download);
        }
        finally { File.Delete(download); }
        log.WriteLine($"Version {manifest.Version} is ready.");
        if (Interlocked.Exchange(ref _switching, 1) == 0) _ = Task.Run(() => SwitchWhenIdleAsync(manifest.Version));
    }

    async Task SwitchWhenIdleAsync(string version)
    {
        var giveUpAt = DateTime.UtcNow + MaximumWaitForIdle;
        while (isBusy() && DateTime.UtcNow < giveUpAt) await Task.Delay(_busyPoll);
        var state = _versions.Load();
        _versions.Save(new(version, state.Current, DateTime.UtcNow, state.Failed));
        log.WriteLine($"Switching to version {version}.");
        SwitchRequested?.Invoke();
    }
}
