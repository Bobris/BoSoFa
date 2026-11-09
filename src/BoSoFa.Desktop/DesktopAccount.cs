using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BoSoFa.Protocol;

namespace BoSoFa.Desktop;

/// <summary>Keeps the refresh token where only this user can read it.</summary>
public interface ITokenVault
{
    Task<string?> ReadAsync(CancellationToken cancellation);
    Task WriteAsync(string token, CancellationToken cancellation);
    Task DeleteAsync(CancellationToken cancellation);
}

public sealed record DesktopSettings(string? Server);

[JsonSerializable(typeof(DesktopSettings))]
internal sealed partial class DesktopSettingsContext : JsonSerializerContext;

/// <summary>
/// This app's sign-in with a server (spike 03): approval in the system browser, the code back on a loopback address, then a
/// short access token and a refresh token that rotates. The page only ever sees access tokens.
/// </summary>
public sealed class DesktopAccount(string dataDirectory, ITokenVault vault, Func<Uri, Task>? openBrowser = null, HttpMessageHandler? handler = null) : IDisposable
{
    static readonly TimeSpan BrowserTimeout = TimeSpan.FromMinutes(5);
    static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(1);
    const string RedirectPath = "/callback";

    readonly HttpClient _http = new(handler ?? new SocketsHttpHandler()) { Timeout = TimeSpan.FromSeconds(30) };
    readonly SemaphoreSlim _lock = new(1);
    readonly Func<Uri, Task> _openBrowser = openBrowser ?? OpenSystemBrowserAsync;
    string SettingsPath => Path.Combine(dataDirectory, "desktop.json");
    DesktopTokens? _tokens;
    bool? _signedIn;

    public string? Server { get; private set; } = LoadServer(dataDirectory);

    static string? LoadServer(string directory)
    {
        var path = Path.Combine(directory, "desktop.json");
        return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), DesktopSettingsContext.Default.DesktopSettings)?.Server : null;
    }

    /// <summary>The server this app was installed from, until the person signs in somewhere. An address that makes no sense is
    /// ignored: they can still type one.</summary>
    public void Preset(string server)
    {
        if (Server != null || !Uri.TryCreate(server.Trim(), UriKind.Absolute, out var origin) || origin.Scheme is not ("https" or "http")) return;
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new DesktopSettings(origin.GetLeftPart(UriPartial.Authority)), DesktopSettingsContext.Default.DesktopSettings));
        Server = origin.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>The system browser, or what <c>BROWSER</c> names, as command-line tools do.</summary>
    static Task OpenSystemBrowserAsync(Uri address)
    {
        var info = Environment.GetEnvironmentVariable("BROWSER") is { Length: > 0 } browser
            ? new ProcessStartInfo(browser) { ArgumentList = { address.ToString() }, UseShellExecute = false }
            : new ProcessStartInfo(address.ToString()) { UseShellExecute = true };
        Process.Start(info)?.Dispose();
        return Task.CompletedTask;
    }

    public async Task<bool> IsSignedInAsync(CancellationToken cancellation)
    {
        _signedIn ??= Server != null && await vault.ReadAsync(cancellation) != null;
        return _signedIn.Value;
    }

    /// <summary>Null when it worked, else what to tell the person.</summary>
    public async Task<string?> SignInAsync(string server, CancellationToken cancellation)
    {
        if (!Uri.TryCreate(server.Trim().TrimEnd('/'), UriKind.Absolute, out var origin) || origin.Scheme is not ("https" or "http"))
            return "That is not a server address.";
        using var listener = new HttpListener();
        var port = FreeLoopbackPort();
        var redirect = $"http://127.0.0.1:{port}{RedirectPath}";
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var verifier = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var state = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var authorize = new Uri(origin, "/desktop/authorize?redirect_uri=" + Uri.EscapeDataString(redirect) + "&code_challenge=" + challenge + "&state=" + state);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(BrowserTimeout);
        try
        {
            var waiting = WaitForCodeAsync(listener, state, timeout.Token); // listening before anything can come back
            await _openBrowser(authorize);
            var code = await waiting;
            using var response = await _http.PostAsJsonAsync(new Uri(origin, "/auth/desktop/token"),
                new DesktopTokenRequest(code, verifier, redirect, Environment.MachineName), ProtocolJsonContext.Default.DesktopTokenRequest, cancellation);
            if (!response.IsSuccessStatusCode) return "The server refused this sign-in.";
            var tokens = (await response.Content.ReadFromJsonAsync(ProtocolJsonContext.Default.DesktopTokens, cancellation))!;
            await _lock.WaitAsync(cancellation);
            try
            {
                await vault.WriteAsync(tokens.RefreshToken, cancellation);
                Directory.CreateDirectory(dataDirectory);
                await File.WriteAllTextAsync(SettingsPath, JsonSerializer.Serialize(new DesktopSettings(origin.GetLeftPart(UriPartial.Authority)),
                    DesktopSettingsContext.Default.DesktopSettings), cancellation);
                Server = origin.GetLeftPart(UriPartial.Authority);
                _tokens = tokens;
                _signedIn = true;
            }
            finally { _lock.Release(); }
            return null;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return "Nothing came back from the browser in time."; }
        catch (HttpRequestException exception) { return "The server could not be reached: " + exception.Message; }
        catch (System.ComponentModel.Win32Exception exception) { return "The browser could not be opened: " + exception.Message; }
    }

    static int FreeLoopbackPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    static async Task<string> WaitForCodeAsync(HttpListener listener, string state, CancellationToken cancellation)
    {
        await using (cancellation.Register(listener.Stop))
            while (true)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); }
                catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    cancellation.ThrowIfCancellationRequested();
                    throw;
                }
                var query = context.Request.QueryString;
                var matches = context.Request.Url?.AbsolutePath == RedirectPath && query["state"] == state && query["code"] is { Length: > 0 };
                context.Response.StatusCode = matches ? 200 : 404;
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.OutputStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes($"<!doctype html><title>{Brand.Name}</title><p>You can close this tab and go back to {Brand.Name}.</p>"), cancellation);
                context.Response.Close();
                if (matches) return query["code"]!;
            }
    }

    /// <summary>A current access token, refreshed when needed; null when nobody is signed in any more.</summary>
    public async Task<DesktopTokens?> AccessTokenAsync(CancellationToken cancellation)
    {
        await _lock.WaitAsync(cancellation);
        try
        {
            if (_tokens is { } current && current.ExpiresAt - DateTime.UtcNow > ExpiryMargin) return current;
            if (Server == null || await vault.ReadAsync(cancellation) is not { } refresh) return null;
            using var response = await _http.PostAsJsonAsync(new Uri(new Uri(Server), "/auth/desktop/refresh"), new DesktopRefreshRequest(refresh),
                ProtocolJsonContext.Default.DesktopRefreshRequest, cancellation);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await vault.DeleteAsync(cancellation);
                _tokens = null;
                _signedIn = false;
                return null;
            }
            response.EnsureSuccessStatusCode();
            _tokens = (await response.Content.ReadFromJsonAsync(ProtocolJsonContext.Default.DesktopTokens, cancellation))!;
            await vault.WriteAsync(_tokens.RefreshToken, cancellation);
            return _tokens;
        }
        finally { _lock.Release(); }
    }

    public async Task SignOutAsync(CancellationToken cancellation)
    {
        await _lock.WaitAsync(cancellation);
        try
        {
            if (Server != null && await vault.ReadAsync(cancellation) is { } refresh)
                try
                {
                    using var _ = await _http.PostAsJsonAsync(new Uri(new Uri(Server), "/auth/desktop/revoke"), new DesktopRefreshRequest(refresh),
                        ProtocolJsonContext.Default.DesktopRefreshRequest, cancellation);
                }
                catch (HttpRequestException) { /* the server is away; the token is forgotten here all the same */ }
            await vault.DeleteAsync(cancellation);
            _tokens = null;
            _signedIn = false;
        }
        finally { _lock.Release(); }
    }

    /// <summary>Downloads a blob of the server into the Downloads folder, under a name not in use yet.</summary>
    public async Task<string?> SaveBlobAsync(string sha256, string name, string directory, CancellationToken cancellation)
    {
        if (Server == null || await AccessTokenAsync(cancellation) is not { } tokens) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(Server), "/api/v1/blobs/" + sha256));
        request.Headers.Authorization = new("Bearer", tokens.AccessToken);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();
        Directory.CreateDirectory(directory);
        var safeName = Path.GetFileName(name);
        var target = Path.Combine(directory, safeName);
        for (var copy = 1; File.Exists(target); copy++)
            target = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(safeName)} ({copy}){Path.GetExtension(safeName)}");
        await using var file = File.Create(target);
        await response.Content.CopyToAsync(file, cancellation);
        return target;
    }

    public void Dispose()
    {
        _http.Dispose();
        _lock.Dispose();
    }
}
