using System.Text;
using BoSoFa.Desktop;
using NeoAstra;
using NeoAstra.Desktop;
using NeoAstra.Desktop.SafeStorage;
using NeoAstra.Rpc;
using BoSoFa.Protocol;

// A desktop client of a BoSoFa server: the same SolidJS page as the web, in a NeoAstra window. Environment:
//   BOSOFA_DESKTOP_DATA        this app's data folder (default: the per-user data folder)
//   BOSOFA_DESKTOP_DAEMON      use this daemon binary instead of a release from the server
//   BOSOFA_DESKTOP_DAEMON_DATA the daemon's data folder (default: its per-user data folder)
//   BOSOFA_DESKTOP_DAEMON_INSTALL_ARGS  more arguments for install and uninstall, such as --dry-run to change nothing
//   BOSOFA_DESKTOP_SMOKE       a JavaScript file run in the page; it sets window.__smoke to a string when it is done
//   BOSOFA_DESKTOP_SMOKE_OUT   where that string is written, after which the app closes

// Runs from a staged update, once this app has closed; there is no window and no page.
if (args is [UpdateHelper.Argument, var updatePlan]) return await UpdateHelper.RunAsync(updatePlan);

var dataRoot = Environment.GetEnvironmentVariable("BOSOFA_DESKTOP_DATA") is { Length: > 0 } configured
    ? Path.GetFullPath(configured)
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), Brand.Name, "desktop");
Directory.CreateDirectory(dataRoot);
var spa = Path.Combine(AppContext.BaseDirectory, "spa");
if (!File.Exists(Path.Combine(spa, "index.html")))
{
    Console.Error.WriteLine("The page is missing: run 'pnpm --dir web build', then build this app again.");
    return 2;
}

using var account = new DesktopAccount(dataRoot, new SafeStorageVault(NeoSafeStorage.CreateSystem($"dev.{Brand.Id}.desktop", Path.Combine(dataRoot, "secrets"))));
// The install script starts the app with the server it came from.
if (args is ["--server", var installedFrom]) account.Preset(installedFrom);
using var daemonHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
var updater = new DesktopUpdater(InstalledApp.FromBaseDirectory(AppContext.BaseDirectory), dataRoot, () => account.Server, daemonHttp, LocalDaemon.EmbeddedReleaseKey);
using var stopUpdates = new CancellationTokenSource();
var localDaemon = new LocalDaemon(account, dataRoot,
    Environment.GetEnvironmentVariable("BOSOFA_DESKTOP_DAEMON_DATA") is { Length: > 0 } daemonData ? Path.GetFullPath(daemonData) : LocalDaemon.DefaultDirectory(),
    new ProcessCommandRunner(), daemonHttp, LocalDaemon.EmbeddedReleaseKey, Environment.GetEnvironmentVariable("BOSOFA_DESKTOP_DAEMON") is { Length: > 0 } daemonPath ? daemonPath : null,
    Environment.GetEnvironmentVariable("BOSOFA_DESKTOP_DAEMON_INSTALL_ARGS")?.Split(' ', StringSplitOptions.RemoveEmptyEntries));
return NeoApplication.Run(new NeoApplicationOptions { ApplicationName = Brand.Name, ShutdownMode = NeoApplicationShutdownMode.Explicit }, async application =>
{
    try
    {
        // On macOS the page runs under the title bar and the window buttons sit in the sidebar's top row, as in other modern apps.
        await using var window = application.CreateWindow(new NeoWindowOptions
        {
            Label = "main", Title = Brand.Name, Width = 1200, Height = 800, IsVisible = false,
            TitleBar = OperatingSystem.IsMacOS() ? new NeoWindowTitleBar(NeoWindowTitleBarStyle.Overlay) : default
        });
        application.MainWindow = window;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        await using var environment = await application.CreateEnvironmentAsync(new NeoEnvironmentOptions
        {
            UserDataRoot = Path.Combine(dataRoot, "webview"),
            CustomSchemes = [NeoCustomScheme.Application("app", new SpaProvider(spa, () => account.Server))]
        });
        var builder = new NeoRpcBuilder(new NeoRpcOptions { ContractHash = NeoRpcGeneratedContract.Hash, Release = true });
        builder.AddAuthService(new AuthService(account));
        builder.AddFilesService(new FilesService(account));
        builder.AddLocalDaemonService(new LocalDaemonService(localDaemon));
        builder.AddUpdatesService(new UpdatesService(updater));
        builder.AddFrameService(new FrameService(window, application.Dispatcher));
        await using var rpc = builder.Build();
        window.Show();
        await using var view = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window), new NeoAstraOptions
        {
            ViewLabel = "main",
            BrowserFeatures = NeoBrowserFeatures.ApplicationShell(),
            BridgePolicy = OperatingSystem.IsLinux() ? NeoBridgePolicy.TrustEntireView : NeoBridgePolicy.TrustedOrigins,
            BridgeOrigins = OperatingSystem.IsLinux() ? [] : ["app://bosofa"]
        });
        // Every app://bosofa document is ours, and nothing else opens in this window.
        view.NavigationRequested = static request => ValueTask.FromResult(
            request.Uri is { Scheme: "app", Host: "bosofa" } ? NeoNavigationDecision.Allow : NeoNavigationDecision.Cancel);
        view.NewWindowRequested = static _ => ValueTask.FromResult(new NeoNewWindowDecision(NeoDecisionAction.Cancel));
        await using var binding = NeoRpcViewBinding.Bind(rpc, view);
        await view.NavigateAsync(new Uri("app://bosofa/"));
        updater.ConfirmIfJustUpdated();
        updater.Quit = () => application.Dispatcher.Post(window.Close);
        _ = updater.RunAsync(stopUpdates.Token);
        if (Environment.GetEnvironmentVariable("BOSOFA_DESKTOP_SMOKE") is { Length: > 0 } script)
            _ = RunSmokeAsync(view, script, Environment.GetEnvironmentVariable("BOSOFA_DESKTOP_SMOKE_OUT")!, () => application.Dispatcher.Post(window.Close));
        await closed.Task;
        await stopUpdates.CancelAsync();
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(exception);
    }
    finally
    {
        application.ForceShutdown();
    }
});

static async Task RunSmokeAsync(NeoAstra.NeoAstra view, string script, string output, Action close)
{
    try
    {
        await Task.Delay(TimeSpan.FromSeconds(2)); // the page has started
        await view.EvaluateScriptAsync($"{await File.ReadAllTextAsync(script)}\n;0");
        var deadline = DateTime.UtcNow.AddSeconds(90);
        string? result = null;
        while (DateTime.UtcNow < deadline)
        {
            result = await view.EvaluateScriptAsync("window.__smoke ?? null");
            if (result is not (null or "null" or "")) break;
            await Task.Delay(250);
        }
        await File.WriteAllTextAsync(output, result ?? "timed out", Encoding.UTF8);
    }
    catch (Exception exception)
    {
        await File.WriteAllTextAsync(output, exception.ToString(), Encoding.UTF8);
    }
    close();
}

sealed class SafeStorageVault(INeoSafeStorage storage) : ITokenVault
{
    const string Key = "refresh-token";

    public async Task<string?> ReadAsync(CancellationToken cancellation)
    {
        var result = await storage.RetrieveAsync(Key, cancellation);
        return result.IsSuccess && result.Value is { Length: > 0 } bytes ? Encoding.UTF8.GetString(bytes) : null;
    }

    public async Task WriteAsync(string token, CancellationToken cancellation)
    {
        var status = await storage.StoreAsync(Key, Encoding.UTF8.GetBytes(token), cancellation);
        if (status != NeoDesktopStatus.Success) throw new IOException($"The token could not be stored safely: {status}");
    }

    public Task DeleteAsync(CancellationToken cancellation) => storage.DeleteAsync(Key, cancellation).AsTask();
}
