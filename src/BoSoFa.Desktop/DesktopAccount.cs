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

    /// <summary>The origin a person meant: <c>https://</c> when they typed no scheme, except for this computer.</summary>
    public static Uri? OriginOf(string server)
    {
        var text = server.Trim().TrimEnd('/');
        if (text.Length == 0) return null;
        if (!text.Contains("://", StringComparison.Ordinal))
            text = (text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || text.StartsWith("127.", StringComparison.Ordinal) ? "http://" : "https://") + text;
        return Uri.TryCreate(text, UriKind.Absolute, out var origin) && origin.Scheme is "https" or "http" && origin.Host.Length > 0
            ? new Uri(origin.GetLeftPart(UriPartial.Authority))
            : null;
    }

    /// <summary>A sign-in waiting for the browser: asked again for the same server, the app shows its page again instead of
    /// starting over, so approving in a tab opened earlier still works.</summary>
    sealed class Pending(Uri origin, Uri authorize, CancellationTokenSource cancel)
    {
        public Uri Origin { get; } = origin;
        public Uri Authorize { get; } = authorize;
        public CancellationTokenSource Cancel { get; } = cancel;
        public Task<string?> Result { get; set; } = Task.FromResult<string?>(null);
        public volatile bool Replaced;
    }

    readonly object _pendingLock = new();
    Pending? _pending;

    /// <summary>Null when it worked, else what to tell the person. A sign-in for another server replaces one that still waits for
    /// the browser, so a wrong address never holds the app up.</summary>
    public async Task<string?> SignInAsync(string server, CancellationToken cancellation)
    {
        if (OriginOf(server) is not { } origin) return "That is not a server address.";
        Pending? waiting, replaced = null;
        lock (_pendingLock)
        {
            waiting = _pending is { Result.IsCompleted: false } current && current.Origin == origin ? current : null;
            if (waiting == null) (replaced, _pending) = (_pending, null);
        }
        if (waiting != null)
        {
            await _openBrowser(waiting.Authorize);
            return await waiting.Result.WaitAsync(cancellation);
        }
        if (replaced != null)
            try
            {
                replaced.Replaced = true;
                await replaced.Cancel.CancelAsync();
            }
            catch (ObjectDisposedException) { /* it ended meanwhile */ }
        if (await ProblemWithAsync(origin, cancellation) is { } problem) return problem;

        using var attempt = new CancellationTokenSource(BrowserTimeout);
        using var listener = new HttpListener();
        var port = FreeLoopbackPort();
        var redirect = $"http://127.0.0.1:{port}{RedirectPath}";
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var verifier = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var state = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var authorize = new Uri(origin, "/desktop/authorize?redirect_uri=" + Uri.EscapeDataString(redirect) + "&code_challenge=" + challenge + "&state=" + state);
        var pending = new Pending(origin, authorize, attempt);
        var result = pending.Result = CompleteAsync(pending, listener, state, verifier, redirect);
        lock (_pendingLock) _pending = pending;
        try
        {
            await _openBrowser(authorize);
            return await result.WaitAsync(cancellation);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            await attempt.CancelAsync();
            return "The browser could not be opened: " + exception.Message;
        }
        finally
        {
            try { await result; } catch (Exception) { /* told above */ }
            lock (_pendingLock)
                if (_pending == pending) _pending = null;
        }
    }

    /// <summary>Waits for the browser's code and exchanges it for tokens. A newer sign-in elsewhere or the browser timeout ends it.</summary>
    async Task<string?> CompleteAsync(Pending pending, HttpListener listener, string state, string verifier, string redirect)
    {
        var (origin, attempt) = (pending.Origin, pending.Cancel);
        try
        {
            var code = await WaitForCodeAsync(listener, state, attempt.Token);
            using var response = await _http.PostAsJsonAsync(new Uri(origin, "/auth/desktop/token"),
                new DesktopTokenRequest(code, verifier, redirect, Environment.MachineName), ProtocolJsonContext.Default.DesktopTokenRequest, attempt.Token);
            if (!response.IsSuccessStatusCode) return "The server refused this sign-in.";
            var tokens = (await response.Content.ReadFromJsonAsync(ProtocolJsonContext.Default.DesktopTokens, attempt.Token))!;
            await _lock.WaitAsync(CancellationToken.None);
            try
            {
                await vault.WriteAsync(tokens.RefreshToken, CancellationToken.None);
                Directory.CreateDirectory(dataDirectory);
                await File.WriteAllTextAsync(SettingsPath, JsonSerializer.Serialize(new DesktopSettings(origin.GetLeftPart(UriPartial.Authority)),
                    DesktopSettingsContext.Default.DesktopSettings), CancellationToken.None);
                Server = origin.GetLeftPart(UriPartial.Authority);
                _tokens = tokens;
                _signedIn = true;
            }
            finally { _lock.Release(); }
            return null;
        }
        catch (OperationCanceledException) { return pending.Replaced ? "A newer sign-in took over." : "Nothing came back from the browser in time."; }
        catch (HttpRequestException exception) { return "The server could not be reached: " + exception.Message; }
    }

    /// <summary>Why this is not a server to sign in to, asked before the browser opens; null when it answers as one.</summary>
    async Task<string?> ProblemWithAsync(Uri origin, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var response = await _http.GetAsync(new Uri(origin, "/api/v1/branding"), timeout.Token);
            if (!response.IsSuccessStatusCode) return $"{origin.Authority} answers, but not as a {Brand.Name} server ({(int)response.StatusCode}).";
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("name", out _)
                ? null
                : $"{origin.Authority} answers, but not as a {Brand.Name} server.";
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return $"{origin.Authority} did not answer."; }
        catch (HttpRequestException exception) { return $"{origin.Authority} could not be reached: {exception.Message}"; }
        catch (JsonException) { return $"{origin.Authority} answers, but not as a {Brand.Name} server."; }
    }

    static int FreeLoopbackPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    static async Task<string> WaitForCodeAsync(HttpListener listener, string state, CancellationToken cancellation)
    {
        // Close, not Stop: on Unix a stopped listener's Dispose binds its port again to let go of it, which fails once another
        // process took the port meanwhile ("Address already in use").
        await using (cancellation.Register(listener.Close))
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
