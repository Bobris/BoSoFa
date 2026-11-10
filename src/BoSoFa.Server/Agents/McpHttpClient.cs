using System.Net;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Agents;

/// <summary>An MCP server could not be reached or refused a request.</summary>
public sealed class McpException(string message) : Exception(message);

public sealed record McpRemoteTool(string Name, string Description, JsonObject InputSchema);

/// <summary>What a tool returned: its text, and whether the tool itself reports a failure.</summary>
/// <param name="Images">Images already stored as blobs, as the machine's tools give them.</param>
/// <param name="NewImages">Images' content the loop is yet to store.</param>
public sealed record McpToolResult(string Text, bool IsError, PromptImage[]? Images = null, IReadOnlyList<byte[]>? NewImages = null);

/// <summary>
/// The client side of MCP's Streamable HTTP transport, for catalog servers called from the server: JSON-RPC over POST,
/// answered with one JSON message or a server-sent stream. One instance serves one loop, one call at a time.
/// </summary>
public sealed class McpHttpClient(HttpClient http, string server, Uri url, IReadOnlyDictionary<string, string> headers)
{
    const string SupportedVersion = "2025-11-25";

    string? _sessionId;
    string? _protocolVersion;
    bool _initialized;
    long _nextId;

    public async Task<IReadOnlyList<McpRemoteTool>> ListToolsAsync(CancellationToken cancellation)
    {
        var tools = new List<McpRemoteTool>();
        string? cursor = null;
        do
        {
            var result = await RequestAsync("tools/list", cursor == null ? null : new JsonObject { ["cursor"] = cursor }, cancellation);
            foreach (var tool in (result?["tools"] as JsonArray ?? []).OfType<JsonObject>())
                if (Str(tool["name"]) is { Length: > 0 } name)
                    tools.Add(new(name, Str(tool["description"]) ?? "", tool["inputSchema"]?.DeepClone() as JsonObject ?? new JsonObject { ["type"] = "object" }));
            cursor = Str(result?["nextCursor"]);
        } while (cursor is { Length: > 0 });
        return tools;
    }

    public async Task<McpToolResult> CallToolAsync(string tool, JsonObject arguments, CancellationToken cancellation)
    {
        var result = await RequestAsync("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments.DeepClone() }, cancellation);
        var content = McpResults.Read(result);
        return new(content.Text, content.IsError, NewImages: content.Images);
    }

    async Task<JsonNode?> RequestAsync(string method, JsonObject? parameters, CancellationToken cancellation)
    {
        if (!_initialized) await InitializeAsync(cancellation);
        try { return await SendRequestAsync(method, parameters, cancellation); }
        catch (SessionExpiredException)
        {
            // The server forgot this client: nothing ran, so asking again in a new session is safe.
            _initialized = false;
            await InitializeAsync(cancellation);
            return await SendRequestAsync(method, parameters, cancellation);
        }
    }

    async Task InitializeAsync(CancellationToken cancellation)
    {
        _sessionId = null;
        _protocolVersion = null;
        var result = await SendRequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = SupportedVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = Protocol.Brand.Id, ["version"] = Api.Version }
        }, cancellation);
        _protocolVersion = Str(result?["protocolVersion"]) ?? SupportedVersion;
        using var message = Post(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
        using var response = await SendAsync(message, cancellation);
        if (!response.IsSuccessStatusCode) throw new McpException($"{server}: initialization was refused ({(int)response.StatusCode}).");
        _initialized = true;
    }

    async Task<JsonNode?> SendRequestAsync(string method, JsonObject? parameters, CancellationToken cancellation)
    {
        var id = ++_nextId;
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters != null) body["params"] = parameters.DeepClone();
        using var message = Post(body);
        using var response = await SendAsync(message, cancellation);
        if (response.StatusCode == HttpStatusCode.NotFound && _sessionId != null) throw new SessionExpiredException();
        if (!response.IsSuccessStatusCode) throw new McpException($"{server} answered {(int)response.StatusCode} to {method}.");
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessions)) _sessionId = sessions.FirstOrDefault();
        var answer = response.Content.Headers.ContentType?.MediaType == "text/event-stream"
            ? await ReadStreamAsync(response, id, cancellation)
            : JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation)) as JsonObject;
        if (answer == null) throw new McpException($"{server} gave no answer to {method}.");
        if (answer["error"] is { } error) throw new McpException($"{server}: {Str(error["message"]) ?? "the request failed."}");
        return answer["result"];
    }

    async Task<JsonObject?> ReadStreamAsync(HttpResponseMessage response, long id, CancellationToken cancellation)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        await foreach (var item in SseParser.Create(stream).EnumerateAsync(cancellation))
        {
            if (item.Data.Length == 0) continue;
            JsonObject? message;
            try { message = JsonNode.Parse(item.Data) as JsonObject; }
            catch (JsonException) { continue; }
            // A server may send its own requests and notifications first.
            if (message?["id"] is JsonValue value && value.TryGetValue<long>(out var received) && received == id && message["method"] == null)
                return message;
        }
        return null;
    }

    HttpRequestMessage Post(JsonObject body)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        message.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        message.Headers.TryAddWithoutValidation("User-Agent", ProviderHttp.UserAgent);
        foreach (var (name, value) in headers) message.Headers.TryAddWithoutValidation(name, value);
        if (_sessionId != null) message.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        if (_protocolVersion != null) message.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _protocolVersion);
        return message;
    }

    async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken cancellation)
    {
        try { return await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellation); }
        catch (HttpRequestException problem) { throw new McpException($"{server} could not be reached: {problem.Message}"); }
    }

    static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    sealed class SessionExpiredException : Exception;
}
