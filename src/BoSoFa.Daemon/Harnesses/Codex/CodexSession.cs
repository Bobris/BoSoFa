using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using BoSoFa.Daemon.Harnesses.Claude;
using BoSoFa.Daemon.Sessions;
using BoSoFa.Protocol;

namespace BoSoFa.Daemon.Harnesses.Codex;

/// <summary>A started <c>codex app-server</c>, and the API key to sign it in with when the session's account has one.</summary>
public sealed record CodexLaunch(JsonRpcProcess Rpc, string? ApiKey, JsonObject? Config = null);

/// <summary>
/// One BoSoFa session on <c>codex app-server</c> (JSON-RPC over stdio). The process and its thread start with the first
/// message, and a later process resumes the thread by id. Work runs on the session's own loop, because signing in can
/// take minutes: a machine that is not signed in shows a device code in the transcript and the turn starts once the
/// user approved it. Codex refreshes its tokens itself, in the profile's CODEX_HOME (plan §5.4.1).
/// </summary>
public sealed class CodexSession(SessionState state, ISessionSink sink, Func<Task<CodexLaunch>> launch, TextWriter log)
    : IHarnessSession
{
    static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(2);
    static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(15);
    const string StoppedDuringLogin = "stopped";

    readonly Channel<(string Text, DispatchMode Mode)> _work = Channel.CreateUnbounded<(string, DispatchMode)>();
    readonly CancellationTokenSource _stop = new();
    readonly object _lock = new();
    readonly Queue<string> _queued = new();
    readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _approvals = new();
    readonly ConcurrentDictionary<string, JsonElement> _items = new();
    Task? _worker;
    JsonRpcProcess? _rpc;
    Task? _pump;
    bool _threadReady;
    bool _turnActive;
    string? _turnId;
    long _inputTokens, _outputTokens;
    TaskCompletionSource<(bool Success, string? Error)>? _login;
    JsonObject? _config;

    public SessionState State => state;

    public Task SendAsync(string text, DispatchMode mode)
    {
        lock (_lock) _worker ??= Task.Run(WorkAsync);
        _work.Writer.TryWrite((text, mode));
        return Task.CompletedTask;
    }

    async Task WorkAsync()
    {
        try
        {
            await foreach (var (text, mode) in _work.Reader.ReadAllAsync(_stop.Token))
                try
                {
                    if (await EnsureThreadAsync()) await DispatchAsync(text, mode);
                }
                catch (Exception exception) when (exception is IOException or JsonRpcException or TimeoutException or
                                                      InvalidOperationException)
                {
                    lock (_lock) _turnActive = false;
                    sink.Report(state.SessionId, new StatusReport(SessionStatus.Idle, Error: $"Codex failed: {exception.Message}"));
                }
        }
        catch (OperationCanceledException) { }
    }

    async Task DispatchAsync(string text, DispatchMode mode)
    {
        string? steerable;
        lock (_lock)
        {
            if (_turnActive && mode == DispatchMode.Queue || _turnActive && _turnId == null)
            {
                _queued.Enqueue(text);
                return;
            }
            steerable = _turnActive ? _turnId : null;
        }
        if (steerable == null)
        {
            await StartTurnAsync(text);
            return;
        }
        try
        {
            await (_rpc ?? throw new IOException("Codex exited.")).RequestAsync("turn/steer", new JsonObject
            {
                ["threadId"] = state.HarnessSessionId, ["expectedTurnId"] = steerable, ["input"] = Input(text)
            }, RequestTimeout);
        }
        catch (JsonRpcException)
        {
            // The turn ended meanwhile: this message starts the next one.
            lock (_lock)
            {
                if (_turnActive)
                {
                    _queued.Enqueue(text);
                    return;
                }
            }
            await StartTurnAsync(text);
        }
    }

    // The constructor, not a collection expression: that compiles to JsonArray.Add<T>, which Native AOT rejects.
    static JsonArray Input(string text) => new(new JsonObject { ["type"] = "text", ["text"] = text });

    async Task StartTurnAsync(string text)
    {
        lock (_lock)
        {
            _turnActive = true;
            _turnId = null;
            _inputTokens = _outputTokens = 0;
        }
        sink.Report(state.SessionId, new StatusReport(SessionStatus.Running));
        var rpc = _rpc ?? throw new IOException("Codex exited.");
        var result = await rpc.RequestAsync("turn/start", new JsonObject
        {
            ["threadId"] = state.HarnessSessionId, ["input"] = Input(text)
        }, RequestTimeout);
        // turn/started can arrive before this response; whichever comes first names the turn.
        if (ClaudeJson.Obj(result, "turn") is { } turn)
            lock (_lock) _turnId ??= ClaudeJson.Str(turn, "id");
    }

    /// <summary>Our permission modes as Codex policies. Unless bypassed, the sandbox keeps writes in the workspace and
    /// everything that is not known to be safe asks first.</summary>
    (string Approval, string Sandbox) Policies() => state.PermissionMode switch
    {
        "bypassPermissions" => ("never", "danger-full-access"),
        "acceptEdits" => ("on-request", "workspace-write"),
        "plan" => ("untrusted", "read-only"),
        _ => ("untrusted", "workspace-write")
    };

    async Task<bool> EnsureThreadAsync()
    {
        if (_threadReady && _rpc != null) return true;
        if (!Directory.Exists(state.WorkingDirectory))
        {
            sink.Report(state.SessionId, new StatusReport(SessionStatus.Failed,
                Error: $"The working directory {state.WorkingDirectory} does not exist on this machine."));
            return false;
        }
        CodexLaunch started;
        try { started = await launch(); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or HttpRequestException or
                                              System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            sink.Report(state.SessionId, new StatusReport(SessionStatus.Failed, Error: $"Codex did not start: {exception.Message}"));
            return false;
        }
        var rpc = started.Rpc;
        _config = started.Config;
        rpc.OnRequest = HandleRequestAsync;
        _rpc = rpc;
        _pump = Task.Run(() => PumpAsync(rpc));
        await rpc.RequestAsync("initialize", new JsonObject
        {
            ["clientInfo"] = new JsonObject { ["name"] = "bosofa", ["title"] = "BoSoFa", ["version"] = Cli.Version },
            ["capabilities"] = new JsonObject { ["experimentalApi"] = false }
        }, RequestTimeout);
        await rpc.NotifyAsync("initialized");
        if (started.ApiKey != null)
            await rpc.RequestAsync("account/login/start", new JsonObject { ["type"] = "apiKey", ["apiKey"] = started.ApiKey },
                RequestTimeout);
        else if (!await EnsureSignedInAsync(rpc)) return false;
        await OpenThreadAsync(rpc);
        _threadReady = true;
        return true;
    }

    /// <summary>A signed-out profile would otherwise fail mid-turn with a bare 401.</summary>
    async Task<bool> EnsureSignedInAsync(JsonRpcProcess rpc)
    {
        var account = await rpc.RequestAsync("account/read", new JsonObject { ["refreshToken"] = false }, RequestTimeout);
        if (ClaudeJson.Obj(account, "account") != null || !ClaudeJson.Bool(account, "requiresOpenaiAuth")) return true;
        var completed = new TaskCompletionSource<(bool, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _login = completed;
        var login = await rpc.RequestAsync("account/login/start", new JsonObject { ["type"] = "chatgptDeviceCode" }, RequestTimeout);
        var url = ClaudeJson.Str(login, "verificationUrl") ?? "";
        var code = ClaudeJson.Str(login, "userCode") ?? "";
        sink.Report(state.SessionId, new ItemReport(SessionItemKind.Notice, DateTime.UtcNow,
            $"Sign in to Codex on this machine: open {url} and enter the code {code}. The session continues afterwards. If the page says device code sign-in is off, turn it on in ChatGPT's security settings and start the session again.",
            Data: new JsonObject { ["verificationUrl"] = url, ["userCode"] = code }.ToJsonString()));
        sink.Report(state.SessionId, new StatusReport(SessionStatus.WaitingForApproval));
        (bool Success, string? Error) result;
        try { result = await completed.Task.WaitAsync(LoginTimeout, _stop.Token); }
        catch (TimeoutException)
        {
            result = (false, "The sign-in code expired.");
            await rpc.RequestAsync("account/login/cancel", new JsonObject { ["loginId"] = ClaudeJson.Str(login, "loginId") }, RequestTimeout);
        }
        if (result.Success) return true;
        if (result.Error == StoppedDuringLogin) return false; // StopAsync reports the outcome
        sink.Report(state.SessionId, new StatusReport(SessionStatus.Failed, Error: $"Signing in to Codex failed: {result.Error}"));
        return false;
    }

    async Task OpenThreadAsync(JsonRpcProcess rpc)
    {
        var (approval, sandbox) = Policies();
        var settings = new JsonObject { ["cwd"] = state.WorkingDirectory, ["approvalPolicy"] = approval, ["sandbox"] = sandbox };
        if (state.Model != null) settings["model"] = state.Model;
        if (_config != null) settings["config"] = _config.DeepClone(); // Codex does not keep it across resumes
        JsonElement result;
        if (state.HarnessSessionId is { } threadId)
        {
            var resume = settings.DeepClone().AsObject();
            resume["threadId"] = threadId;
            resume["excludeTurns"] = true;
            try
            {
                result = await rpc.RequestAsync("thread/resume", resume, RequestTimeout);
                _threadReady = true;
                return;
            }
            catch (JsonRpcException exception)
            {
                sink.Report(state.SessionId, new ItemReport(SessionItemKind.Notice, DateTime.UtcNow,
                    $"The previous Codex conversation could not be resumed ({exception.Message}); starting a new one."));
            }
        }
        settings["serviceName"] = "bosofa";
        result = await rpc.RequestAsync("thread/start", settings, RequestTimeout);
        var thread = ClaudeJson.Obj(result, "thread") ?? throw new InvalidOperationException("thread/start returned no thread.");
        sink.Report(state.SessionId, new StatusReport(SessionStatus.Running, HarnessSessionId: ClaudeJson.Str(thread, "id"),
            Model: ClaudeJson.Str(result, "model")));
    }

    async Task<JsonNode?> HandleRequestAsync(JsonRpcIncoming request)
    {
        var parameters = request.Params;
        var key = request.Id?.GetRawText().Trim('"') ?? "";
        var itemId = ClaudeJson.Str(parameters, "itemId");
        switch (request.Method)
        {
            case "item/commandExecution/requestApproval":
                var command = new JsonObject
                {
                    ["command"] = ClaudeJson.Str(parameters, "command"), ["cwd"] = ClaudeJson.Str(parameters, "cwd"),
                    ["reason"] = ClaudeJson.Str(parameters, "reason")
                };
                return new JsonObject { ["decision"] = await AskAsync(key, "Bash", itemId, command.ToJsonString(), ClaudeJson.Str(parameters, "reason")) };
            case "item/fileChange/requestApproval":
                // The paths and diff are on the item, not in the request.
                var changes = itemId != null && _items.TryGetValue(itemId, out var item) && ClaudeJson.Obj(item, "changes") is { } list
                    ? list.GetRawText()
                    : "[]";
                return new JsonObject { ["decision"] = await AskAsync(key, "Edit", itemId, changes, ClaudeJson.Str(parameters, "reason")) };
            case "item/permissions/requestApproval":
                return new JsonObject { ["permissions"] = new JsonObject(), ["scope"] = "turn" }; // nothing beyond the sandbox
            case "mcpServer/elicitation/request":
                return new JsonObject { ["action"] = "decline" };
            default:
                throw new NotSupportedException($"BoSoFa does not support {request.Method} yet.");
        }
    }

    async Task<string> AskAsync(string key, string tool, string? itemId, string input, string? reason)
    {
        var decision = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _approvals[key] = decision;
        sink.Report(state.SessionId, new ApprovalRequestedReport(key, DateTime.UtcNow, tool, itemId, input, reason));
        sink.Report(state.SessionId, new StatusReport(SessionStatus.WaitingForApproval));
        return await decision.Task;
    }

    public Task AnswerAsync(string key, bool allow, string? message)
    {
        if (_approvals.TryRemove(key, out var decision))
        {
            decision.TrySetResult(allow ? "accept" : "decline");
            if (_approvals.IsEmpty) sink.Report(state.SessionId, new StatusReport(SessionStatus.Running));
        }
        return Task.CompletedTask;
    }

    void CancelApprovals(string reason)
    {
        foreach (var key in _approvals.Keys)
            if (_approvals.TryRemove(key, out var decision))
            {
                decision.TrySetResult("cancel");
                sink.Report(state.SessionId, new ApprovalClosedReport(key, reason));
            }
    }

    public async Task InterruptAsync()
    {
        string? turn;
        lock (_lock)
        {
            turn = _turnId;
            _queued.Clear();
        }
        if (_rpc is not { } rpc || turn == null) return;
        CancelApprovals("interrupted");
        try
        {
            await rpc.RequestAsync("turn/interrupt", new JsonObject { ["threadId"] = state.HarnessSessionId, ["turnId"] = turn },
                RequestTimeout);
        }
        catch (Exception exception) when (exception is IOException or JsonRpcException or TimeoutException)
        {
            log.WriteLine($"Session {state.SessionId}: interrupt failed: {exception.Message}");
        }
    }

    async Task PumpAsync(JsonRpcProcess rpc)
    {
        try
        {
            await foreach (var message in rpc.Incoming.ReadAllAsync())
                try
                {
                    if (message.Reached != null) message.Reached();
                    else if (message.Id != null) rpc.Serve(message);
                    else Translate(message);
                }
                catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException)
                {
                    log.WriteLine($"Session {state.SessionId}: skipped {message.Method}: {exception.Message}");
                }
        }
        finally { await OnExitedAsync(rpc); }
    }

    async Task OnExitedAsync(JsonRpcProcess rpc)
    {
        if (Interlocked.CompareExchange(ref _rpc, null, rpc) != rpc) return; // stopped on purpose
        _threadReady = false;
        _login?.TrySetResult((false, "Codex exited."));
        CancelApprovals("exited");
        bool wasActive;
        lock (_lock)
        {
            wasActive = _turnActive;
            _turnActive = false;
            _turnId = null;
        }
        var code = rpc.ExitCode;
        if (wasActive || code is not (null or 0))
            sink.Report(state.SessionId, new StatusReport(SessionStatus.Idle,
                Error: $"Codex exited (code {code?.ToString() ?? "unknown"}). {rpc.StandardErrorTail}".Trim()));
        await rpc.DisposeAsync();
    }

    void Translate(JsonRpcIncoming message)
    {
        var parameters = message.Params;
        switch (message.Method)
        {
            case "turn/started" when ClaudeJson.Obj(parameters, "turn") is { } turn:
                lock (_lock) _turnId ??= ClaudeJson.Str(turn, "id");
                break;
            case "turn/completed" when ClaudeJson.Obj(parameters, "turn") is { } turn:
                OnTurnCompleted(turn);
                break;
            case "item/started" when ClaudeJson.Obj(parameters, "item") is { } item:
                if (ClaudeJson.Str(item, "id") is { } startedId) _items[startedId] = item;
                ReportStarted(item);
                break;
            case "item/completed" when ClaudeJson.Obj(parameters, "item") is { } item:
                ReportCompleted(item);
                if (ClaudeJson.Str(item, "id") is { } completedId) _items.TryRemove(completedId, out _);
                break;
            case "item/agentMessage/delta":
                if (ClaudeJson.Str(parameters, "itemId") is { } itemRef && ClaudeJson.Str(parameters, "delta") is { Length: > 0 } text)
                    sink.Delta(state.SessionId, itemRef, text);
                break;
            case "thread/tokenUsage/updated" when ClaudeJson.Obj(parameters, "tokenUsage") is { } usage && ClaudeJson.Obj(usage, "last") is { } last:
                lock (_lock)
                {
                    _inputTokens = ClaudeJson.Long(last, "inputTokens");
                    _outputTokens = ClaudeJson.Long(last, "outputTokens");
                }
                break;
            case "account/login/completed":
                _login?.TrySetResult((ClaudeJson.Bool(parameters, "success"), ClaudeJson.Str(parameters, "error")));
                break;
        }
    }

    void ReportStarted(JsonElement item)
    {
        var now = DateTime.UtcNow;
        var id = ClaudeJson.Str(item, "id");
        switch (ClaudeJson.Str(item, "type"))
        {
            case "commandExecution":
                var command = ClaudeJson.Str(item, "command") ?? "";
                sink.Report(state.SessionId, new ItemReport(SessionItemKind.ToolUse, now, command, id, "Bash",
                    new JsonObject { ["command"] = command, ["cwd"] = ClaudeJson.Str(item, "cwd") }.ToJsonString()));
                break;
            case "fileChange" when ClaudeJson.Obj(item, "changes") is { ValueKind: JsonValueKind.Array } changes:
                var paths = string.Join(", ", changes.EnumerateArray().Select(change => ClaudeJson.Str(change, "path")));
                sink.Report(state.SessionId, new ItemReport(SessionItemKind.ToolUse, now, paths, id, "Edit", changes.GetRawText()));
                break;
            case "mcpToolCall":
                var tool = $"mcp__{ClaudeJson.Str(item, "server")}__{ClaudeJson.Str(item, "tool")}";
                sink.Report(state.SessionId, new ItemReport(SessionItemKind.ToolUse, now, "", id, tool,
                    ClaudeJson.Obj(item, "arguments")?.GetRawText()));
                break;
        }
    }

    void ReportCompleted(JsonElement item)
    {
        var now = DateTime.UtcNow;
        var id = ClaudeJson.Str(item, "id");
        var status = ClaudeJson.Str(item, "status");
        var failed = status is "failed" or "declined";
        switch (ClaudeJson.Str(item, "type"))
        {
            case "agentMessage" when ClaudeJson.Str(item, "text") is { Length: > 0 } text:
                sink.Report(state.SessionId, new ItemReport(SessionItemKind.Assistant, now, text, Ref: id));
                break;
            case "reasoning" when ClaudeJson.Obj(item, "summary") is { ValueKind: JsonValueKind.Array } summary:
                var thinking = string.Join("\n\n", summary.EnumerateArray().Select(part =>
                    part.ValueKind == JsonValueKind.String ? part.GetString() : ClaudeJson.Str(part, "text")));
                if (thinking.Length > 0) sink.Report(state.SessionId, new ItemReport(SessionItemKind.Thinking, now, thinking, Ref: id));
                break;
            case "commandExecution":
                var exitCode = ClaudeJson.Obj(item, "exitCode") is { ValueKind: JsonValueKind.Number } code ? code.GetInt32() : 0;
                sink.Report(state.SessionId, new ItemReport(SessionItemKind.ToolResult, now,
                    Cut(ClaudeJson.Str(item, "aggregatedOutput") ?? (status == "declined" ? "Declined." : "")), id,
                    IsError: failed || exitCode != 0));
                break;
            case "fileChange" when ClaudeJson.Obj(item, "changes") is { ValueKind: JsonValueKind.Array } changes:
                var diff = new StringBuilder();
                foreach (var change in changes.EnumerateArray()) diff.Append(ClaudeJson.Str(change, "diff")).Append('\n');
                sink.Report(state.SessionId, new ItemReport(SessionItemKind.ToolResult, now,
                    status == "declined" ? "Declined." : Cut(diff.ToString()), id, IsError: failed));
                break;
            case "mcpToolCall":
                sink.Report(state.SessionId, new ItemReport(SessionItemKind.ToolResult, now,
                    Cut(ClaudeJson.Obj(item, "error") is { } error
                        ? ClaudeJson.Str(error, "message") ?? ""
                        : ClaudeJson.Obj(item, "result")?.GetRawText() ?? ""), id, IsError: status == "failed"));
                break;
            case "webSearch" when ClaudeJson.Str(item, "query") is { Length: > 0 } query:
                sink.Report(state.SessionId, new ItemReport(SessionItemKind.ToolUse, now, query, id, "WebSearch"));
                break;
        }
    }

    static string Cut(string text) => text.Length <= ItemReport.InlineLimit
        ? text
        : text[..ItemReport.InlineLimit] + $"\n… ({text.Length - ItemReport.InlineLimit} more characters)";

    void OnTurnCompleted(JsonElement turn)
    {
        CancelApprovals("turn ended");
        var status = ClaudeJson.Str(turn, "status") ?? "completed";
        var now = DateTime.UtcNow;
        long input, output;
        string? next = null;
        lock (_lock)
        {
            _turnActive = false;
            _turnId = null;
            (input, output) = (_inputTokens, _outputTokens);
            if (_queued.Count > 0) next = _queued.Dequeue();
        }
        // Codex reports tokens but no price; a subscription has none, and API prices are not known here.
        sink.Report(state.SessionId, new RunEndedReport(now, status, status == "failed", 0, input, output, ClaudeJson.Long(turn, "durationMs")));
        if (status == "failed" && ClaudeJson.Obj(turn, "error") is { } error)
            sink.Report(state.SessionId, new ItemReport(SessionItemKind.Notice, now, ClaudeJson.Str(error, "message") ?? "The turn failed.", IsError: true));
        if (next != null) _work.Writer.TryWrite((next, DispatchMode.Queue));
        else sink.Report(state.SessionId, new StatusReport(SessionStatus.Idle));
    }

    public async Task StopAsync()
    {
        await CloseAsync();
        sink.Report(state.SessionId, new StatusReport(SessionStatus.Stopped));
    }

    async Task CloseAsync()
    {
        _login?.TrySetResult((false, StoppedDuringLogin));
        CancelApprovals("stopped");
        lock (_lock)
        {
            _turnActive = false;
            _turnId = null;
            _queued.Clear();
        }
        _threadReady = false;
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
