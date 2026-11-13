using System.Diagnostics;
using BoSoFa.Protocol;

namespace BoSoFa.Daemon.Service;

/// <summary>A running child of the launcher.</summary>
public interface ILauncherChild : IDisposable
{
    /// <summary>Completes with the exit code.</summary>
    Task<int> WaitAsync();

    /// <summary>Asks it to stop, and forces it after a grace period.</summary>
    Task StopAsync();
}

/// <summary>
/// What the service runs (plan §5.7): keeps the current version running, restarts it when it dies, and switches when it exits
/// with <see cref="SwitchVersionExitCode"/> after putting a new version in place. Older versions are deleted.
/// </summary>
public sealed class Launcher(VersionStore versions, Func<string, ILauncherChild> start, TextWriter log)
{
    public const int SwitchVersionExitCode = 75;
    public const string LaunchedVariable = "BOSOFA_LAUNCHED";

    /// <summary>The version the launcher knows this child by, which is what it reports.</summary>
    public const string VersionVariable = "BOSOFA_VERSION";
    static readonly TimeSpan MaximumBackoff = TimeSpan.FromSeconds(30);

    public async Task<int> RunAsync(CancellationToken cancellation)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!cancellation.IsCancellationRequested)
        {
            if (versions.Load().Current is not { } version)
            {
                log.WriteLine($"No version is installed; run '{Brand.Daemon} install'.");
                return 2;
            }
            versions.RemoveAllBut(version);
            var startedAt = DateTime.UtcNow;
            log.WriteLine($"Starting version {version}.");
            using var child = start(versions.ExecutableOf(version));
            var exit = child.WaitAsync();
            var stopped = new TaskCompletionSource();
            await using (cancellation.Register(() => stopped.TrySetResult()))
                if (await Task.WhenAny(exit, stopped.Task) == stopped.Task)
                {
                    await child.StopAsync();
                    return 0;
                }
            if (await exit == SwitchVersionExitCode && versions.Load().Current != version)
            {
                backoff = TimeSpan.FromSeconds(1);
                continue;
            }
            if (DateTime.UtcNow - startedAt > MaximumBackoff) backoff = TimeSpan.FromSeconds(1); // it worked for a while
            log.WriteLine($"Version {version} exited; restarting in {backoff.TotalSeconds:0} s.");
            try { await Task.Delay(backoff, cancellation); }
            catch (OperationCanceledException) { return 0; }
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaximumBackoff.Ticks));
        }
        return 0;
    }
}

/// <summary>The real child: stops when its standard input closes, which also ends it should the launcher die.</summary>
public sealed class ProcessChild : ILauncherChild
{
    static readonly TimeSpan Grace = TimeSpan.FromSeconds(20);
    readonly Process _process;

    public ProcessChild(string executable, string dataDirectory, string[] extraArguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true };
        foreach (var argument in new[] { "run", "--data-dir", dataDirectory }.Concat(extraArguments)) info.ArgumentList.Add(argument);
        info.Environment[Launcher.LaunchedVariable] = "1";
        info.Environment[Launcher.VersionVariable] = Path.GetFileName(Path.GetDirectoryName(executable));
        _process = Process.Start(info)!;
    }

    public async Task<int> WaitAsync()
    {
        await _process.WaitForExitAsync();
        return _process.ExitCode;
    }

    public async Task StopAsync()
    {
        try { _process.StandardInput.Close(); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException) { }
        using var timeout = new CancellationTokenSource(Grace);
        try { await _process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { _process.Kill(entireProcessTree: true); }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    public void Dispose() => _process.Dispose();
}
