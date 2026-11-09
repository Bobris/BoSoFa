using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using BoSoFa.Protocol;

namespace BoSoFa.Desktop;

/// <summary>Written into a package when it is built; a build without one (<c>dotnet run</c>) never updates itself.</summary>
public sealed record PackagedRelease(string Version, string Rid);

/// <summary>What the helper needs in order to put a staged version in place of the installed one.</summary>
public sealed record UpdatePlan(int ProcessId, string Version, string Staged, string Target, string Executable, string DataRoot);

/// <summary>What the page shows about updates.</summary>
public sealed record UpdateState(string? Current, bool Supported, string? Available, bool Ready, string? Problem);

[JsonSerializable(typeof(PackagedRelease))]
[JsonSerializable(typeof(UpdatePlan))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;

/// <summary>An installed copy of the app: a folder, or a <c>.app</c> bundle on macOS.</summary>
public sealed record InstalledApp(string Version, string Rid, string Root, string Executable)
{
    const string Marker = "release.json";

    public static string ExecutableName => OperatingSystem.IsWindows() ? "bosofa-desktop.exe" : "bosofa-desktop";

    /// <summary>The installed app a running build belongs to, from the folder of its executable.</summary>
    public static InstalledApp? FromBaseDirectory(string directory)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var contents = Path.GetDirectoryName(full);
        var bundle = contents == null ? null : Path.GetDirectoryName(contents);
        var inBundle = Path.GetFileName(full) == "MacOS" && Path.GetFileName(contents) == "Contents" && bundle?.EndsWith(".app", StringComparison.Ordinal) == true;
        return FromRoot(inBundle ? bundle! : full);
    }

    public static InstalledApp? FromRoot(string root)
    {
        var bundled = Path.Combine(root, "Contents", "MacOS");
        var code = File.Exists(Path.Combine(bundled, Marker)) ? bundled : root;
        var marker = Path.Combine(code, Marker);
        if (!File.Exists(marker)) return null;
        var release = JsonSerializer.Deserialize(File.ReadAllText(marker), UpdateJsonContext.Default.PackagedRelease);
        return release == null ? null : new(release.Version, release.Rid, root, Path.Combine(code, ExecutableName));
    }
}

/// <summary>
/// Keeps an installed app up to date from the server it was installed from (plan §6). A release counts only if the key compiled
/// into this build signed it, so a server can offer nothing else. It is unpacked aside, and goes in place when the person restarts.
/// </summary>
public sealed class DesktopUpdater(InstalledApp? installed, string dataRoot, Func<string?> server, HttpClient http, string? releaseKey,
    Func<ProcessStartInfo, IDisposable?>? startProcess = null)
{
    public const string UpdatedVariable = "BOSOFA_DESKTOP_UPDATED";
    static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);

    readonly SemaphoreSlim _checking = new(1, 1);
    readonly Func<ProcessStartInfo, IDisposable?> _start = startProcess ?? (info => Process.Start(info));
    string Updates => Path.Combine(dataRoot, "updates");
    string? _available;
    string? _stagedRoot;
    string? _problem;

    /// <summary>Set once the window exists: closes the app so the helper can replace it.</summary>
    public Action? Quit { get; set; }

    public bool Supported => installed != null && releaseKey != null;

    public UpdateState State => new(installed?.Version, Supported, _available, _stagedRoot != null, _problem);

    public static string CurrentRid => Releases.RidOf(OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux",
        RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant());

    /// <summary>Only forward: a server must not be able to walk apps back to an older build with a known bug.</summary>
    public static bool IsNewer(string candidate, string current)
    {
        static (Version? Number, bool Prerelease) Parse(string text)
        {
            var end = text.IndexOfAny(['-', '+']);
            return (Version.TryParse(end < 0 ? text : text[..end], out var number) ? number : null, end >= 0 && text[end] == '-');
        }
        var (a, aPre) = Parse(candidate);
        var (b, bPre) = Parse(current);
        if (a == null || b == null) return candidate != current;
        var order = a.CompareTo(b);
        return order > 0 || (order == 0 && bPre && !aPre);
    }

    string FailedPath => Path.Combine(Updates, "failed");

    bool HasFailed(string version) => File.Exists(FailedPath) && File.ReadAllLines(FailedPath).Contains(version);

    public static void MarkFailed(string dataRoot, string version)
    {
        var directory = Path.Combine(dataRoot, "updates");
        Directory.CreateDirectory(directory);
        File.AppendAllLines(Path.Combine(directory, "failed"), [version]);
    }

    public static string ConfirmedPath(string dataRoot) => Path.Combine(dataRoot, "updates", "confirmed");

    /// <summary>The new version is up and showing its page: the helper may drop the old one.</summary>
    public void ConfirmIfJustUpdated()
    {
        if (Environment.GetEnvironmentVariable(UpdatedVariable) is not { Length: > 0 } version) return;
        Directory.CreateDirectory(Updates);
        File.WriteAllText(ConfirmedPath(dataRoot), version);
    }

    /// <summary>Checks now and then for as long as the app runs.</summary>
    public async Task RunAsync(CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellation);
            while (true)
            {
                await CheckAsync(cancellation);
                await Task.Delay(CheckEvery, cancellation);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Looks for a newer release and stages it. Failures only end up in <see cref="State"/>: the app works without updates.</summary>
    public async Task<UpdateState> CheckAsync(CancellationToken cancellation)
    {
        if (installed == null || releaseKey == null || server() is not { } origin) return State;
        await _checking.WaitAsync(cancellation);
        try
        {
            _problem = null;
            await CheckCoreAsync(installed, releaseKey, new Uri(origin), cancellation);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or InvalidDataException or UnauthorizedAccessException or TaskCanceledException
                                              && !cancellation.IsCancellationRequested)
        {
            _problem = exception.Message;
        }
        finally { _checking.Release(); }
        if (_problem != null) Console.Error.WriteLine("Update check: " + _problem);
        return State;
    }

    async Task CheckCoreAsync(InstalledApp app, string key, Uri origin, CancellationToken cancellation)
    {
        using var response = await http.GetAsync(new Uri(origin, $"/api/v1/downloads/{Releases.Desktop}/latest?rid={app.Rid}"), cancellation);
        if (!response.IsSuccessStatusCode) return; // nothing published for this platform
        var release = (await response.Content.ReadFromJsonAsync(ProtocolJsonContext.Default.SignedRelease, cancellation))!;
        if (!Releases.Verify(key, release.Manifest, release.Signature))
        {
            _problem = "The server's desktop release is not properly signed.";
            return;
        }
        var manifest = JsonSerializer.Deserialize(release.Manifest, ProtocolJsonContext.Default.ReleaseManifest);
        if (manifest == null || !Releases.IsFor(manifest, Releases.Desktop) || !Releases.IsValidVersion(manifest.Version) ||
            manifest.Artifacts.FirstOrDefault(candidate => candidate.Rid == app.Rid) is not { } artifact)
        {
            _problem = "The server's desktop release does not fit this app.";
            return;
        }
        var wanted = IsNewer(manifest.Version, app.Version) && !HasFailed(manifest.Version);
        RemoveStagedExcept(wanted ? manifest.Version : null);
        if (!wanted)
        {
            _available = null;
            _stagedRoot = null;
            return;
        }
        _available = manifest.Version;
        var staged = Path.Combine(Updates, manifest.Version);
        _stagedRoot = FindStaged(staged, manifest.Version, app.Rid) ?? await StageAsync(origin, manifest.Version, artifact, app.Rid, staged, cancellation);
    }

    void RemoveStagedExcept(string? version)
    {
        if (!Directory.Exists(Updates)) return;
        foreach (var directory in Directory.GetDirectories(Updates))
            if (Path.GetFileName(directory) != version)
                try { Directory.Delete(directory, true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { } // the helper may still run from it
    }

    static string? FindStaged(string staged, string version, string rid)
    {
        if (!Directory.Exists(staged)) return null;
        var roots = Directory.GetDirectories(staged);
        return roots.Length == 1 && InstalledApp.FromRoot(roots[0]) is { } found && found.Version == version && found.Rid == rid && File.Exists(found.Executable) ? roots[0] : null;
    }

    async Task<string?> StageAsync(Uri origin, string version, ReleaseArtifact artifact, string rid, string staged, CancellationToken cancellation)
    {
        Directory.CreateDirectory(Updates);
        var download = staged + ".download";
        var unpacked = staged + ".tmp";
        try
        {
            using (var content = await http.GetAsync(new Uri(origin, $"/api/v1/downloads/{Releases.Desktop}/{version}/{rid}"), HttpCompletionOption.ResponseHeadersRead, cancellation))
            {
                content.EnsureSuccessStatusCode();
                await using var file = File.Create(download);
                await content.Content.CopyToAsync(file, cancellation);
            }
            await using (var written = File.OpenRead(download))
                if (Convert.ToHexStringLower(await SHA256.HashDataAsync(written, cancellation)) != artifact.Sha256)
                {
                    _problem = "The downloaded update does not match its release.";
                    return null;
                }
            if (Directory.Exists(unpacked)) Directory.Delete(unpacked, true);
            Directory.CreateDirectory(unpacked);
            if (Releases.FileName(Releases.Desktop, rid).EndsWith(".zip", StringComparison.Ordinal)) ZipFile.ExtractToDirectory(download, unpacked);
            else
            {
                await using var compressed = File.OpenRead(download);
                await using var archive = new GZipStream(compressed, CompressionMode.Decompress);
                await TarFile.ExtractToDirectoryAsync(archive, unpacked, false, cancellation);
            }
            if (FindStaged(unpacked, version, rid) == null)
            {
                _problem = "The update's contents do not match its release.";
                return null;
            }
            Directory.Move(unpacked, staged);
            return FindStaged(staged, version, rid);
        }
        finally
        {
            File.Delete(download);
            if (Directory.Exists(unpacked)) Directory.Delete(unpacked, true);
        }
    }

    /// <summary>Starts the staged version's helper and closes this app. Null when it goes ahead, else what to tell the person.</summary>
    public string? Install()
    {
        if (installed == null || _stagedRoot is not { } root || _available is not { } version) return "No update is ready.";
        if (InstalledApp.FromRoot(root) is not { } staged) return "The update has gone missing.";
        if (!CanWriteBeside(installed.Root)) return $"The folder of {installed.Root} cannot be written. Install the new version from the server's page.";
        var plan = Path.Combine(Updates, "plan.json");
        File.WriteAllText(plan, JsonSerializer.Serialize(new UpdatePlan(Environment.ProcessId, version, root, installed.Root, installed.Executable, dataRoot),
            UpdateJsonContext.Default.UpdatePlan));
        var info = new ProcessStartInfo(staged.Executable) { UseShellExecute = false };
        info.ArgumentList.Add(UpdateHelper.Argument);
        info.ArgumentList.Add(plan);
        _start(info)?.Dispose();
        var quit = Quit;
        // The page gets its answer first.
        _ = Task.Delay(TimeSpan.FromMilliseconds(300)).ContinueWith(_ => quit?.Invoke(), TaskScheduler.Default);
        return null;
    }

    static bool CanWriteBeside(string root)
    {
        try
        {
            var probe = root + ".probe-" + Environment.ProcessId;
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }
}

/// <summary>A copy of the app the helper started.</summary>
public interface IStartedApp : IDisposable
{
    bool HasExited { get; }
    void Kill();
}

/// <summary>
/// Runs from the staged copy once the installed app has closed, because a running program cannot be replaced on Windows. The new
/// version goes in beside the old one and swaps with it by renames, and comes back out if it does not show its page in time.
/// </summary>
public sealed class UpdateHelper(UpdatePlan plan, Func<string, string?, IStartedApp?> start, TextWriter log, TimeSpan? closeLimit = null, TimeSpan? confirmLimit = null,
    TimeSpan? poll = null, Func<int, TimeSpan, Task<bool>>? waitForExit = null)
{
    public const string Argument = "--apply-update";

    readonly TimeSpan _closeLimit = closeLimit ?? TimeSpan.FromMinutes(1);
    readonly TimeSpan _confirmLimit = confirmLimit ?? TimeSpan.FromMinutes(2);
    readonly TimeSpan _poll = poll ?? TimeSpan.FromMilliseconds(500);
    readonly Func<int, TimeSpan, Task<bool>> _waitForExit = waitForExit ?? WaitForExitAsync;

    /// <summary>The entry point of <c>--apply-update</c>.</summary>
    public static async Task<int> RunAsync(string planFile)
    {
        var plan = JsonSerializer.Deserialize(await File.ReadAllTextAsync(planFile), UpdateJsonContext.Default.UpdatePlan)!;
        await using var log = new StreamWriter(Path.Combine(Path.GetDirectoryName(planFile)!, "update.log"), true) { AutoFlush = true };
        return await new UpdateHelper(plan, StartReal, log).ApplyAsync();
    }

    sealed class RealApp(Process process) : IStartedApp
    {
        public bool HasExited => process.HasExited;
        public void Kill() => process.Kill(entireProcessTree: true);
        public void Dispose() => process.Dispose();
    }

    static IStartedApp? StartReal(string executable, string? updatedVersion)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false };
        if (updatedVersion != null) info.Environment[DesktopUpdater.UpdatedVariable] = updatedVersion;
        return Process.Start(info) is { } process ? new RealApp(process) : null;
    }

    static async Task<bool> WaitForExitAsync(int processId, TimeSpan limit)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            using var timeout = new CancellationTokenSource(limit);
            await process.WaitForExitAsync(timeout.Token);
            return true;
        }
        catch (ArgumentException) { return true; } // already gone
        catch (OperationCanceledException) { return false; }
    }

    /// <summary>Zero when the new version runs. In every other case the old one is back in place and running.</summary>
    public async Task<int> ApplyAsync()
    {
        if (!await _waitForExit(plan.ProcessId, _closeLimit))
        {
            log.WriteLine("The app did not close, so nothing was changed.");
            return 1;
        }
        var incoming = plan.Target + ".new";
        var previous = plan.Target + ".previous";
        try
        {
            RemoveIfThere(incoming);
            RemoveIfThere(previous);
            CopyDirectory(plan.Staged, incoming);
            File.Delete(DesktopUpdater.ConfirmedPath(plan.DataRoot));
            Directory.Move(plan.Target, previous);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log.WriteLine("Could not put the new version beside the old one: " + exception.Message);
            return Abandon(incoming, previous, failed: false); // a locked file, say: the person may try again
        }
        try { Directory.Move(incoming, plan.Target); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log.WriteLine("Could not move the new version in: " + exception.Message);
            return Abandon(incoming, previous, failed: false);
        }
        using var app = start(plan.Executable, plan.Version);
        if (app != null && await IsConfirmedAsync(app))
        {
            log.WriteLine($"Version {plan.Version} is running.");
            RemoveQuietly(previous);
            return 0;
        }
        log.WriteLine($"Version {plan.Version} did not start properly; going back.");
        try { if (app is { HasExited: false }) app.Kill(); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        return Abandon(incoming, previous, failed: true);
    }

    async Task<bool> IsConfirmedAsync(IStartedApp app)
    {
        var path = DesktopUpdater.ConfirmedPath(plan.DataRoot);
        var deadline = DateTime.UtcNow + _confirmLimit;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path) && (await File.ReadAllTextAsync(path)).Trim() == plan.Version) return true;
            if (app.HasExited) return false;
            await Task.Delay(_poll);
        }
        return false;
    }

    /// <summary>Puts the old version back if it was moved and starts it. A version that ran and failed is never offered again.</summary>
    int Abandon(string incoming, string previous, bool failed)
    {
        try
        {
            if (failed) RemoveIfThere(plan.Target);
            if (Directory.Exists(previous) && !Directory.Exists(plan.Target)) Directory.Move(previous, plan.Target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log.WriteLine("Could not restore the old version: " + exception.Message);
        }
        RemoveQuietly(incoming);
        if (failed) DesktopUpdater.MarkFailed(plan.DataRoot, plan.Version);
        start(plan.Executable, null)?.Dispose();
        return 1;
    }

    static void RemoveIfThere(string directory)
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    static void RemoveQuietly(string directory)
    {
        try { RemoveIfThere(directory); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)));
    }
}
