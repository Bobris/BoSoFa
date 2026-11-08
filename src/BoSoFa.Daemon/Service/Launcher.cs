using System.Diagnostics;

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
/// What the service runs (plan §5.7): keeps the current version running, restarts it when it dies, switches when it
/// exits with <see cref="SwitchVersionExitCode"/>, and takes back a new version that does not reach the server in time.
/// </summary>
public sealed class Launcher(VersionStore versions, Func<string, ILauncherChild> start, TextWriter log, TimeSpan? trialLimit = null, TimeSpan? pollInterval = null)
{
    public const int SwitchVersionExitCode = 75;
    public const string LaunchedVariable = "BOSOFA_LAUNCHED";
    static readonly TimeSpan MaximumBackoff = TimeSpan.FromSeconds(30);

    readonly TimeSpan _trialLimit = trialLimit ?? TimeSpan.FromMinutes(2);
    readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);

    public async Task<int> RunAsync(CancellationToken cancellation)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!cancellation.IsCancellationRequested)
        {
            var state = versions.Load();
            if (state.Current is not { } version)
            {
                log.WriteLine("No version is installed; run 'bosofad install'.");
                return 2;
            }
            var startedAt = DateTime.UtcNow;
            log.WriteLine($"Starting version {version}{(state.TrialStartedAt != null ? " on trial" : "")}.");
            using var child = start(versions.ExecutableOf(version));
            var outcome = await SuperviseAsync(child, version, state, cancellation);
            if (cancellation.IsCancellationRequested) return 0;
            switch (outcome)
            {
                case Outcome.Failed:
                    RollBack(version);
                    backoff = TimeSpan.FromSeconds(1);
                    continue;
                case Outcome.Switch:
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

    enum Outcome
    {
        Exited,
        Switch,
        Failed
    }

    async Task<Outcome> SuperviseAsync(ILauncherChild child, string version, VersionState state, CancellationToken cancellation)
    {
        var exit = child.WaitAsync();
        var onTrial = state.TrialStartedAt != null;
        var stopped = new TaskCompletionSource();
        await using var registration = cancellation.Register(() => stopped.TrySetResult());
        while (true)
        {
            var finished = await Task.WhenAny(exit, stopped.Task, Task.Delay(_pollInterval, CancellationToken.None));
            if (finished == stopped.Task)
            {
                await child.StopAsync();
                return Outcome.Exited;
            }
            if (onTrial && versions.IsConfirmed(version))
            {
                versions.Save(versions.Load() with { TrialStartedAt = null });
                onTrial = false;
                log.WriteLine($"Version {version} is confirmed.");
            }
            if (exit.IsCompleted)
            {
                var code = await exit;
                if (onTrial) return Outcome.Failed; // it never reached the server
                return code == SwitchVersionExitCode ? Outcome.Switch : Outcome.Exited;
            }
            if (onTrial && DateTime.UtcNow - state.TrialStartedAt > _trialLimit)
            {
                await child.StopAsync();
                return Outcome.Failed;
            }
        }
    }

    void RollBack(string failed)
    {
        var state = versions.Load();
        log.WriteLine($"Version {failed} did not reach the server; going back to {state.Previous ?? "nothing"}.");
        versions.Save(new(state.Previous, null, null, [..state.Failed, failed]));
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
