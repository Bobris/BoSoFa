using System.Text.Json;
using ClaudeStreamSpike;

// Usage:
//   claude-stream-spike allow                     new session, Write permission answered "allow"
//   claude-stream-spike deny                      new session, Write permission answered "deny"
//   claude-stream-spike resume <session-id>       --resume, SDK MCP tool call, interrupt mid-turn, one more turn
// Options: --claude <path> --work <dir> --model <alias>
var scenario = args.Length > 0 ? args[0] : "allow";
string? resumeId = scenario == "resume" && args.Length > 1 ? args[1] : null;
var claude = Opt("--claude") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/bin/claude");
var work = Path.GetFullPath(Opt("--work") ?? "artifacts/spikes/claude-work");
var model = Opt("--model") ?? "haiku";
var logs = Path.Combine(work, "..", "claude-logs");
Directory.CreateDirectory(work);
Directory.CreateDirectory(logs);
var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
var logPath = Path.GetFullPath(Path.Combine(logs, $"{scenario}-{stamp}.log"));

var arguments = new List<string>
{
    "-p",
    "--input-format", "stream-json",
    "--output-format", "stream-json",
    "--verbose", // required by -p + stream-json output
    "--include-partial-messages", // stream_event lines (Anthropic SSE events)
    "--replay-user-messages", // echoes our user lines back (ack + uuid)
    "--permission-prompt-tool", "stdio", // permission prompts arrive as control_request can_use_tool
    // The user's settings say bypassPermissions; a host must state the mode or it inherits that silently.
    "--permission-mode", "default",
    // Keep the spike cheap and deterministic: no user settings (plugins, hooks, default mode), only two built-in tools.
    "--setting-sources", "project,local",
    "--strict-mcp-config",
    "--mcp-config", """{"mcpServers":{"bosofa":{"type":"sdk","name":"bosofa"}}}""",
    "--allowedTools=mcp__bosofa__ping",
    "--tools", "Read,Write",
    "--model", model,
};
if (resumeId is not null) arguments.Add($"--resume={resumeId}");

var environment = new Dictionary<string, string>(StringComparer.Ordinal);
foreach (var name in new[] { "HOME", "USER", "LOGNAME", "SHELL", "TMPDIR", "LANG", "TERM" })
    if (Environment.GetEnvironmentVariable(name) is { } v) environment[name] = v;
environment["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin:" + Path.GetDirectoryName(claude);
environment["CLAUDE_CODE_EMIT_SESSION_STATE_EVENTS"] = "1"; // system/session_state_changed: idle|running|requires_action
environment["CLAUDE_AGENT_SDK_CLIENT_APP"] = "bosofa-spike/0.1";
environment["DISABLE_AUTOUPDATER"] = "1";
// Per-profile isolation would add: environment["CLAUDE_CONFIG_DIR"] = "<data>/profiles/<id>/claude";

var kinds = new SortedDictionary<string, (int Count, string Sample)>(StringComparer.Ordinal);
var permissionAnswer = scenario == "deny" ? "deny" : "allow";

ClaudeProcess? self = null; // control requests only arrive after initialize, by then it is set
await using var cli = ClaudeProcess.Start(claude, arguments, work, environment, logPath, HandleControlRequestAsync, Record);
self = cli;
Console.WriteLine($"[spike] scenario={scenario} log={logPath}");

// 1. initialize: registers hook callbacks and SDK MCP servers; the response lists commands, models, account...
var init = await cli.RequestAsync(
    new InitializeRequest(
        Hooks: new() { ["PreToolUse"] = [new HookMatcher(null, ["bsf_pretool"], 30)] },
        SdkMcpServers: ["bosofa"],
        AppendSystemPrompt: "You are driven by an automated test harness. Keep every answer very short."),
    SpikeJson.Default.ControlRequestLineInitializeRequest,
    TimeSpan.FromSeconds(60));
Console.WriteLine($"[spike] initialized: keys={string.Join(',', init.EnumerateObject().Select(p => p.Name))}");

string? sessionId = resumeId;
switch (scenario)
{
    case "allow":
        await TurnAsync("Use the Write tool to create the file allowed.txt in the current directory with the content: hello from bosofa. Then reply: done.");
        Console.WriteLine($"[spike] allowed.txt exists={File.Exists(Path.Combine(work, "allowed.txt"))}");
        break;
    case "deny":
        await TurnAsync("Use the Write tool to create the file denied.txt in the current directory with the content: should not exist. Then reply: done.");
        Console.WriteLine($"[spike] denied.txt exists={File.Exists(Path.Combine(work, "denied.txt"))}");
        break;
    case "resume":
        await TurnAsync("Call the mcp__bosofa__ping tool once. Then, in one short sentence, name the file you created earlier in this conversation.");
        await TurnAsync("Write the numbers from one to three hundred as English words, comma separated, no other text.", interruptAfterFirstDelta: true);
        await TurnAsync("Reply with just: OK");
        break;
}

Console.WriteLine("[spike] distinct inbound kinds:");
foreach (var (kind, (count, _)) in kinds) Console.WriteLine($"  {count,4}  {kind}");
var kindsPath = Path.ChangeExtension(logPath, ".kinds.txt");
await File.WriteAllLinesAsync(kindsPath, kinds.Select(k => $"{k.Key}\t{k.Value.Count}\t{k.Value.Sample}"));
Console.WriteLine($"[spike] kinds -> {kindsPath}");
return 0;

async Task TurnAsync(string text, bool interruptAfterFirstDelta = false)
{
    Console.WriteLine($"\n[spike] >>> {text}");
    await cli.SendAsync(new UserLine(sessionId ?? "", new UserMessageBody(text)), SpikeJson.Default.UserLine);
    var interrupted = false;
    var deltas = 0;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));
    await foreach (var msg in cli.Messages.ReadAllAsync(timeout.Token))
    {
        var type = ClaudeProcess.Str(msg, "type");
        if (type == "system" && ClaudeProcess.Str(msg, "subtype") == "init")
        {
            sessionId = ClaudeProcess.Str(msg, "session_id");
            Console.WriteLine($"[spike] system/init session_id={sessionId} model={ClaudeProcess.Str(msg, "model")} permissionMode={ClaudeProcess.Str(msg, "permissionMode")}");
        }
        else if (type == "stream_event" && msg.GetProperty("event") is var ev && ClaudeProcess.Str(ev, "type") == "content_block_delta"
                 && ev.GetProperty("delta") is var d && ClaudeProcess.Str(d, "type") == "text_delta")
        {
            deltas++;
            Console.Write(ClaudeProcess.Str(d, "text"));
            if (interruptAfterFirstDelta && !interrupted && deltas >= 2)
            {
                interrupted = true;
                Console.WriteLine("\n[spike] --- sending interrupt");
                var r = await cli.RequestAsync(new InterruptRequest(), SpikeJson.Default.ControlRequestLineInterruptRequest, TimeSpan.FromSeconds(30));
                Console.WriteLine($"[spike] interrupt acknowledged: {r.GetRawText()}");
            }
        }
        else if (type == "result")
        {
            var result = msg.Deserialize(SpikeJson.Default.ResultLine)!;
            sessionId = result.SessionId;
            Console.WriteLine($"\n[spike] result subtype={result.Subtype} is_error={result.IsError} turns={result.NumTurns} " +
                              $"cost=${result.TotalCostUsd:0.000000} in={result.Usage?.InputTokens} out={result.Usage?.OutputTokens} " +
                              $"cache_read={result.Usage?.CacheReadInputTokens} cache_write={result.Usage?.CacheCreationInputTokens} " +
                              $"session={result.SessionId} denials={result.PermissionDenials?.GetArrayLength()} deltas={deltas}");
            return;
        }
    }

    throw new InvalidOperationException("stream ended before a result");
}

async Task HandleControlRequestAsync(string requestId, string subtype, JsonElement request)
{
    var c = self!;
    switch (subtype)
    {
        case "can_use_tool":
            Console.WriteLine($"\n[spike] can_use_tool {ClaudeProcess.Str(request, "tool_name")} -> {permissionAnswer}");
            var answer = permissionAnswer == "allow"
                ? new PermissionResult("allow", UpdatedInput: request.GetProperty("input"))
                : new PermissionResult("deny", Message: "The BoSoFa spike denied this write on purpose. Do not retry; just reply: denied.");
            await c.RespondAsync(requestId, JsonSerializer.SerializeToElement(answer, SpikeJson.Default.PermissionResult));
            break;
        case "hook_callback":
            // An empty response means "no opinion": the CLI's own permission logic continues (and may prompt).
            await c.RespondAsync(requestId, null);
            break;
        case "mcp_message":
            await c.RespondAsync(requestId, HandleMcp(request.GetProperty("message")));
            break;
        default:
            await c.RespondErrorAsync(requestId, $"bosofa spike does not handle '{subtype}'");
            break;
    }
}

JsonElement HandleMcp(JsonElement message)
{
    var method = ClaudeProcess.Str(message, "method");
    if (!message.TryGetProperty("id", out var id))
    {
        // A notification (notifications/initialized): answer with an empty JSON-RPC result, as the TS SDK does.
        return JsonSerializer.SerializeToElement(new McpMessageResult(new JsonRpcResponse(null, ClaudeProcess.Empty, null)), SpikeJson.Default.McpMessageResult);
    }

    JsonElement? result = method switch
    {
        "initialize" => JsonSerializer.SerializeToElement(
            new McpInitializeResult(
                message.GetProperty("params").TryGetProperty("protocolVersion", out var pv) ? pv.GetString()! : "2025-06-18",
                new McpCapabilities(new McpToolsCapability(false)), new McpServerInfo("bosofa", "0.1")),
            McpJson.Default.McpInitializeResult),
        "tools/list" => JsonSerializer.SerializeToElement(
            new McpToolsList([new McpTool("ping", "Returns pong. Used by the BoSoFa spike.", JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement)]),
            McpJson.Default.McpToolsList),
        "tools/call" => JsonSerializer.SerializeToElement(new McpCallResult([new McpContent("text", "pong from bosofad")], false), McpJson.Default.McpCallResult),
        "ping" => ClaudeProcess.Empty,
        _ => null,
    };
    var response = result is null
        ? new JsonRpcResponse(id, null, new JsonRpcError(-32601, $"Method '{method}' not supported"))
        : new JsonRpcResponse(id, result, null);
    return JsonSerializer.SerializeToElement(new McpMessageResult(response), SpikeJson.Default.McpMessageResult);
}

void Record(JsonElement root)
{
    var type = ClaudeProcess.Str(root, "type") ?? "?";
    var key = type switch
    {
        "stream_event" => $"stream_event/{ClaudeProcess.Str(root.GetProperty("event"), "type")}" +
                          (root.GetProperty("event").TryGetProperty("delta", out var dl) && ClaudeProcess.Str(dl, "type") is { } dt ? $"/{dt}" : "") +
                          (root.GetProperty("event").TryGetProperty("content_block", out var cb) && ClaudeProcess.Str(cb, "type") is { } ct ? $"/{ct}" : ""),
        "control_request" => $"control_request/{ClaudeProcess.Str(root.GetProperty("request"), "subtype")}",
        "control_response" => $"control_response/{ClaudeProcess.Str(root.GetProperty("response"), "subtype")}",
        "assistant" or "user" => $"{type}/{string.Join('+', ContentTypes(root))}",
        _ => ClaudeProcess.Str(root, "subtype") is { } st ? $"{type}/{st}" : type,
    };
    lock (kinds)
    {
        kinds[key] = kinds.TryGetValue(key, out var e) ? (e.Count + 1, e.Sample) : (1, root.GetRawText());
    }

    static IEnumerable<string> ContentTypes(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var m) || !m.TryGetProperty("content", out var c)) return ["?"];
        if (c.ValueKind == JsonValueKind.String) return ["string"];
        return c.EnumerateArray().Select(b => ClaudeProcess.Str(b, "type") ?? "?").Distinct();
    }
}

string? Opt(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
