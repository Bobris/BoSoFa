using System.Diagnostics;
using NeoAstra;
using NeoAstra.Rpc;
using NeoAstraHello;

// Spike ③: a NeoAstra window hosting a SolidJS 2 SPA on app://bosofa, plus the probes of how that page reaches a
// BoSoFa server. Explicit NeoApplication composition (like CodeAlta's DesktopApplication.RunAsync) instead of the
// simple NeoApp.Run host, because the app:// scheme gets our own resource provider (the HTTP proxy, prototype c).
//
// Environment:
//   BOSOFA_SERVER          server origin (default http://localhost:5080)
//   SPIKE_SPA_DIST         serve this directory (e.g. web/apps/web/dist) instead of the spike's own frontend
//   SPIKE_DEV_LOGIN        email: sign the C# cookie jar in before the page loads (dev login)
//   SPIKE_CAPTURE          write a PNG of the view to this path once the page reported it rendered
//   SPIKE_AUTOCLOSE        close the window after this many seconds (headless verification)

var clock = Stopwatch.StartNew();
var processStart = Process.GetCurrentProcess().StartTime.ToUniversalTime();
Log.Write($"process start → Main: {(DateTime.UtcNow - processStart).TotalMilliseconds:F0} ms");

var server = new Uri(Environment.GetEnvironmentVariable("BOSOFA_SERVER") ?? "http://localhost:5080");
var spaDist = Environment.GetEnvironmentVariable("SPIKE_SPA_DIST");
var session = new ServerSession(server);

var exit = NeoApplication.Run(new NeoApplicationOptions
{
    ApplicationName = "BoSoFa NeoAstra hello",
    ShutdownMode = NeoApplicationShutdownMode.Explicit,
}, async application =>
{
    try
    {
        await using var window = application.CreateWindow(new NeoWindowOptions
        {
            Label = "main", Title = "BoSoFa (NeoAstra spike)", Width = 1100, Height = 760, IsVisible = false,
        });
        application.MainWindow = window;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();

        INeoResourceProvider content;
        if (spaDist is { Length: > 0 })
        {
            content = new SpaDirectoryProvider(Path.GetFullPath(spaDist), server);
            Log.Write($"serving SPA from {Path.GetFullPath(spaDist)}");
        }
        else
        {
            var assets = Path.Combine(AppContext.BaseDirectory, "assets");
            content = new NeoManifestResourceProvider(assets, NeoAssetManifest.Load(Path.Combine(assets, "neoastra-assets.json")));
        }

        await using var environment = await application.CreateEnvironmentAsync(new NeoEnvironmentOptions
        {
            UserDataRoot = Path.Combine(Path.GetTempPath(), "bosofa-neoastra-hello", "webview"),
            CustomSchemes = [NeoCustomScheme.Application("app", new AppSchemeProvider(content, session))],
        });
        Log.Write($"environment created: {clock.ElapsedMilliseconds} ms");

        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = new NeoRpcBuilder(new NeoRpcOptions { ContractHash = NeoRpcGeneratedContract.Hash, Release = true });
        builder.AddSystemService(new SystemService(processStart, rendered));
        builder.AddServerService(new ServerService(session));
        await using var rpc = builder.Build();

        if (Environment.GetEnvironmentVariable("SPIKE_DEV_LOGIN") is { Length: > 0 } email)
            Log.Write($"pre-login {email}: {await session.DevLoginAsync(email)}");

        window.Show();
        var features = NeoBrowserFeatures.ApplicationShell();
        features.DevTools = true;
        await using var view = await environment.CreateWebViewAsync(NeoAstraHost.FillWindow(window), new NeoAstraOptions
        {
            ViewLabel = "main",
            BrowserFeatures = features,
            BridgePolicy = OperatingSystem.IsLinux() ? NeoBridgePolicy.TrustEntireView : NeoBridgePolicy.TrustedOrigins,
            BridgeOrigins = OperatingSystem.IsLinux() ? [] : ["app://bosofa"],
        });
        Log.Write($"view created: {clock.ElapsedMilliseconds} ms");
        // The SPA routes with path history, so every app://bosofa document is ours; everything else is cancelled.
        view.NavigationRequested = static request => ValueTask.FromResult(
            request.Uri is { Scheme: "app", Host: "bosofa" } ? NeoNavigationDecision.Allow : NeoNavigationDecision.Cancel);
        view.NewWindowRequested = static _ => ValueTask.FromResult(new NeoNewWindowDecision(NeoDecisionAction.Cancel));
        await using var binding = NeoRpcViewBinding.Bind(rpc, view);
        await view.NavigateAsync(new Uri(spaDist is { Length: > 0 } ? "app://bosofa/" : "app://bosofa/index.html"));
        Log.Write($"navigation started: {clock.ElapsedMilliseconds} ms");

        if (Environment.GetEnvironmentVariable("SPIKE_CAPTURE") is { Length: > 0 } capture)
            _ = CaptureAsync(view, capture, rendered.Task, spaDist is { Length: > 0 });
        if (int.TryParse(Environment.GetEnvironmentVariable("SPIKE_AUTOCLOSE"), out var seconds))
            _ = Task.Delay(TimeSpan.FromSeconds(seconds)).ContinueWith(_ => application.Dispatcher.Post(window.Close));

        await closed.Task;
        Log.Write("window closed");
    }
    catch (Exception exception)
    {
        Log.Write($"FAILED: {exception}");
    }
    finally
    {
        application.ForceShutdown();
    }
});
session.Dispose();
return exit;

static async Task CaptureAsync(NeoAstra.NeoAstra view, string path, Task rendered, bool externalSpa)
{
    // The spike's page reports when its probes finished; an external SPA cannot, so it gets a fixed delay.
    await (externalSpa ? Task.Delay(3000) : Task.WhenAny(rendered, Task.Delay(15000)));
    await Task.Delay(500);
    try
    {
        var image = await view.CaptureAsync(null);
        await File.WriteAllBytesAsync(path, image.Data.ToArray());
        Log.Write($"captured view to {path}");
        var text = await view.EvaluateScriptAsync("document.body.innerText");
        Log.Write($"page text: {text}");
    }
    catch (Exception exception)
    {
        Log.Write($"capture failed: {exception.Message}");
    }
}

static class Log
{
    public static void Write(string message) => Console.WriteLine($"[spike {DateTime.Now:HH:mm:ss.fff}] {message}");
}
