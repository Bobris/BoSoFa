using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BoSoFa.Daemon.Harnesses.Claude;
using BoSoFa.Protocol;

namespace BoSoFa.Daemon.Sessions;

/// <summary>Uploads a file of a session to the server's blob store.</summary>
public interface IBlobUploader
{
    Task<BlobRef> UploadAsync(string file, CancellationToken cancellation);
}

/// <summary>
/// The BoSoFa MCP server (plan §5.5): one loopback HTTP endpoint for every session, <c>/mcp/&lt;session&gt;</c>, each with
/// its own bearer. Streamable HTTP with plain JSON responses, which the protocol allows for servers without
/// server-initiated messages.
/// </summary>
public sealed class McpEndpoint : IAsyncDisposable
{
    public const string ServerName = "bosofa";

    /// <summary>Harmless tools that need no approval of their own; <c>bosofa_upload_file</c> shares a file, so it asks.</summary>
    public static readonly string[] PreApprovedClaudeTools = ["mcp__bosofa__bosofa_notify_user", "mcp__bosofa__bosofa_ask_user"];

    readonly HttpListener _listener = new();
    readonly Func<ulong, SessionState?> _sessions;
    readonly ISessionSink _sink;
    readonly IBlobUploader? _uploader;
    readonly ConcurrentDictionary<ulong, string> _tokens = new();
    readonly ConcurrentDictionary<string, (ulong SessionId, TaskCompletionSource<(bool Allow, string? Message)> Answer)> _questions = new();
    readonly Task _serving;

    public McpEndpoint(Func<ulong, SessionState?> sessions, ISessionSink sink, IBlobUploader? uploader)
    {
        _sessions = sessions;
        _sink = sink;
        _uploader = uploader;
        // localhost prefixes need no URL reservation on Windows.
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        _listener.Prefixes.Add($"http://localhost:{Port}/");
        _listener.Start();
        _serving = Task.Run(ServeAsync);
    }

    public int Port { get; }

    /// <summary>The server entry a harness gets for this session.</summary>
    public ResolvedMcpServer ServerFor(ulong sessionId)
    {
        var token = _tokens.GetOrAdd(sessionId, _ => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
        return new(ServerName, McpTransport.Http, null, [], $"http://localhost:{Port}/mcp/{sessionId}",
            new() { ["Authorization"] = "Bearer " + token });
    }

    /// <summary>A person answered a question of <c>bosofa_ask_user</c>; false when the key is not one of ours.</summary>
    public bool TryAnswer(string key, bool allow, string? message)
    {
        if (!_questions.TryRemove(key, out var question)) return false;
        question.Answer.TrySetResult((allow, message));
        _sink.Report(question.SessionId, new StatusReport(SessionStatus.Running));
        return true;
    }

    /// <summary>The run ended: nobody is waiting for these answers any more.</summary>
    public void CancelQuestions(ulong sessionId)
    {
        foreach (var (key, question) in _questions.Where(entry => entry.Value.SessionId == sessionId).ToList())
            if (_questions.TryRemove(key, out _))
            {
                question.Answer.TrySetResult((false, "The turn ended before anyone answered."));
                _sink.Report(sessionId, new ApprovalClosedReport(key, "turn ended"));
            }
    }

    async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    async Task HandleAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            var segments = context.Request.Url!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments is not ["mcp", var id] || !ulong.TryParse(id, out var sessionId) || !_tokens.TryGetValue(sessionId, out var token) ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(context.Request.Headers["Authorization"] ?? ""),
                    Encoding.UTF8.GetBytes("Bearer " + token)))
            {
                response.StatusCode = 401;
                return;
            }
            if (context.Request.HttpMethod != "POST")
            {
                response.StatusCode = 405; // no server-initiated stream
                return;
            }
            using var document = await JsonDocument.ParseAsync(context.Request.InputStream);
            var message = document.RootElement;
            if (!message.TryGetProperty("id", out var requestId) || requestId.ValueKind == JsonValueKind.Null)
            {
                response.StatusCode = 202; // a notification
                return;
            }
            var result = await AnswerAsync(sessionId, ClaudeJson.Str(message, "method") ?? "",
                message.TryGetProperty("params", out var parameters) ? parameters : default);
            var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(requestId.GetRawText()) };
            if (result != null) body["result"] = result;
            else body["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found." };
            var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
        }
        catch (Exception exception) when (exception is JsonException or HttpListenerException or IOException or ObjectDisposedException)
        {
            try { response.StatusCode = 400; }
            catch (ObjectDisposedException) { }
        }
        finally
        {
            try { response.Close(); }
            catch (ObjectDisposedException) { }
        }
    }

    async Task<JsonNode?> AnswerAsync(ulong sessionId, string method, JsonElement parameters)
    {
        switch (method)
        {
            case "initialize":
                return new JsonObject
                {
                    ["protocolVersion"] = ClaudeJson.Str(parameters, "protocolVersion") ?? "2025-06-18",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = Cli.Version }
                };
            case "ping":
                return new JsonObject();
            case "tools/list":
                return new JsonObject
                {
                    ["tools"] = new JsonArray(
                        Tool("bosofa_notify_user", "Show a short note to the people following this session, without waiting for them.", "message", "The note."),
                        Tool("bosofa_ask_user", "Ask the people following this session a yes-or-no question and wait for the answer.", "question", "The question."),
                        Tool("bosofa_upload_file", "Upload a file from the session's working directory so people can download it.", "path", "The file's path."))
                };
            case "tools/call":
                var arguments = ClaudeJson.Obj(parameters, "arguments") ?? default;
                return await CallAsync(sessionId, ClaudeJson.Str(parameters, "name") ?? "", arguments);
            default:
                return null;
        }
    }

    static JsonObject Tool(string name, string description, string argument, string argumentDescription) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { [argument] = new JsonObject { ["type"] = "string", ["description"] = argumentDescription } },
            ["required"] = new JsonArray(JsonValue.Create(argument))
        }
    };

    static JsonObject ToolResult(string text, bool isError = false) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError
    };

    async Task<JsonNode> CallAsync(ulong sessionId, string tool, JsonElement arguments)
    {
        switch (tool)
        {
            case "bosofa_notify_user" when ClaudeJson.Str(arguments, "message") is { Length: > 0 } message:
                _sink.Report(sessionId, new ItemReport(SessionItemKind.Notice, DateTime.UtcNow, message));
                return ToolResult("Shown.");
            case "bosofa_ask_user" when ClaudeJson.Str(arguments, "question") is { Length: > 0 } question:
                var key = "ask-" + Guid.NewGuid().ToString("N");
                var answer = new TaskCompletionSource<(bool, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
                _questions[key] = (sessionId, answer);
                _sink.Report(sessionId, new ApprovalRequestedReport(key, DateTime.UtcNow, "bosofa_ask_user", null,
                    new JsonObject { ["question"] = question }.ToJsonString(), question));
                var (allow, comment) = await answer.Task;
                return ToolResult((allow ? "The person answered yes." : "The person answered no.") +
                                  (string.IsNullOrWhiteSpace(comment) ? "" : " They added: " + comment));
            case "bosofa_upload_file" when ClaudeJson.Str(arguments, "path") is { Length: > 0 } path:
                return await UploadAsync(sessionId, path);
            default:
                return ToolResult($"Unknown tool or missing argument: {tool}.", isError: true);
        }
    }

    async Task<JsonNode> UploadAsync(ulong sessionId, string path)
    {
        if (_uploader == null || _sessions(sessionId) is not { } state) return ToolResult("Uploading is not available.", isError: true);
        var file = PathGuard.Resolve([state.WorkingDirectory], path);
        if (file == null || !File.Exists(file)) return ToolResult("Only existing files in the working directory can be uploaded.", isError: true);
        try
        {
            var blob = await _uploader.UploadAsync(file, CancellationToken.None);
            var name = Path.GetFileName(file);
            _sink.Report(sessionId, new ItemReport(SessionItemKind.Notice, DateTime.UtcNow, $"Uploaded {name} ({blob.Size} bytes).",
                Data: new JsonObject { ["blob"] = blob.Sha256, ["fileName"] = name, ["size"] = blob.Size }.ToJsonString()));
            return ToolResult($"Uploaded {name}.");
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or UnauthorizedAccessException)
        {
            return ToolResult("The upload failed: " + exception.Message, isError: true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Close();
        await _serving;
    }
}
