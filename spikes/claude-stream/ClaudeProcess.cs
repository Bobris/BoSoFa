using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;

namespace ClaudeStreamSpike;

/// <summary>
/// One <c>claude -p --input-format stream-json --output-format stream-json</c> child process: newline-delimited JSON in
/// both directions, with control requests flowing both ways and correlated by <c>request_id</c>.
/// </summary>
internal sealed class ClaudeProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Stream _stdin;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Channel<JsonElement> _messages = Channel.CreateUnbounded<JsonElement>();
    private readonly TextWriter _log;
    private readonly Task _stdoutPump;
    private readonly Task _stderrPump;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _nextRequestId;

    /// <summary>Called for each control request the CLI sends (can_use_tool, hook_callback, mcp_message, ...).</summary>
    public required Func<string, string, JsonElement, Task> OnControlRequest { get; init; }

    public Action<JsonElement>? OnAnyLine { get; init; }

    private ClaudeProcess(Process process, StreamWriter log)
    {
        _process = process;
        _stdin = process.StandardInput.BaseStream;
        _log = TextWriter.Synchronized(log);
        _stdoutPump = Task.Run(PumpStdoutAsync);
        _stderrPump = Task.Run(PumpStderrAsync);
    }

    public ChannelReader<JsonElement> Messages => _messages.Reader;

    public static ClaudeProcess Start(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        string logPath,
        Func<string, string, JsonElement, Task> onControlRequest,
        Action<JsonElement>? onAnyLine)
    {
        var psi = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        // A daemon runs as a service: start from an empty environment and pass only what the CLI needs, so nothing of
        // a parent Claude Code session (CLAUDECODE, CLAUDE_CODE_* of the desktop host, its messaging socket) leaks in.
        psi.Environment.Clear();
        foreach (var (k, v) in environment) psi.Environment[k] = v;

        var log = new StreamWriter(logPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        log.WriteLine($"# {executable} {string.Join(' ', arguments.Select(Quote))}");
        log.WriteLine($"# cwd={workingDirectory}");
        log.WriteLine($"# env={string.Join(' ', environment.Keys.Order())}");
        var process = Process.Start(psi) ?? throw new InvalidOperationException("claude did not start");
        return new ClaudeProcess(process, log) { OnControlRequest = onControlRequest, OnAnyLine = onAnyLine };

        static string Quote(string s) => s.Contains(' ') || s.Contains('{') ? $"'{s}'" : s;
    }

    public async Task SendAsync<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            Log(">", Encoding.UTF8.GetString(bytes));
            await _stdin.WriteAsync(bytes).ConfigureAwait(false);
            await _stdin.WriteAsync("\n"u8.ToArray()).ConfigureAwait(false);
            await _stdin.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Sends a control request and waits for the matching control_response.</summary>
    public async Task<JsonElement> RequestAsync<T>(T request, JsonTypeInfo<ControlRequestLine<T>> typeInfo, TimeSpan timeout)
    {
        var id = $"bsf_{Interlocked.Increment(ref _nextRequestId)}";
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await SendAsync(new ControlRequestLine<T>(id, request), typeInfo).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(timeout).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task RespondAsync(string requestId, JsonElement? response)
        => SendAsync(new ControlResponseLine(new ControlResponseBody("success", requestId, response ?? Empty, null)),
            SpikeJson.Default.ControlResponseLine);

    public Task RespondErrorAsync(string requestId, string error)
        => SendAsync(new ControlResponseLine(new ControlResponseBody("error", requestId, null, error)),
            SpikeJson.Default.ControlResponseLine);

    public static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new EmptyObject(), SpikeJson.Default.EmptyObject);

    public void Log(string direction, string line) => _log.WriteLine($"{_clock.ElapsedMilliseconds,7} {direction} {line}");

    private async Task PumpStdoutAsync()
    {
        var reader = _process.StandardOutput;
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                Log("<", line);
                var trimmed = line.AsSpan().TrimStart();
                if (trimmed.IsEmpty || trimmed[0] != '{') continue; // non-JSON diagnostics are skipped
                JsonElement root;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    root = doc.RootElement.Clone();
                }
                catch (JsonException)
                {
                    continue;
                }

                OnAnyLine?.Invoke(root);
                switch (Str(root, "type"))
                {
                    case "control_response":
                        var resp = root.GetProperty("response");
                        if (Str(resp, "request_id") is { } rid && _pending.TryRemove(rid, out var tcs))
                        {
                            if (Str(resp, "subtype") == "error") tcs.TrySetException(new InvalidOperationException(Str(resp, "error")));
                            else tcs.TrySetResult(resp.TryGetProperty("response", out var p) ? p : Empty);
                        }

                        break;
                    case "control_request":
                        var reqId = Str(root, "request_id")!;
                        var req = root.GetProperty("request");
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await OnControlRequest(reqId, Str(req, "subtype") ?? "", req).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                await RespondErrorAsync(reqId, ex.Message).ConfigureAwait(false);
                            }
                        });
                        break;
                    default:
                        await _messages.Writer.WriteAsync(root).ConfigureAwait(false);
                        break;
                }
            }
        }
        finally
        {
            _messages.Writer.TryComplete();
            foreach (var (_, tcs) in _pending) tcs.TrySetException(new IOException("claude exited"));
        }
    }

    private async Task PumpStderrAsync()
    {
        while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line) Log("!", line);
    }

    public static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public async ValueTask DisposeAsync()
    {
        // Closing stdin is the graceful shutdown: the CLI finishes and exits by itself.
        try { _stdin.Close(); } catch (IOException) { }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _process.Kill(entireProcessTree: true);
        }

        await Task.WhenAll(_stdoutPump, _stderrPump).ConfigureAwait(false);
        Log("#", $"exit code {_process.ExitCode}");
        await _log.DisposeAsync().ConfigureAwait(false);
        _process.Dispose();
    }
}
