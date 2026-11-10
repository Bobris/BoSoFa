using System.Text.Json;
using System.Text.Json.Serialization;
using NeoAstra.Rpc;

[assembly: NeoRpcJsonContext(typeof(BoSoFa.Desktop.DesktopRpcJsonContext))]

namespace BoSoFa.Desktop;

public sealed record EmptyRequest;

/// <param name="Branding">The names this app was built with (its brand), shown until a server tells its own.</param>
public sealed record AuthState(string? Server, bool SignedIn, DesktopBranding Branding);

public sealed record DesktopBranding(string Name, string Tagline, string Daemon);

public sealed record AccessTokenResponse(string? AccessToken, string? ExpiresAt);

public sealed record SignInRequest(string Server);

public sealed record SignInResponse(string? Error);

public sealed record DoneResponse;

public sealed record SaveBlobRequest(string Sha256, string Name);

public sealed record SaveBlobResponse(string? Path);

/// <summary>What the page may ask about the sign-in. The refresh token stays on this side.</summary>
[NeoRpcService("auth", Version = 1)]
public sealed class AuthService
{
    readonly DesktopAccount _account;

    internal AuthService(DesktopAccount account) => _account = account;

    [NeoRpcMethod("state")]
    public async ValueTask<AuthState> StateAsync(EmptyRequest request, CancellationToken cancellationToken) =>
        new(_account.Server, await _account.IsSignedInAsync(cancellationToken),
            new(BoSoFa.Protocol.Brand.Name, BoSoFa.Protocol.Brand.FullName, BoSoFa.Protocol.Brand.Daemon));

    [NeoRpcMethod("accessToken")]
    public async ValueTask<AccessTokenResponse> AccessTokenAsync(EmptyRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var tokens = await _account.AccessTokenAsync(cancellationToken);
            return new(tokens?.AccessToken, tokens?.ExpiresAt.ToString("O"));
        }
        catch (HttpRequestException) { return new(null, null); } // the server is away for now
    }

    [NeoRpcMethod("signIn")]
    public async ValueTask<SignInResponse> SignInAsync(SignInRequest request, CancellationToken cancellationToken) =>
        new(await _account.SignInAsync(request.Server, cancellationToken));

    [NeoRpcMethod("signOut")]
    public async ValueTask<DoneResponse> SignOutAsync(EmptyRequest request, CancellationToken cancellationToken)
    {
        await _account.SignOutAsync(cancellationToken);
        return new();
    }
}

public sealed record ActionResponse(string? Error);

public sealed record AddClaudeAccountRequest(string Label);

/// <summary>"Enable this machine", and the Claude sign-in that needs this computer's browser.</summary>
[NeoRpcService("localDaemon", Version = 1)]
public sealed class LocalDaemonService
{
    readonly LocalDaemon _daemon;

    internal LocalDaemonService(LocalDaemon daemon) => _daemon = daemon;

    [NeoRpcMethod("status")]
    public LocalDaemonStatus Status(EmptyRequest request) => _daemon.Status();

    [NeoRpcMethod("enable")]
    public async ValueTask<ActionResponse> EnableAsync(EmptyRequest request, CancellationToken cancellationToken) => await GuardAsync(_daemon.EnableAsync, cancellationToken);

    [NeoRpcMethod("disable")]
    public async ValueTask<ActionResponse> DisableAsync(EmptyRequest request, CancellationToken cancellationToken) => await GuardAsync(_daemon.DisableAsync, cancellationToken);

    [NeoRpcMethod("addClaudeAccount")]
    public async ValueTask<ActionResponse> AddClaudeAccountAsync(AddClaudeAccountRequest request, CancellationToken cancellationToken) =>
        await GuardAsync(cancellation => _daemon.AddClaudeAccountAsync(request.Label, cancellation), cancellationToken);

    static async ValueTask<ActionResponse> GuardAsync(Func<CancellationToken, Task<string?>> action, CancellationToken cancellationToken)
    {
        try { return new(await action(cancellationToken)); }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException)
        {
            return new(exception.Message);
        }
    }
}

/// <summary>Updates of this app. Nothing here takes a version or a path: what is installed is decided on this side.</summary>
[NeoRpcService("updates", Version = 1)]
public sealed class UpdatesService
{
    readonly DesktopUpdater _updater;

    internal UpdatesService(DesktopUpdater updater) => _updater = updater;

    [NeoRpcMethod("state")]
    public UpdateState State(EmptyRequest request) => _updater.State;

    [NeoRpcMethod("check")]
    public async ValueTask<UpdateState> CheckAsync(EmptyRequest request, CancellationToken cancellationToken) => await _updater.CheckAsync(cancellationToken);

    [NeoRpcMethod("install")]
    public ActionResponse Install(EmptyRequest request)
    {
        try { return new(_updater.Install()); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { return new(exception.Message); }
    }
}

[NeoRpcService("files", Version = 1)]
public sealed class FilesService
{
    readonly DesktopAccount _account;

    internal FilesService(DesktopAccount account) => _account = account;

    /// <summary>Into the Downloads folder: the page cannot save a file from a bearer-protected address by itself.</summary>
    [NeoRpcMethod("saveBlob")]
    public async ValueTask<SaveBlobResponse> SaveBlobAsync(SaveBlobRequest request, CancellationToken cancellationToken)
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return new(await _account.SaveBlobAsync(request.Sha256, request.Name, downloads, cancellationToken));
    }
}

/// <summary>Where the native window buttons sit over the page, in CSS pixels; all zero when the window has its own title bar.</summary>
public sealed record TitleBarLayout(int Height, int LeftInset, int RightInset);

/// <summary>The window's frame for a page that draws its own title bar: where the native buttons are, and moving or zooming the
/// window from the page's top strip. Only this app's own page reaches it.</summary>
[NeoRpcService("frame", Version = 1)]
public sealed class FrameService
{
    readonly NeoAstra.NeoWindow _window;
    readonly NeoAstra.NeoDispatcher _dispatcher;

    internal FrameService(NeoAstra.NeoWindow window, NeoAstra.NeoDispatcher dispatcher) => (_window, _dispatcher) = (window, dispatcher);

    [NeoRpcMethod("titleBar")]
    public async ValueTask<TitleBarLayout> TitleBarAsync(EmptyRequest request, CancellationToken cancellationToken) =>
        await _dispatcher.InvokeAsync(() =>
        {
            var layout = _window.GetTitleBarLayout();
            return layout.Style == NeoAstra.NeoWindowTitleBarStyle.Default ? new TitleBarLayout(0, 0, 0) : new TitleBarLayout(layout.Height, layout.LeftInset, layout.RightInset);
        }, cancellationToken);

    /// <summary>Moves the window with the pointer while its button is still down, as dragging a title bar does.</summary>
    [NeoRpcMethod("startDrag")]
    public async ValueTask<DoneResponse> StartDragAsync(EmptyRequest request, CancellationToken cancellationToken)
    {
        await _dispatcher.InvokeAsync(_window.BeginDrag, cancellationToken);
        return new();
    }

    /// <summary>What a double click on a title bar does: fills the screen, or gives the window its size back.</summary>
    [NeoRpcMethod("zoom")]
    public async ValueTask<DoneResponse> ZoomAsync(EmptyRequest request, CancellationToken cancellationToken)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            if (_window.State == NeoAstra.NeoWindowState.Maximized) _window.Restore();
            else _window.Maximize();
        }, cancellationToken);
        return new();
    }
}

[JsonSerializable(typeof(EmptyRequest))]
[JsonSerializable(typeof(TitleBarLayout))]
[JsonSerializable(typeof(AuthState))]
[JsonSerializable(typeof(AccessTokenResponse))]
[JsonSerializable(typeof(SignInRequest))]
[JsonSerializable(typeof(SignInResponse))]
[JsonSerializable(typeof(DoneResponse))]
[JsonSerializable(typeof(SaveBlobRequest))]
[JsonSerializable(typeof(SaveBlobResponse))]
[JsonSerializable(typeof(LocalDaemonStatus))]
[JsonSerializable(typeof(ActionResponse))]
[JsonSerializable(typeof(AddClaudeAccountRequest))]
[JsonSerializable(typeof(UpdateState))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class DesktopRpcJsonContext : JsonSerializerContext;
