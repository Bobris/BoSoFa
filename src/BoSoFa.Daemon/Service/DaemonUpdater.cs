using System.Security.Cryptography;
using BoSoFa.Daemon.Sessions;
using BoSoFa.Protocol;

namespace BoSoFa.Daemon.Service;

/// <summary>
/// Takes the daemon build the server carries (plan §5.7): the server offers it in its welcome when it is newer than this one.
/// The binary comes from the server's downloads, must match the offered SHA-256, goes next to the other versions, and once
/// no session is busy the launcher switches to it.
/// </summary>
public sealed class DaemonUpdater(string dataDirectory, Uri server, HttpClient http, bool launched, Func<bool> isBusy, TextWriter log,
    TimeSpan? busyPoll = null)
{
    /// <summary>A session that never ends must not block an update for good.</summary>
    static readonly TimeSpan MaximumWaitForIdle = TimeSpan.FromMinutes(30);

    readonly VersionStore _versions = new(dataDirectory);
    readonly TimeSpan _busyPoll = busyPoll ?? TimeSpan.FromSeconds(5);
    readonly SemaphoreSlim _applying = new(1, 1);
    int _switching;

    /// <summary>The new version is ready and may take over: this process should exit with code 75.</summary>
    public event Action? SwitchRequested;

    /// <summary>Why the last offer was refused; for logs and tests.</summary>
    public string? LastRefusal { get; private set; }

    public static string CurrentRid => Releases.RidOf(DaemonStore.OsName(), DaemonStore.ArchName());

    void Refuse(string reason)
    {
        LastRefusal = reason;
        log.WriteLine("Update refused: " + reason);
    }

    /// <summary>Applies an offer in the background: fetching the binary can take minutes. Failures are logged; the next
    /// connection offers it again.</summary>
    public void Offer(DaemonUpdate update) => _ = Task.Run(async () =>
    {
        try { await ApplyAsync(update, CancellationToken.None); }
        catch (Exception exception) when (exception is IOException or HttpRequestException or UnauthorizedAccessException or TaskCanceledException)
        {
            log.WriteLine($"Update to {update.Version} failed: {exception.Message}");
        }
    });

    /// <summary>Returns once the binary is in place; the switch itself follows when this machine is idle.</summary>
    public async Task ApplyAsync(DaemonUpdate update, CancellationToken cancellation)
    {
        if (!launched)
        {
            Refuse($"this daemon does not run under the launcher ({Brand.Daemon} install).");
            return;
        }
        if (!Releases.IsValidVersion(update.Version))
        {
            Refuse("the offered version is not a plain version.");
            return;
        }
        if (!await _applying.WaitAsync(0, cancellation)) return; // an earlier offer is still being fetched
        try
        {
            if (_versions.Load().Current == update.Version || Volatile.Read(ref _switching) == 1) return;
            var download = Path.Combine(Path.GetTempPath(), $"{Brand.Daemon}-{Guid.NewGuid():N}");
            try
            {
                using (var response = await http.GetAsync(new Uri(server, $"/api/v1/downloads/{Releases.Daemon}/{CurrentRid}"),
                           HttpCompletionOption.ResponseHeadersRead, cancellation))
                {
                    response.EnsureSuccessStatusCode();
                    await using var file = File.Create(download);
                    await response.Content.CopyToAsync(file, cancellation);
                }
                string hash;
                await using (var written = File.OpenRead(download))
                    hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(written, cancellation));
                if (hash != update.Sha256)
                {
                    // Another node of a cluster in the middle of a rolling update may carry another build.
                    Refuse($"the download does not match {update.Version}.");
                    return;
                }
                _versions.Add(update.Version, download);
            }
            finally { File.Delete(download); }
        }
        finally { _applying.Release(); }
        log.WriteLine($"Version {update.Version} is ready.");
        if (Interlocked.Exchange(ref _switching, 1) == 0) _ = Task.Run(() => SwitchWhenIdleAsync(update.Version));
    }

    async Task SwitchWhenIdleAsync(string version)
    {
        var giveUpAt = DateTime.UtcNow + MaximumWaitForIdle;
        while (isBusy() && DateTime.UtcNow < giveUpAt) await Task.Delay(_busyPoll);
        _versions.Save(new(version));
        log.WriteLine($"Switching to version {version}.");
        SwitchRequested?.Invoke();
    }
}
