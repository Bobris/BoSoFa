using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BoSoFa.Daemon.Harnesses;
using BoSoFa.Daemon.Tools;
using BoSoFa.Protocol;

namespace BoSoFa.Daemon.Sessions;

/// <summary>
/// The MCP servers of a harness session's profile, served by the daemon at <c>/mcp/&lt;session&gt;/&lt;server&gt;</c> of
/// <see cref="McpEndpoint"/>. A harness (Claude Code, Codex, an ACP agent) gets only that loopback address and the session's
/// bearer, never a server's environment or headers: the daemon starts a stdio server itself and relays its JSON-RPC, and calls a
/// remote one with its headers, passing the Streamable HTTP exchange through. So a secret of an MCP server is neither on the
/// harness's command line, nor in its configuration, nor in the environment of the agent's shell.
/// </summary>
/// <remarks>
/// A stdio server is still a process of this user, whose environment the same user can read; a remote server's headers stay
/// in the daemon's memory. Requests a server sends to its client (sampling, elicitation, roots) are refused, as for server
/// agent sessions, and a harness's <c>notifications/cancelled</c> is not passed on, since the relay numbers requests anew.
/// </remarks>
/// <param name="tokens">Where the bearers of SkillShare servers come from (<see cref="ResolvedMcpServer.SkillShareProvider"/>): asked for when
/// first needed, again 5 minutes before they expire, and once more when the server refuses one.</param>
/// <param name="masker">Takes each such bearer, so a harness that prints one never reports it.</param>
sealed class McpGateway : IAsyncDisposable
{
    static readonly TimeSpan CallTimeout = TimeSpan.FromHours(1), RequestTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Request headers of the Streamable HTTP transport that go on to a remote server; the harness's bearer does not.</summary>
    static readonly string[] Forwarded = ["Accept", "Mcp-Session-Id", "MCP-Protocol-Version", "Last-Event-ID"];

    sealed record Registration(string WorkingDirectory, IReadOnlyDictionary<string, ResolvedMcpServer> Servers);

    readonly ConcurrentDictionary<ulong, Registration> _sessions = new();
    readonly StdioMcpServers _stdio = new(new ToolImages(null));
    readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    readonly CancellationTokenSource _stopping = new();
    readonly ConcurrentDictionary<(ulong SessionId, string Provider), SkillShareTokenResponse> _bearers = new();
    readonly IProfileSource? _tokens;
    readonly SecretMasker? _masker;
    readonly Task _idle;

    public McpGateway(IProfileSource? tokens = null, SecretMasker? masker = null)
    {
        _tokens = tokens;
        _masker = masker;
        _idle = StopIdleAsync();
    }

    /// <summary>Serves these servers for the session from now on, in place of what it had (its profile may have changed).</summary>
    public void Register(ulong sessionId, string workingDirectory, IEnumerable<ResolvedMcpServer> servers)
    {
        _sessions[sessionId] = new(workingDirectory, servers.ToDictionary(server => server.Name, StringComparer.Ordinal));
        foreach (var key in _bearers.Keys.Where(key => key.SessionId == sessionId)) _bearers.TryRemove(key, out _);
    }

    /// <summary>Serves one request of the session's harness to one of its servers. Writes the whole response, errors included.</summary>
    public async Task HandleAsync(HttpListenerContext context, ulong sessionId, string name)
    {
        var response = context.Response;
        if (!_sessions.TryGetValue(sessionId, out var registration) || !registration.Servers.TryGetValue(name, out var server))
        {
            response.StatusCode = 404;
            return;
        }
        if (server.Transport == McpTransport.Http) await ProxyAsync(context, sessionId, server);
        else await RelayAsync(context, sessionId, registration.WorkingDirectory, server);
    }

    async Task RelayAsync(HttpListenerContext context, ulong sessionId, string workingDirectory, ResolvedMcpServer server)
    {
        var response = context.Response;
        if (context.Request.HttpMethod != "POST")
        {
            response.StatusCode = 405; // no server-initiated stream
            return;
        }
        JsonObject? message;
        try { message = await JsonNode.ParseAsync(context.Request.InputStream) as JsonObject; }
        catch (JsonException) { message = null; }
        if (message?["method"] is not JsonValue methodValue || !methodValue.TryGetValue<string>(out var method))
        {
            response.StatusCode = 400;
            return;
        }
        var parameters = message["params"]?.DeepClone();
        if (message["id"] is not { } id)
        {
            // The daemon already told it that it is initialized; a cancellation would name an id the server never saw.
            if (method is not ("notifications/initialized" or "notifications/cancelled"))
            {
                try { await _stdio.NotifyAsync(sessionId, workingDirectory, server, method, parameters, _stopping.Token); }
                catch (Exception exception) when (exception is ToolProblem or IOException or JsonRpcException) { }
            }
            response.StatusCode = 202;
            return;
        }
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone() };
        if (method == "server/discover")
        {
            // The probe of the stateless protocol (2026-07-28): the daemon holds an initialized session with the server, so the
            // harness falls back to initialize. Passed on, a server that ignores it would leave the harness waiting.
            body["error"] = Error(-32601, "Method not found.");
            await WriteAsync(response, body);
            return;
        }
        try
        {
            var result = await _stdio.RelayAsync(sessionId, workingDirectory, server, method, parameters,
                method == "tools/call" ? CallTimeout : RequestTimeout, _stopping.Token);
            body["result"] = result.ValueKind == JsonValueKind.Undefined ? new JsonObject() : JsonNode.Parse(result.GetRawText());
        }
        catch (JsonRpcException exception) { body["error"] = Error(exception.Code, exception.Message); }
        catch (ToolProblem exception) { body["error"] = Error(-32603, exception.Message); }
        catch (TimeoutException) { body["error"] = Error(-32603, $"The MCP server {server.Name} did not answer in time."); }
        catch (IOException) { body["error"] = Error(-32603, $"The MCP server {server.Name} exited."); }
        await WriteAsync(response, body);
    }

    static async Task WriteAsync(HttpListenerResponse response, JsonObject body)
    {
        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    static JsonObject Error(int code, string message) => new() { ["code"] = code, ["message"] = message };

    /// <summary>The exchange as it is, the server's own headers in place of the session's bearer, its event streams copied as they come.</summary>
    async Task ProxyAsync(HttpListenerContext context, ulong sessionId, ResolvedMcpServer server)
    {
        var request = context.Request;
        var response = context.Response;
        byte[]? body = null;
        if (request.HasEntityBody)
        {
            using var content = new MemoryStream();
            await request.InputStream.CopyToAsync(content);
            body = content.ToArray();
        }
        HttpResponseMessage upstream;
        try
        {
            upstream = await SendAsync(request, sessionId, server, body, fresh: false);
            if (upstream.StatusCode == HttpStatusCode.Unauthorized && server.SkillShareProvider != null)
            {
                upstream.Dispose();
                upstream = await SendAsync(request, sessionId, server, body, fresh: true);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            response.StatusCode = 502;
            return;
        }
        catch (Exception exception) when (exception is NotSupportedException or IOException or InvalidOperationException)
        {
            // No SkillShare token to be had (the person's sign-in was revoked, say): the server is unavailable, never an OAuth challenge.
            response.StatusCode = 502;
            var bytes = Encoding.UTF8.GetBytes($"The MCP server {server.Name} is not available: {exception.Message}");
            response.ContentType = "text/plain; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
            return;
        }
        using (upstream)
        {
            // A 401 stays a 401, without the server's WWW-Authenticate: the harness must not start an OAuth sign-in against the daemon.
            response.StatusCode = (int)upstream.StatusCode;
            if (upstream.Content.Headers.ContentType is { } type) response.ContentType = type.ToString();
            if (upstream.Headers.TryGetValues("Mcp-Session-Id", out var sessions)) response.Headers["Mcp-Session-Id"] = sessions.First();
            if (upstream.Content.Headers.ContentLength is { } length) response.ContentLength64 = length;
            else response.SendChunked = true;
            try
            {
                await using var from = await upstream.Content.ReadAsStreamAsync(_stopping.Token);
                var buffer = new byte[16 * 1024];
                int read;
                while ((read = await from.ReadAsync(buffer, _stopping.Token)) > 0)
                {
                    await response.OutputStream.WriteAsync(buffer.AsMemory(0, read), _stopping.Token);
                    await response.OutputStream.FlushAsync(_stopping.Token);
                }
            }
            // The harness went away, the server ended its stream, or the daemon stops: the exchange is over either way.
            catch (Exception exception) when (exception is IOException or HttpListenerException or HttpRequestException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    async Task<HttpResponseMessage> SendAsync(HttpListenerRequest request, ulong sessionId, ResolvedMcpServer server, byte[]? body, bool fresh)
    {
        using var message = new HttpRequestMessage(new HttpMethod(request.HttpMethod), server.Url);
        if (body != null)
        {
            message.Content = new ByteArrayContent(body);
            message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType ?? "application/json");
        }
        foreach (var name in Forwarded)
            if (request.Headers[name] is { } value) message.Headers.TryAddWithoutValidation(name, value);
        message.Headers.TryAddWithoutValidation("User-Agent", $"{Brand.Id}/{Cli.Version}");
        foreach (var (name, value) in server.Secrets)
        {
            message.Headers.Remove(name);
            message.Headers.TryAddWithoutValidation(name, value);
        }
        if (server.SkillShareProvider is { } provider)
        {
            message.Headers.Remove("Authorization");
            message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + await BearerAsync(sessionId, provider, fresh));
        }
        return await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, _stopping.Token);
    }

    async Task<string> BearerAsync(ulong sessionId, string provider, bool fresh)
    {
        if (!fresh && _bearers.TryGetValue((sessionId, provider), out var known) && known.ExpiresAt - TimeSpan.FromMinutes(5) > DateTime.UtcNow)
            return known.AccessToken;
        if (_tokens == null) throw new NotSupportedException("This daemon cannot get SkillShare tokens.");
        SkillShareTokenResponse issued;
        try { issued = await _tokens.GetSkillShareTokenAsync(sessionId, provider, fresh, _stopping.Token); }
        catch (HttpRequestException refused) when (refused.StatusCode != null) { throw new InvalidOperationException(refused.Message); }
        _masker?.Retain(issued.AccessToken);
        _bearers[(sessionId, provider)] = issued;
        return issued.AccessToken;
    }

    async Task StopIdleAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_stopping.Token)) await _stdio.StopIdleAsync();
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        await _idle;
        await _stdio.DisposeAsync();
        _http.Dispose();
        _stopping.Dispose();
    }
}
