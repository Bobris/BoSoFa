using BoSoFa.Daemon.Sessions;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;

namespace BoSoFa.Daemon.Harnesses.Claude;

/// <summary>How to start (or resume) one Claude Code conversation.</summary>
/// <summary>How to start (or resume) one Claude Code conversation. <c>Environment</c> adds to (and overrides) the clean
/// environment: the leased account's secret and its own <c>CLAUDE_CONFIG_DIR</c>.</summary>
public sealed record ClaudeLaunch(string WorkingDirectory, string PermissionMode, string? Model, string? ResumeSessionId,
    IReadOnlyDictionary<string, string>? Environment = null, string? McpConfigJson = null, string? PluginDirectory = null,
    string[]? AllowedTools = null);

/// <summary>The stream-json conversation with one Claude Code process, or a replay of one.</summary>
public interface IClaudeTransport : IAsyncDisposable
{
    /// <summary>Messages (everything except control traffic); completes when the process ends.</summary>
    ChannelReader<JsonElement> Messages { get; }

    /// <summary>Control requests from the CLI: request ID, subtype, the request object. Answer each one.</summary>
    Func<string, string, JsonElement, Task>? OnControlRequest { get; set; }

    /// <summary>The CLI no longer needs an answer to this request.</summary>
    Action<string>? OnControlCancel { get; set; }

    int? ExitCode { get; }
    string StandardErrorTail { get; }

    Task SendUserAsync(HarnessPrompt prompt);
    Task<JsonElement> InitializeAsync(TimeSpan timeout);
    Task<JsonElement> InterruptAsync(TimeSpan timeout);
    Task RespondAsync(string requestId, JsonElement response);
    Task RespondErrorAsync(string requestId, string error);
}

/// <summary>
/// <c>claude -p --input-format stream-json --output-format stream-json</c> as a child process: newline-delimited JSON
/// both ways, control requests in both directions correlated by <c>request_id</c>. The child starts from a clean
/// environment, so nothing of a parent Claude Code session leaks in (spike ④, pitfall 2).
/// </summary>
public sealed class ClaudeProcess : IClaudeTransport
{
    readonly Process _process;
    readonly Stream _stdin;
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    readonly Channel<JsonElement> _messages = Channel.CreateUnbounded<JsonElement>();
    readonly Task _stdoutPump;
    readonly Task _stderrPump;
    readonly Queue<string> _stderr = new();
    int _nextRequestId;

    ClaudeProcess(Process process)
    {
        _process = process;
        _stdin = process.StandardInput.BaseStream;
        _stdoutPump = Task.Run(PumpStdoutAsync);
        _stderrPump = Task.Run(PumpStderrAsync);
    }

    public ChannelReader<JsonElement> Messages => _messages.Reader;
    public Func<string, string, JsonElement, Task>? OnControlRequest { get; set; }
    public Action<string>? OnControlCancel { get; set; }
    /// <summary>Waits briefly: stdout can close a moment before the process is reaped.</summary>
    public int? ExitCode => _process.WaitForExit(TimeSpan.FromSeconds(2)) ? _process.ExitCode : null;

    public string StandardErrorTail
    {
        get { lock (_stderr) return string.Join('\n', _stderr); }
    }

    public static IReadOnlyList<string> Arguments(ClaudeLaunch launch)
    {
        var arguments = new List<string>
        {
            "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
            "--include-partial-messages", "--replay-user-messages", "--permission-prompt-tool", "stdio",
            // Always explicit: a user's settings.json may default to bypassPermissions (spike ④, pitfall 1).
            "--permission-mode", launch.PermissionMode
        };
        if (launch.Model != null) arguments.AddRange(["--model", launch.Model]);
        if (launch.ResumeSessionId != null) arguments.Add($"--resume={launch.ResumeSessionId}");
        if (launch.McpConfigJson != null) arguments.AddRange(["--mcp-config", launch.McpConfigJson]);
        if (launch.AllowedTools is { Length: > 0 } allowed) arguments.Add("--allowedTools=" + string.Join(',', allowed));
        if (launch.PluginDirectory != null) arguments.AddRange(["--plugin-dir", launch.PluginDirectory]);
        return arguments;
    }

    public static ClaudeProcess Start(string executable, ClaudeLaunch launch)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = launch.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in Arguments(launch)) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        // API keys are not passed: `-p` would bill a key it finds silently, so keys come with harness accounts.
        foreach (var (name, value) in HarnessEnvironment.Clean("CLAUDE_CONFIG_DIR", "ANTHROPIC_BASE_URL"))
            start.Environment[name] = value;
        start.Environment["CLAUDE_CODE_EMIT_SESSION_STATE_EVENTS"] = "1";
        start.Environment["CLAUDE_CODE_STARTUP_FAILURE_RESULTS"] = "1";
        start.Environment["CLAUDE_AGENT_SDK_CLIENT_APP"] = $"bosofa/{Cli.Version}";
        start.Environment["DISABLE_AUTOUPDATER"] = "1";
        foreach (var (name, value) in launch.Environment ?? new Dictionary<string, string>()) start.Environment[name] = value;
        return new ClaudeProcess(Process.Start(start) ?? throw new InvalidOperationException("claude did not start."));
    }

    /// <summary>The configured executable, else <c>claude</c> on the PATH or in the native installer's folder.</summary>
    public static string? FindExecutable(string? configured)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return HarnessEnvironment.FindExecutable(configured, OperatingSystem.IsWindows() ? ["claude.exe"] : ["claude"],
            Path.Combine(home, ".local", "bin"), Path.Combine(home, ".claude", "local"));
    }

    async Task SendAsync<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        await _writeLock.WaitAsync();
        try
        {
            await _stdin.WriteAsync(bytes);
            await _stdin.WriteAsync("\n"u8.ToArray());
            await _stdin.FlushAsync();
        }
        finally { _writeLock.Release(); }
    }

    public Task SendUserAsync(HarnessPrompt prompt)
    {
        if (prompt.Images.Count == 0) return SendAsync(new UserLine<string>(new(prompt.Text)), ClaudeJson.Default.UserLineString);
        var blocks = prompt.Images
            .Select(ContentBlock (image) => new ImageBlock(new(image.MediaType, Convert.ToBase64String(File.ReadAllBytes(image.Path)))))
            .Append(new TextBlock(prompt.Text)).ToArray();
        return SendAsync(new UserLine<ContentBlock[]>(new(blocks)), ClaudeJson.Default.UserLineContentBlockArray);
    }

    public Task<JsonElement> InitializeAsync(TimeSpan timeout) =>
        RequestAsync(new InitializeRequest(), ClaudeJson.Default.ControlRequestLineInitializeRequest, timeout);

    public Task<JsonElement> InterruptAsync(TimeSpan timeout) =>
        RequestAsync(new InterruptRequest(), ClaudeJson.Default.ControlRequestLineInterruptRequest, timeout);

    async Task<JsonElement> RequestAsync<T>(T request, JsonTypeInfo<ControlRequestLine<T>> typeInfo, TimeSpan timeout)
    {
        // Host IDs have their own prefix: the CLI echoes our control responses back, and only these IDs are ours.
        var id = $"bsf_{Interlocked.Increment(ref _nextRequestId)}";
        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        try
        {
            await SendAsync(new ControlRequestLine<T>(id, request), typeInfo);
            return await answer.Task.WaitAsync(timeout);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public Task RespondAsync(string requestId, JsonElement response) =>
        SendAsync(new ControlResponseLine(new("success", requestId, response, null)), ClaudeJson.Default.ControlResponseLine);

    public Task RespondErrorAsync(string requestId, string error) =>
        SendAsync(new ControlResponseLine(new("error", requestId, null, error)), ClaudeJson.Default.ControlResponseLine);

    async Task PumpStdoutAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync() is { } line)
            {
                var trimmed = line.AsSpan().TrimStart();
                if (trimmed.IsEmpty || trimmed[0] != '{') continue; // some builds print diagnostics on stdout
                JsonElement root;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    root = document.RootElement.Clone();
                }
                catch (JsonException) { continue; }
                ClaudeLines.Dispatch(root, _pending, OnControlRequest, OnControlCancel, RespondErrorAsync, _messages.Writer);
            }
        }
        catch (IOException) { }
        finally
        {
            _messages.Writer.TryComplete();
            foreach (var (_, pending) in _pending) pending.TrySetException(new IOException("claude exited."));
        }
    }

    async Task PumpStderrAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync() is { } line)
                lock (_stderr)
                {
                    _stderr.Enqueue(line);
                    if (_stderr.Count > 20) _stderr.Dequeue();
                }
        }
        catch (IOException) { }
    }

    public async ValueTask DisposeAsync()
    {
        // Closing stdin is the graceful shutdown: the CLI finishes and exits by itself.
        try { _stdin.Close(); }
        catch (IOException) { }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await _process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { _process.Kill(entireProcessTree: true); }
        await Task.WhenAll(_stdoutPump, _stderrPump);
        _process.Dispose();
    }
}

/// <summary>Routing of inbound lines, shared by the process and the replay.</summary>
static class ClaudeLines
{
    public static void Dispatch(JsonElement root, ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending,
        Func<string, string, JsonElement, Task>? onControlRequest, Action<string>? onControlCancel,
        Func<string, string, Task> respondError, ChannelWriter<JsonElement> messages)
    {
        switch (ClaudeJson.Str(root, "type"))
        {
            case "control_response":
                // Unknown IDs are our own responses echoed back (spike ④, pitfall 3): ignore them.
                if (ClaudeJson.Obj(root, "response") is { } response && ClaudeJson.Str(response, "request_id") is { } id &&
                    pending.TryRemove(id, out var waiter))
                {
                    if (ClaudeJson.Str(response, "subtype") == "error")
                        waiter.TrySetException(new InvalidOperationException(ClaudeJson.Str(response, "error")));
                    else waiter.TrySetResult(ClaudeJson.Obj(response, "response") ?? ClaudeJson.Empty);
                }
                break;
            case "control_request":
                var requestId = ClaudeJson.Str(root, "request_id") ?? "";
                var request = ClaudeJson.Obj(root, "request") ?? ClaudeJson.Empty;
                // Served concurrently: the CLI may ask things (MCP handshakes) before it answers our own initialize.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (onControlRequest == null) await respondError(requestId, "Not supported.");
                        else await onControlRequest(requestId, ClaudeJson.Str(request, "subtype") ?? "", request);
                    }
                    catch (Exception exception)
                    {
                        try { await respondError(requestId, exception.Message); }
                        catch (Exception) { /* the process is gone */ }
                    }
                });
                break;
            case "control_cancel_request":
                if (ClaudeJson.Str(root, "request_id") is { } cancelled) onControlCancel?.Invoke(cancelled);
                break;
            default:
                messages.TryWrite(root);
                break;
        }
    }
}
