using System.Runtime.Versioning;
using System.ServiceProcess;

namespace BoSoFa.Daemon.Service;

/// <summary>Runs the launcher under the Windows service manager.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsServiceHost(Func<CancellationToken, Task<int>> run) : ServiceBase
{
    readonly CancellationTokenSource _stop = new();
    Task<int>? _running;

    protected override void OnStart(string[] args) => _running = Task.Run(() => run(_stop.Token));

    protected override void OnStop()
    {
        _stop.Cancel();
        _running?.Wait(TimeSpan.FromSeconds(25));
    }

    public static int Run(Func<CancellationToken, Task<int>> run)
    {
        ServiceBase.Run(new WindowsServiceHost(run) { ServiceName = ServicePlans.WindowsName });
        return 0;
    }
}
