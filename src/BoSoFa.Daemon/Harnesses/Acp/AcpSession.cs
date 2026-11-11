using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using BoSoFa.Daemon.Harnesses.Claude;
using BoSoFa.Daemon.Sessions;
using BoSoFa.Protocol;

namespace BoSoFa.Daemon.Harnesses.Acp;

/// <summary>A started ACP agent and the MCP servers its sessions get.</summary>
public sealed record AcpLaunch(JsonRpcProcess Rpc, JsonArray McpServers);

/// <summary>
/// One BoSoFa session on an Agent Client Protocol agent (OpenCode today): JSON-RPC 2.0 over stdio, one long
/// <c>session/prompt</c> per turn, its output as <c>session/update</c> notifications, permission prompts as
/// <c>session/request_permission</c>. ACP has no queue or steering, so queued messages wait here and steering cancels
/// the running turn first. A later process continues with <c>session/load</c> when the agent supports it.
/// </summary>
public sealed class AcpSession(SessionState state, ISessionSink sink, Func<Task<AcpLaunch>> launch, TextWriter log)
    : IHarnessSession
{
    static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(2);
    static readonly TimeSpan TurnTimeout = TimeSpan.FromHours(4);

    readonly Channel<HarnessPrompt> _work = Channel.CreateUnbounded<HarnessPrompt>();
    readonly CancellationTokenSource _stop = new();
    readonly object _lock = new();
    readonly Queue<HarnessPrompt> _queued = new();
    readonly ConcurrentDictionary<string, TaskCompletionSource<bool?>> _approvals = new();
    readonly HashSet<string> _finishedTools = [];
    readonly StringBuilder _message = new(), _thought = new();
    Task? _worker;
    JsonRpcProcess? _rpc;
    Task? _pump;
    JsonArray _mcpServers = new();
    bool _ready, _turnActive, _loading, _acceptsImages;
    int _turn, _block;

    public SessionState State => state;

    string MessageRef => $"acp-{_turn}-{_block}";

    public async Task SendAsync(HarnessPrompt prompt, DispatchMode mode)
    {
        lock (_lock)
        {
            _worker ??= Task.Run(WorkAsync);
            if (_turnActive)
            {
                if (mode != DispatchMode.Steer)
                {
                    _queued.Enqueue(prompt);
                    return;
                }
                // Steering puts the message first and ends the running turn; it starts when that turn has ended.
                var waiting = _queued.ToArray();
                _queued.Clear();
                _queued.Enqueue(prompt);
                foreach (var message in waiting) _queued.Enqueue(message);
            }
            else
            {
                _work.Writer.TryWrite(prompt);
                return;
            }
        }
        await InterruptAsync(clearQueue: false);
    }

    async Task WorkAsync()
    {
        try
        {
            await foreach (var prompt in _work.Reader.ReadAllAsync(_stop.Token))
                try
                {
                    if (ReferenceEquals(prompt, HarnessPrompt.Clear)) await StartOverAsync();
                    else if (await EnsureSessionAsync()) await RunTurnAsync(prompt);
                }
                catch (Exception exception) when (exception is IOException or JsonRpcException or TimeoutException or
                                                      InvalidOperationException)
                {
                    lock (_lock) _turnActive = false;
                    sink.Report(state.SessionId, new StatusReport(SessionStatus.Idle, Error: $"The agent failed: {exception.Message}"));
                }
        }
        catch (OperationCanceledException) { }
    }

    async Task<bool> EnsureSessionAsync()
    {
        if (_ready && _rpc != null) return true;
        if (!Directory.Exists(state.WorkingDirectory))
        {
            sink.Report(state.SessionId, new StatusReport(SessionStatus.Failed,
                Error: $"The working directory {state.WorkingDirectory} does not exist on this machine."));
            return false;
        }
        JsonRpcProcess rpc;
        try
        {
            var started = await launch();
            rpc = started.Rpc;
            _mcpServers = started.McpServers;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or HttpRequestException or
                                              System.ComponentModel.Win32Exception)
        {
            sink.Report(state.SessionId, new StatusReport(SessionStatus.Failed, Error: $"The agent did not start: {exception.Message}"));
            return false;
        }
        rpc.OnRequest = HandleRequestAsync;
        _rpc = rpc;
        _pump = Task.Run(() => PumpAsync(rpc));
        var initialized = await rpc.RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = 1,
            // Files and commands stay with the agent; BoSoFa only sees and approves them.
            ["clientCapabilities"] = new JsonObject
            {
                ["fs"] = new JsonObject { ["readTextFile"] = false, ["writeTextFile"] = false }, ["terminal"] = false
            },
            ["clientInfo"] = new JsonObject { ["name"] = Brand.Id, ["version"] = Cli.Version }
        }, RequestTimeout);
        var capabilities = ClaudeJson.Obj(initialized, "agentCapabilities") ?? ClaudeJson.Empty;
        var canLoad = ClaudeJson.Bool(capabilities, "loadSession");
        _acceptsImages = ClaudeJson.Obj(capabilities, "promptCapabilities") is { } promptCapabilities && ClaudeJson.Bool(promptCapabilities, "image");
        if (!await OpenSessionAsync(rpc, canLoad)) return false;
        if (state.Model != null) await SelectModelAsync(rpc);
        await SelectEffortAsync(rpc);
        _ready = true;
        return true;
    }

    /// <param name="status">What the session is doing meanwhile: a new session usually opens for the turn about to start.</param>
    async Task<bool> OpenSessionAsync(JsonRpcProcess rpc, bool canLoad, SessionStatus status = SessionStatus.Running)
    {
        if (state.HarnessSessionId is { } existing && canLoad)
            try
            {
                _loading = true; // the agent replays the conversation as updates; the transcript already has it
                var loaded = await rpc.RequestAsync("session/load", new JsonObject
                {
                    ["sessionId"] = existing, ["cwd"] = state.WorkingDirectory, ["mcpServers"] = _mcpServers.DeepClone()
                }, RequestTimeout);
                if (ClaudeJson.Obj(loaded, "configOptions") is { ValueKind: JsonValueKind.Array } options) _configOptions = options;
                return true;
            }
            catch (JsonRpcException exception)
            {
                sink.Report(state.SessionId, new ItemReport(SessionItemKind.Notice, DateTime.UtcNow,
                    $"The previous conversation could not be resumed ({exception.Message}); starting a new one."));
            }
            finally { _loading = false; }
        try
        {
            var created = await rpc.RequestAsync("session/new", new JsonObject
            {
                ["cwd"] = state.WorkingDirectory, ["mcpServers"] = _mcpServers.DeepClone()
            }, RequestTimeout);
            if (ClaudeJson.Obj(created, "configOptions") is { ValueKind: JsonValueKind.Array } options) _configOptions = options;
            sink.Report(state.SessionId, new StatusReport(status, HarnessSessionId: ClaudeJson.Str(created, "sessionId")));
            return true;
        }
        catch (JsonRpcException exception)
        {
            sink.Report(state.SessionId, new StatusReport(SessionStatus.Failed,
                Error: $"The agent refused the session; sign in to it on this machine if it needs that ({exception.Message})."));
            return false;
        }
    }

    /// <summary>The session's options as the agent last listed them; a model of its own brings its own reasoning levels.</summary>
    JsonElement? _configOptions;

    async Task SelectModelAsync(JsonRpcProcess rpc)
    {
        try
        {
            var selected = await rpc.RequestAsync("session/set_config_option", new JsonObject
            {
                ["sessionId"] = state.HarnessSessionId, ["configId"] = "model", ["value"] = state.Model
            }, RequestTimeout);
            if (ClaudeJson.Obj(selected, "configOptions") is { ValueKind: JsonValueKind.Array } options) _configOptions = options;
        }
        catch (JsonRpcException exception)
        {
            sink.Report(state.SessionId, new ItemReport(SessionItemKind.Notice, DateTime.UtcNow,
                $"The agent did not accept the model {state.Model}: {exception.Message}"));
        }
    }

    /// <summary>The reasoning level, through the option the agent files under <c>thought_level</c>.</summary>
    async Task SelectEffortAsync(JsonRpcProcess rpc)
    {
        if (state.ReasoningEffort == null) return;
        var option = _configOptions is { } options
            ? options.EnumerateArray().FirstOrDefault(candidate => ClaudeJson.Str(candidate, "category") == "thought_level")
            : default;
        if (option.ValueKind != JsonValueKind.Object || ClaudeJson.Str(option, "id") is not { } id)
        {
            sink.Report(state.SessionId, new ItemReport(SessionItemKind.Notice, DateTime.UtcNow,
                $"The agent offers no reasoning levels for this model, so {state.ReasoningEffort} was left out."));
            return;
        }
        try
        {
            await rpc.RequestAsync("session/set_config_option", new JsonObject
            {
                ["sessionId"] = state.HarnessSessionId, ["configId"] = id, ["value"] = state.ReasoningEffort
            }, RequestTimeout);
        }
        catch (JsonRpcException exception)
        {
            sink.Report(state.SessionId, new ItemReport(SessionItemKind.Notice, DateTime.UtcNow,
                $"The agent did not accept the reasoning level {state.ReasoningEffort}: {exception.Message}"));
        }
    }

    JsonArray PromptBlocks(HarnessPrompt prompt)
    {
        var blocks = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = prompt.Text });
        if (prompt.Images.Count > 0 && !_acceptsImages)
            sink.Report(state.SessionId, new ItemReport(SessionItemKind.Notice, DateTime.UtcNow, "This agent cannot read images, so they were left out."));
        else
            foreach (var image in prompt.Images)
                blocks.Add((JsonNode)new JsonObject
                {
                    ["type"] = "image", ["mimeType"] = image.MediaType, ["data"] = Convert.ToBase64String(File.ReadAllBytes(image.Path))
                });
        return blocks;
    }

    /// <summary>One turn: <c>session/prompt</c> answers when it has ended.</summary>
    async Task RunTurnAsync(HarnessPrompt prompt)
    {
        lock (_lock)
        {
            _turnActive = true;
            _turn++;
            _block = 0;
        }
        sink.Report(state.SessionId, new StatusReport(SessionStatus.Running));
        var clock = Stopwatch.StartNew();
        var rpc = _rpc ?? throw new IOException("The agent exited.");
        JsonElement result;
        try
        {
            result = await rpc.RequestAsync("session/prompt", new JsonObject
            {
                ["sessionId"] = state.HarnessSessionId,
                ["prompt"] = PromptBlocks(prompt)
            }, TurnTimeout);
        }
        catch (JsonRpcException exception)
        {
            await rpc.DrainAsync();
            EndTurn("failed", true, clock.ElapsedMilliseconds, exception.Message);
            return;
        }
        await rpc.DrainAsync(); // the response can overtake this turn's last updates
        var reason = ClaudeJson.Str(result, "stopReason") ?? "end_turn";
        EndTurn(reason == "end_turn" ? "completed" : reason, reason is "refusal", clock.ElapsedMilliseconds, null);
    }

    void EndTurn(string outcome, bool isError, long durationMs, string? error)
    {
        FlushText();
        CancelApprovals("turn ended");
        HarnessPrompt? next = null;
        lock (_lock)
        {
            _turnActive = false;
            if (_queued.Count > 0) next = _queued.Dequeue();
        }
        var now = DateTime.UtcNow;
        sink.Report(state.SessionId, new RunEndedReport(now, outcome, isError, 0, 0, 0, durationMs)); // ACP reports no usage
        if (error != null) sink.Report(state.SessionId, new ItemReport(SessionItemKind.Notice, now, error, IsError: true));
        if (next != null) _work.Writer.TryWrite(next);
        else sink.Report(state.SessionId, new StatusReport(SessionStatus.Idle));
    }

    Task<JsonNode?> HandleRequestAsync(JsonRpcIncoming request) => request.Method == "session/request_permission"
        ? AskPermissionAsync(request)
        : throw new NotSupportedException($"{Brand.Name} does not support {request.Method}.");

    async Task<JsonNode?> AskPermissionAsync(JsonRpcIncoming request)
    {
        var parameters = request.Params;
        var key = request.Id?.GetRawText().Trim('"') ?? "";
        var options = ClaudeJson.Obj(parameters, "options") is { ValueKind: JsonValueKind.Array } list ? list.EnumerateArray().ToList() : [];
        string? Choose(params string[] kinds) => kinds
            .Select(kind => options.FirstOrDefault(option => ClaudeJson.Str(option, "kind") == kind))
            .Where(option => option.ValueKind == JsonValueKind.Object).Select(option => ClaudeJson.Str(option, "optionId")).FirstOrDefault();
        var allow = Choose("allow_once", "allow_always");
        var reject = Choose("reject_once", "reject_always");
        JsonNode Selected(string? option) => new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "selected", ["optionId"] = option } };
        if (state.PermissionMode == "bypassPermissions" && allow != null) return Selected(allow);

        var call = ClaudeJson.Obj(parameters, "toolCall") ?? default;
        var answer = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _approvals[key] = answer;
        sink.Report(state.SessionId, new ApprovalRequestedReport(key, DateTime.UtcNow, ClaudeJson.Str(call, "kind") ?? "tool",
            ClaudeJson.Str(call, "toolCallId"), ClaudeJson.Obj(call, "rawInput")?.GetRawText() ?? "{}", ClaudeJson.Str(call, "title")));
        sink.Report(state.SessionId, new StatusReport(SessionStatus.WaitingForApproval));
        return await answer.Task switch
        {
            true when allow != null => Selected(allow),
            false when reject != null => Selected(reject),
            _ => new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "cancelled" } }
        };
    }

    public Task AnswerAsync(string key, bool allow, string? message)
    {
        if (_approvals.TryRemove(key, out var answer))
        {
            answer.TrySetResult(allow);
            if (_approvals.IsEmpty) sink.Report(state.SessionId, new StatusReport(SessionStatus.Running));
        }
        return Task.CompletedTask;
    }

    void CancelApprovals(string reason)
    {
        foreach (var key in _approvals.Keys)
            if (_approvals.TryRemove(key, out var answer))
            {
                answer.TrySetResult(null);
                sink.Report(state.SessionId, new ApprovalClosedReport(key, reason));
            }
    }

    public Task InterruptAsync() => InterruptAsync(clearQueue: true);

    /// <summary>The agent's /compact command (OpenCode's summary), as a prompt after the running turn.</summary>
    public Task CompactAsync() => SendAsync(HarnessPrompt.Compaction, DispatchMode.Queue);

    /// <summary>A new ACP session in the same process, after the running turn.</summary>
    public Task ClearAsync() => SendAsync(HarnessPrompt.Clear, DispatchMode.Queue);

    /// <summary>A second agent process on a fork of the session; everything it asks to do is refused, and the fork is closed after.</summary>
    public async Task<string> AnswerSideQuestionAsync(string question, CancellationToken cancellation)
    {
        if (!Directory.Exists(state.WorkingDirectory)) throw new IOException($"The working directory {state.WorkingDirectory} does not exist on this machine.");
        var started = await launch();
        await using var rpc = started.Rpc;
        rpc.OnRequest = request => request.Method == "session/request_permission"
            ? Task.FromResult<JsonNode?>(new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "cancelled" } })
            : throw new NotSupportedException($"{Brand.Name} does not support {request.Method}.");
        var said = new StringBuilder();
        string? sessionId = null;
        var listening = false;
        var pump = Task.Run(async () =>
        {
            await foreach (var message in rpc.Incoming.ReadAllAsync(cancellation))
                if (message.Reached != null) message.Reached();
                else if (message.Id != null) rpc.Serve(message);
                else if (message.Method == "session/update" && Volatile.Read(ref listening) && ClaudeJson.Str(message.Params, "sessionId") == Volatile.Read(ref sessionId) &&
                         ClaudeJson.Obj(message.Params, "update") is { } update && ClaudeJson.Str(update, "sessionUpdate") == "agent_message_chunk" &&
                         Chunk(update) is { Length: > 0 } text)
                    said.Append(text);
        }, cancellation);
        var initialized = await rpc.RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = 1,
            ["clientCapabilities"] = new JsonObject { ["fs"] = new JsonObject { ["readTextFile"] = false, ["writeTextFile"] = false }, ["terminal"] = false },
            ["clientInfo"] = new JsonObject { ["name"] = Brand.Id, ["version"] = Cli.Version }
        }, RequestTimeout);
        var capabilities = ClaudeJson.Obj(initialized, "agentCapabilities") ?? ClaudeJson.Empty;
        var canFork = ClaudeJson.Obj(capabilities, "sessionCapabilities") is { } sessions && ClaudeJson.Obj(sessions, "fork") != null;
        if (state.HarnessSessionId != null && !canFork) throw new InvalidOperationException("This agent cannot answer on the side: it cannot fork a session.");
        var opened = state.HarnessSessionId is { } current
            ? await rpc.RequestAsync("session/fork", new JsonObject { ["sessionId"] = current, ["cwd"] = state.WorkingDirectory, ["mcpServers"] = new JsonArray() }, RequestTimeout)
            : await rpc.RequestAsync("session/new", new JsonObject { ["cwd"] = state.WorkingDirectory, ["mcpServers"] = new JsonArray() }, RequestTimeout);
        Volatile.Write(ref sessionId, ClaudeJson.Str(opened, "sessionId") ?? throw new IOException("The agent did not open a session for the question."));
        if (state.Model != null)
            try
            {
                await rpc.RequestAsync("session/set_config_option", new JsonObject { ["sessionId"] = sessionId, ["configId"] = "model", ["value"] = state.Model }, RequestTimeout);
            }
            catch (JsonRpcException) { /* the session's own model then */ }
        await rpc.DrainAsync(); // a fork may replay the conversation first
        Volatile.Write(ref listening, true);
        var result = await rpc.RequestAsync("session/prompt", new JsonObject
        {
            ["sessionId"] = sessionId, ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = HarnessPrompt.SideQuestion(question) })
        }, TurnTimeout, cancellation);
        await rpc.DrainAsync();
        try { await rpc.RequestAsync("session/close", new JsonObject { ["sessionId"] = sessionId }, RequestTimeout); }
        catch (JsonRpcException) { /* an agent without it keeps the fork */ }
        _ = pump;
        if (ClaudeJson.Str(result, "stopReason") is "refusal") throw new IOException("The agent refused to answer.");
        return said.ToString().Trim();
    }

    async Task StartOverAsync()
    {
        // A process that is not running starts with a new session anyway, once it has none to load.
        state.HarnessSessionId = null;
        if (_ready && _rpc is { } rpc)
        {
            if (!await OpenSessionAsync(rpc, canLoad: false, SessionStatus.Idle)) return;
            if (state.Model != null) await SelectModelAsync(rpc);
            await SelectEffortAsync(rpc);
        }
        sink.Report(state.SessionId, new ItemReport(SessionItemKind.Cleared, DateTime.UtcNow, ""));
        HarnessPrompt? next = null;
        lock (_lock)
            if (_queued.Count > 0) next = _queued.Dequeue();
        if (next != null) _work.Writer.TryWrite(next);
        else sink.Report(state.SessionId, new StatusReport(SessionStatus.Idle));
    }

    async Task InterruptAsync(bool clearQueue)
    {
        lock (_lock)
        {
            if (clearQueue) _queued.Clear();
            if (!_turnActive) return;
        }
        CancelApprovals("interrupted");
        if (_rpc is { } rpc) await rpc.NotifyAsync("session/cancel", new JsonObject { ["sessionId"] = state.HarnessSessionId });
    }

    async Task PumpAsync(JsonRpcProcess rpc)
    {
        try
        {
            await foreach (var message in rpc.Incoming.ReadAllAsync())
                if (message.Reached != null) message.Reached();
                else if (message.Id != null) rpc.Serve(message);
                else if (message.Method == "session/update" && !_loading && ClaudeJson.Obj(message.Params, "update") is { } update)
                    try { Translate(update); }
                    catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException)
                    {
                        log.WriteLine($"Session {state.SessionId}: skipped an update: {exception.Message}");
                    }
        }
        finally { await OnExitedAsync(rpc); }
    }

    async Task OnExitedAsync(JsonRpcProcess rpc)
    {
        if (Interlocked.CompareExchange(ref _rpc, null, rpc) != rpc) return; // stopped on purpose
        _ready = false;
        CancelApprovals("exited");
        lock (_lock) _turnActive = false;
        var code = rpc.ExitCode;
        if (code is not (null or 0))
            sink.Report(state.SessionId, new StatusReport(SessionStatus.Idle,
                Error: $"The agent exited (code {code}). {rpc.StandardErrorTail}".Trim()));
        await rpc.DisposeAsync();
    }

    void Translate(JsonElement update)
    {
        switch (ClaudeJson.Str(update, "sessionUpdate"))
        {
            case "agent_message_chunk" when Chunk(update) is { Length: > 0 } text:
                if (_thought.Length > 0) FlushText();
                _message.Append(text);
                sink.Delta(state.SessionId, MessageRef, text);
                break;
            case "agent_thought_chunk" when Chunk(update) is { Length: > 0 } text:
                if (_message.Length > 0) FlushText();
                _thought.Append(text);
                break;
            case "tool_call":
                FlushText();
                var id = ClaudeJson.Str(update, "toolCallId");
                sink.Report(state.SessionId, new ItemReport(SessionItemKind.ToolUse, DateTime.UtcNow, ClaudeJson.Str(update, "title") ?? "",
                    id, ClaudeJson.Str(update, "kind") ?? "tool", ClaudeJson.Obj(update, "rawInput")?.GetRawText()));
                ReportIfFinished(update);
                break;
            case "tool_call_update":
                ReportIfFinished(update);
                break;
        }
    }

    static string? Chunk(JsonElement update) => ClaudeJson.Obj(update, "content") is { } content ? ClaudeJson.Str(content, "text") : null;

    /// <summary>Text before a tool call, or a change between thinking and answering, ends the current block.</summary>
    void FlushText()
    {
        var now = DateTime.UtcNow;
        if (_thought.Length > 0) sink.Report(state.SessionId, new ItemReport(SessionItemKind.Thinking, now, _thought.ToString()));
        if (_message.Length > 0) sink.Report(state.SessionId, new ItemReport(SessionItemKind.Assistant, now, _message.ToString(), Ref: MessageRef));
        _thought.Clear();
        _message.Clear();
        _block++;
    }

    void ReportIfFinished(JsonElement update)
    {
        var status = ClaudeJson.Str(update, "status");
        if (status is not ("completed" or "failed") || ClaudeJson.Str(update, "toolCallId") is not { } id) return;
        lock (_finishedTools)
            if (!_finishedTools.Add(id)) return;
        var output = new StringBuilder();
        if (ClaudeJson.Obj(update, "content") is { ValueKind: JsonValueKind.Array } content)
            foreach (var part in content.EnumerateArray())
                if (ClaudeJson.Obj(part, "content") is { } inner && ClaudeJson.Str(inner, "text") is { } text) output.Append(text).Append('\n');
                else if (ClaudeJson.Str(part, "type") == "diff") output.Append(ClaudeJson.Str(part, "path")).Append('\n');
        if (output.Length == 0 && ClaudeJson.Obj(update, "rawOutput") is { } raw) output.Append(raw.GetRawText());
        var result = output.ToString();
        sink.Report(state.SessionId, new ItemReport(SessionItemKind.ToolResult, DateTime.UtcNow,
            result.Length <= ItemReport.InlineLimit ? result : result[..ItemReport.InlineLimit] + "\n…", id, IsError: status == "failed"));
    }

    public async Task StopAsync()
    {
        await CloseAsync();
        sink.Report(state.SessionId, new StatusReport(SessionStatus.Stopped));
    }

    async Task CloseAsync()
    {
        CancelApprovals("stopped");
        lock (_lock)
        {
            _turnActive = false;
            _queued.Clear();
        }
        _ready = false;
        if (Interlocked.Exchange(ref _rpc, null) is { } rpc) await rpc.DisposeAsync();
        if (_pump != null)
            try { await _pump; }
            catch (Exception) { /* already reported */ }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _work.Writer.TryComplete();
        await CloseAsync();
        if (_worker != null)
            try { await _worker; }
            catch (Exception) { /* cancelled */ }
    }
}
