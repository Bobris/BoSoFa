using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using BoSoFa.Daemon.Sessions;
using BoSoFa.Protocol;

namespace BoSoFa.Daemon.Harnesses.Claude;

/// <summary>
/// One BoSoFa session on Claude Code. The process starts with the first message and stays for later turns; after it
/// exits (or the daemon restarted) the next message resumes the conversation with <c>--resume</c>. Translates the
/// CLI's stream into durable reports (items, status, run ends, approvals) and ephemeral text deltas.
/// </summary>
public sealed class ClaudeSession(SessionState state, ISessionSink sink, Func<ClaudeLaunch, Task<IClaudeTransport>> launch,
    TextWriter log) : IHarnessSession
{
    static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(60);

    readonly SemaphoreSlim _lock = new(1);
    readonly ConcurrentDictionary<string, JsonElement> _approvals = new();
    IClaudeTransport? _transport;
    Task? _pump;
    bool _turnRunning;
    string? _messageId;
    long _blockIndex;

    public SessionState State => state;

    /// <summary>The CLI queues a message written while a turn runs; steering interrupts that turn first, so the
    /// message starts the next one at once (Claude Code has no native steering).</summary>
    public async Task SendAsync(string text, DispatchMode mode)
    {
        if (mode == DispatchMode.Steer && _turnRunning) await InterruptAsync();
        await SendAsync(text);
    }

    async Task SendAsync(string text)
    {
        await _lock.WaitAsync();
        try
        {
            var transport = await EnsureStartedAsync();
            if (transport == null) return;
            _turnRunning = true;
            sink.Report(state.SessionId, new StatusReport(SessionStatus.Running));
            await transport.SendUserAsync(text);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            sink.Report(state.SessionId, new StatusReport(SessionStatus.Idle, Error: $"Could not send the message: {exception.Message}"));
        }
        finally { _lock.Release(); }
    }

    public async Task InterruptAsync()
    {
        if (_transport is not { } transport) return;
        try { await transport.InterruptAsync(ControlTimeout); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException)
        {
            log.WriteLine($"Session {state.SessionId}: interrupt failed: {exception.Message}");
        }
    }

    public async Task AnswerAsync(string key, bool allow, string? message)
    {
        if (_transport is not { } transport || !_approvals.TryRemove(key, out var input)) return;
        var answer = allow
            ? new PermissionResult("allow", UpdatedInput: input)
            : new PermissionResult("deny", Message: string.IsNullOrWhiteSpace(message) ? "Denied in BoSoFa." : message);
        try
        {
            await transport.RespondAsync(key, JsonSerializer.SerializeToElement(answer, ClaudeJson.Default.PermissionResult));
            if (_approvals.IsEmpty) sink.Report(state.SessionId, new StatusReport(SessionStatus.Running));
        }
        catch (IOException) { /* the process is gone; its exit is reported by the pump */ }
    }

    public async Task StopAsync()
    {
        await _lock.WaitAsync();
        try
        {
            await DisposeTransportAsync();
            foreach (var key in _approvals.Keys)
                if (_approvals.TryRemove(key, out _)) sink.Report(state.SessionId, new ApprovalClosedReport(key, "stopped"));
            _turnRunning = false;
            sink.Report(state.SessionId, new StatusReport(SessionStatus.Stopped));
        }
        finally { _lock.Release(); }
    }

    async Task<IClaudeTransport?> EnsureStartedAsync()
    {
        if (_transport != null) return _transport;
        if (!Directory.Exists(state.WorkingDirectory))
        {
            sink.Report(state.SessionId, new StatusReport(SessionStatus.Failed,
                Error: $"The working directory {state.WorkingDirectory} does not exist on this machine."));
            return null;
        }
        IClaudeTransport transport;
        try
        {
            transport = await launch(new(state.WorkingDirectory, state.PermissionMode, state.Model, state.HarnessSessionId));
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or
                                              System.ComponentModel.Win32Exception or FileNotFoundException or
                                              HttpRequestException or UnauthorizedAccessException)
        {
            sink.Report(state.SessionId, new StatusReport(SessionStatus.Failed, Error: $"Claude Code did not start: {exception.Message}"));
            return null;
        }
        transport.OnControlRequest = (id, subtype, request) => OnControlRequestAsync(transport, id, subtype, request);
        transport.OnControlCancel = id =>
        {
            if (_approvals.TryRemove(id, out _)) sink.Report(state.SessionId, new ApprovalClosedReport(id, "cancelled"));
        };
        _transport = transport;
        _pump = Task.Run(() => PumpAsync(transport));
        try { await transport.InitializeAsync(ControlTimeout); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException)
        {
            log.WriteLine($"Session {state.SessionId}: initialize failed: {exception.Message}");
        }
        return transport;
    }

    async Task OnControlRequestAsync(IClaudeTransport transport, string id, string subtype, JsonElement request)
    {
        switch (subtype)
        {
            case "can_use_tool":
                var input = ClaudeJson.Obj(request, "input") ?? ClaudeJson.Empty;
                _approvals[id] = input;
                sink.Report(state.SessionId, new ApprovalRequestedReport(id, DateTime.UtcNow,
                    ClaudeJson.Str(request, "tool_name") ?? "tool", ClaudeJson.Str(request, "tool_use_id"),
                    input.GetRawText(), ClaudeJson.Str(request, "description")));
                sink.Report(state.SessionId, new StatusReport(SessionStatus.WaitingForApproval));
                break; // answered later by AnswerAsync
            case "hook_callback":
                await transport.RespondAsync(id, ClaudeJson.Empty); // no opinion
                break;
            default:
                await transport.RespondErrorAsync(id, $"BoSoFa does not support {subtype} yet.");
                break;
        }
    }

    async Task PumpAsync(IClaudeTransport transport)
    {
        try
        {
            await foreach (var line in transport.Messages.ReadAllAsync())
                try { Translate(line); }
                catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException)
                {
                    log.WriteLine($"Session {state.SessionId}: skipped a line it could not read: {exception.Message}");
                }
        }
        finally { await OnExitedAsync(transport); }
    }

    async Task OnExitedAsync(IClaudeTransport transport)
    {
        // Whoever clears the field owns the shutdown: a stop (or dispose) already did, or the process ended by itself.
        if (Interlocked.CompareExchange(ref _transport, null, transport) != transport) return;
        foreach (var key in _approvals.Keys)
            if (_approvals.TryRemove(key, out _)) sink.Report(state.SessionId, new ApprovalClosedReport(key, "exited"));
        // Exiting between turns is fine: the next message resumes. Exiting during a turn is reported.
        var wasRunning = _turnRunning;
        _turnRunning = false;
        var code = transport.ExitCode;
        if (wasRunning || code is not (null or 0))
            sink.Report(state.SessionId, new StatusReport(SessionStatus.Idle,
                Error: $"Claude Code exited (code {code?.ToString() ?? "unknown"}). {transport.StandardErrorTail}".Trim()));
        await transport.DisposeAsync();
    }

    void Translate(JsonElement line)
    {
        var now = DateTime.UtcNow;
        switch (ClaudeJson.Str(line, "type"))
        {
            case "system":
                switch (ClaudeJson.Str(line, "subtype"))
                {
                    case "init" when ClaudeJson.Str(line, "session_id") is { } id && id != state.HarnessSessionId:
                        sink.Report(state.SessionId, new StatusReport(_turnRunning ? SessionStatus.Running : SessionStatus.Idle,
                            HarnessSessionId: id, Model: ClaudeJson.Str(line, "model")));
                        break;
                    case "session_state_changed":
                        switch (ClaudeJson.Str(line, "state"))
                        {
                            case "running": sink.Report(state.SessionId, new StatusReport(SessionStatus.Running)); break;
                            case "requires_action": sink.Report(state.SessionId, new StatusReport(SessionStatus.WaitingForApproval)); break;
                        }
                        break;
                }
                break;
            case "stream_event":
                TranslateStreamEvent(line);
                break;
            case "assistant":
                TranslateAssistant(line, now);
                break;
            case "user":
                if (!ClaudeJson.Bool(line, "isReplay")) TranslateUser(line, now); // our own message, already stored
                break;
            case "result":
                TranslateResult(line, now);
                break;
        }
    }

    void TranslateStreamEvent(JsonElement line)
    {
        if (ClaudeJson.Obj(line, "event") is not { } ev) return;
        switch (ClaudeJson.Str(ev, "type"))
        {
            case "message_start":
                _messageId = ClaudeJson.Obj(ev, "message") is { } message ? ClaudeJson.Str(message, "id") : null;
                break;
            case "content_block_start":
                _messageId = ClaudeJson.Str(line, "api_message_id") ?? _messageId;
                _blockIndex = ClaudeJson.Long(ev, "index");
                break;
            case "content_block_delta":
                if (ClaudeJson.Obj(ev, "delta") is { } delta && ClaudeJson.Str(delta, "type") == "text_delta" &&
                    ClaudeJson.Str(delta, "text") is { Length: > 0 } text && _messageId != null)
                    sink.Delta(state.SessionId, $"{_messageId}:{ClaudeJson.Long(ev, "index")}", text);
                break;
        }
    }

    void TranslateAssistant(JsonElement line, DateTime now)
    {
        if (ClaudeJson.Obj(line, "message") is not { } message ||
            ClaudeJson.Obj(message, "content") is not { ValueKind: JsonValueKind.Array } content) return;
        var messageId = ClaudeJson.Str(message, "id");
        // One complete message per content block, right after that block streamed: it takes the block's delta ref.
        var itemRef = messageId != null && messageId == _messageId ? $"{messageId}:{_blockIndex}" : messageId;
        foreach (var block in content.EnumerateArray())
            switch (ClaudeJson.Str(block, "type"))
            {
                case "text" when ClaudeJson.Str(block, "text") is { Length: > 0 } text:
                    sink.Report(state.SessionId, new ItemReport(SessionItemKind.Assistant, now, text, Ref: itemRef));
                    break;
                case "thinking" when ClaudeJson.Str(block, "thinking") is { Length: > 0 } thinking:
                    sink.Report(state.SessionId, new ItemReport(SessionItemKind.Thinking, now, thinking, Ref: itemRef));
                    break;
                case "tool_use":
                    var input = ClaudeJson.Obj(block, "input") ?? ClaudeJson.Empty;
                    sink.Report(state.SessionId, new ItemReport(SessionItemKind.ToolUse, now, ToolSummary(input),
                        ClaudeJson.Str(block, "id"), ClaudeJson.Str(block, "name"), input.GetRawText(), Ref: itemRef));
                    break;
            }
    }

    /// <summary>A short line for the transcript: the most telling argument of common tools.</summary>
    static string ToolSummary(JsonElement input)
    {
        foreach (var name in new[] { "command", "file_path", "path", "pattern", "url", "query", "description" })
            if (ClaudeJson.Str(input, name) is { Length: > 0 } value)
                return value.Length <= 200 ? value : value[..199] + "…";
        return "";
    }

    void TranslateUser(JsonElement line, DateTime now)
    {
        if (ClaudeJson.Obj(line, "message") is not { } message || ClaudeJson.Obj(message, "content") is not { } content) return;
        if (content.ValueKind == JsonValueKind.String)
        {
            sink.Report(state.SessionId, new ItemReport(SessionItemKind.Notice, now, content.GetString() ?? ""));
            return;
        }
        if (content.ValueKind != JsonValueKind.Array) return;
        foreach (var block in content.EnumerateArray())
            switch (ClaudeJson.Str(block, "type"))
            {
                case "tool_result":
                    sink.Report(state.SessionId, new ItemReport(SessionItemKind.ToolResult, now,
                        Inline(ContentText(ClaudeJson.Obj(block, "content"))), ClaudeJson.Str(block, "tool_use_id"),
                        IsError: ClaudeJson.Bool(block, "is_error")));
                    break;
                case "text" when ClaudeJson.Str(block, "text") is { Length: > 0 } text:
                    sink.Report(state.SessionId, new ItemReport(SessionItemKind.Notice, now, text));
                    break;
            }
    }

    static string ContentText(JsonElement? content)
    {
        if (content is not { } value) return "";
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? "";
        if (value.ValueKind != JsonValueKind.Array) return value.GetRawText();
        var text = new StringBuilder();
        foreach (var part in value.EnumerateArray())
        {
            if (text.Length > 0) text.Append('\n');
            text.Append(ClaudeJson.Str(part, "text") ?? $"[{ClaudeJson.Str(part, "type") ?? "content"}]");
        }
        return text.ToString();
    }

    static string Inline(string text) => text.Length <= ItemReport.InlineLimit
        ? text
        : text[..ItemReport.InlineLimit] + $"\n… ({text.Length - ItemReport.InlineLimit} more characters)";

    void TranslateResult(JsonElement line, DateTime now)
    {
        _turnRunning = false;
        // total_cost_usd is cumulative for the process; the per-turn cost is in model_usage_since_last_result.
        double cost = 0;
        if (ClaudeJson.Obj(line, "model_usage_since_last_result") is { ValueKind: JsonValueKind.Object } usageByModel)
            foreach (var model in usageByModel.EnumerateObject())
                if (model.Value.ValueKind == JsonValueKind.Object && model.Value.TryGetProperty("costUSD", out var c) &&
                    c.ValueKind == JsonValueKind.Number)
                    cost += c.GetDouble();
        var usage = ClaudeJson.Obj(line, "usage") ?? ClaudeJson.Empty;
        var isError = ClaudeJson.Bool(line, "is_error");
        var outcome = ClaudeJson.Str(line, "terminal_reason") ?? ClaudeJson.Str(line, "subtype") ?? "ended";
        foreach (var key in _approvals.Keys)
            if (_approvals.TryRemove(key, out _)) sink.Report(state.SessionId, new ApprovalClosedReport(key, "turn ended"));
        sink.Report(state.SessionId, new RunEndedReport(now, outcome, isError, cost,
            ClaudeJson.Long(usage, "input_tokens") + ClaudeJson.Long(usage, "cache_creation_input_tokens") +
            ClaudeJson.Long(usage, "cache_read_input_tokens"),
            ClaudeJson.Long(usage, "output_tokens"), ClaudeJson.Long(line, "duration_ms")));
        if (isError && ClaudeJson.Str(line, "result") is { Length: > 0 } error && outcome != "aborted_streaming")
            sink.Report(state.SessionId, new ItemReport(SessionItemKind.Notice, now, error, IsError: true));
        sink.Report(state.SessionId, new StatusReport(SessionStatus.Idle));
    }

    async Task DisposeTransportAsync()
    {
        var transport = Interlocked.Exchange(ref _transport, null);
        if (transport != null) await transport.DisposeAsync();
        if (_pump != null)
            try { await _pump; }
            catch (Exception) { /* already reported */ }
    }

    public ValueTask DisposeAsync() => new(DisposeTransportAsync());
}
