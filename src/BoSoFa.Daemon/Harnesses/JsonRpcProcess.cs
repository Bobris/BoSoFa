using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace BoSoFa.Daemon.Harnesses;

/// <summary>A JSON-RPC message from the process: a notification (no id) or a request it sends us (with an id).
/// <c>Reached</c> marks a position in the stream instead (see <see cref="JsonRpcProcess.DrainAsync"/>).</summary>
public sealed record JsonRpcIncoming(string Method, JsonElement Params, JsonElement? Id, Action? Reached = null);

/// <summary>The error a JSON-RPC response carried.</summary>
public sealed class JsonRpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// A harness that speaks JSON-RPC 2.0 over newline-delimited stdio (Codex app-server, ACP agents). Requests are
/// correlated by id. Notifications and requests from the process share one ordered queue, <see cref="Incoming"/>: a
/// request must not overtake the updates sent before it (an approval refers to an item announced just earlier). The
/// consumer hands requests to <see cref="Serve"/>, which answers them concurrently, as a process may ask while it
/// still owes us an answer. The child starts from the environment it is given and nothing else.
/// </summary>
public sealed class JsonRpcProcess : IAsyncDisposable
{
    readonly Process? _process;
    readonly StreamReader _output, _error;
    readonly Stream _stdin;
    readonly bool _includeVersion;
    readonly SemaphoreSlim _write = new(1, 1);
    readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    readonly Channel<JsonRpcIncoming> _notifications = Channel.CreateUnbounded<JsonRpcIncoming>();
    readonly Queue<string> _stderr = new();
    readonly Task _stdout, _stderrPump;
    long _nextId;

    /// <param name="includeVersion">Write <c>"jsonrpc":"2.0"</c>: ACP requires it, Codex's envelope has no such field.</param>
    JsonRpcProcess(Process? process, Stream input, StreamReader output, StreamReader error, bool includeVersion)
    {
        _process = process;
        _stdin = input;
        _output = output;
        _error = error;
        _includeVersion = includeVersion;
        _stdout = Task.Run(PumpStdoutAsync);
        _stderrPump = Task.Run(PumpStderrAsync);
    }

    /// <summary>A connection over given streams, for tests that play the server in-process.</summary>
    public static JsonRpcProcess Connect(Stream toServer, Stream fromServer, bool includeVersion) =>
        new(null, toServer, new StreamReader(fromServer, new UTF8Encoding(false)), new StreamReader(Stream.Null), includeVersion);

    /// <summary>Answers a request from the process; the result is serialized as the response's <c>result</c>.</summary>
    public Func<JsonRpcIncoming, Task<JsonNode?>>? OnRequest { get; set; }

    /// <summary>Notifications and requests from the process, in order; completes when it exits.</summary>
    public ChannelReader<JsonRpcIncoming> Incoming => _notifications.Reader;

    /// <summary>Completes once the consumer of <see cref="Incoming"/> has handled everything received so far. A response
    /// can overtake the notifications before it in that queue; a turn that ends with a response waits for this.</summary>
    public Task DrainAsync()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_notifications.Writer.TryWrite(new("", default, null, reached.SetResult))) reached.TrySetResult();
        return reached.Task;
    }

    /// <summary>Answers a request taken from <see cref="Incoming"/>, without blocking the consumer.</summary>
    public void Serve(JsonRpcIncoming request)
    {
        var id = request.Id!.Value;
        _ = Task.Run(async () =>
        {
            try
            {
                if (OnRequest is not { } handler) await RespondErrorAsync(id, -32601, $"{request.Method} is not supported.");
                else await RespondAsync(id, await handler(request));
            }
            catch (Exception exception)
            {
                try { await RespondErrorAsync(id, -32603, exception.Message); }
                catch (Exception) { /* the process is gone */ }
            }
        });
    }

    public string StandardErrorTail
    {
        get { lock (_stderr) return string.Join('\n', _stderr); }
    }

    /// <summary>Waits briefly: stdout can close a moment before the process is reaped.</summary>
    public int? ExitCode => _process == null ? 0 : _process.WaitForExit(TimeSpan.FromSeconds(2)) ? _process.ExitCode : null;

    public static JsonRpcProcess Start(string executable, IEnumerable<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string> environment, bool includeVersion)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach (var (name, value) in environment) start.Environment[name] = value;
        var process = Process.Start(start) ?? throw new InvalidOperationException($"{executable} did not start.");
        return new(process, process.StandardInput.BaseStream, process.StandardOutput, process.StandardError, includeVersion);
    }

    /// <summary>Sends a request and waits for its response's <c>result</c>.</summary>
    public async Task<JsonElement> RequestAsync(string method, JsonNode? parameters, TimeSpan timeout,
        CancellationToken cancellation = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        try
        {
            await WriteAsync(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters });
            return await answer.Task.WaitAsync(timeout, cancellation);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public Task NotifyAsync(string method, JsonNode? parameters = null) =>
        WriteAsync(new JsonObject { ["method"] = method, ["params"] = parameters });

    Task RespondAsync(JsonElement id, JsonNode? result) =>
        WriteAsync(new JsonObject { ["id"] = JsonNode.Parse(id.GetRawText()), ["result"] = result ?? new JsonObject() });

    Task RespondErrorAsync(JsonElement id, int code, string message) => WriteAsync(new JsonObject
    {
        ["id"] = JsonNode.Parse(id.GetRawText()),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    });

    async Task WriteAsync(JsonObject message)
    {
        if (_includeVersion) message["jsonrpc"] = "2.0";
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _write.WaitAsync();
        try
        {
            await _stdin.WriteAsync(bytes);
            await _stdin.WriteAsync("\n"u8.ToArray());
            await _stdin.FlushAsync();
        }
        finally { _write.Release(); }
    }

    async Task PumpStdoutAsync()
    {
        try
        {
            while (await _output.ReadLineAsync() is { } line)
            {
                if (line.AsSpan().TrimStart() is not ['{', ..]) continue; // diagnostics some builds print on stdout
                JsonElement root;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    root = document.RootElement.Clone();
                }
                catch (JsonException) { continue; }
                Dispatch(root);
            }
        }
        catch (IOException) { }
        finally
        {
            _notifications.Writer.TryComplete();
            foreach (var (_, pending) in _pending) pending.TrySetException(new IOException("The process exited."));
        }
    }

    void Dispatch(JsonElement root)
    {
        var hasId = root.TryGetProperty("id", out var id) && id.ValueKind != JsonValueKind.Null;
        var method = root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        var parameters = root.TryGetProperty("params", out var p) ? p : default;
        if (method == null)
        {
            // A response to one of our requests.
            if (!hasId || !id.TryGetInt64(out var number) || !_pending.TryRemove(number, out var waiter)) return;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                waiter.TrySetException(new JsonRpcException(
                    error.TryGetProperty("code", out var code) && code.TryGetInt32(out var c) ? c : 0,
                    error.TryGetProperty("message", out var text) ? text.GetString() ?? "" : ""));
            else waiter.TrySetResult(root.TryGetProperty("result", out var result) ? result : default);
            return;
        }
        _notifications.Writer.TryWrite(new JsonRpcIncoming(method, parameters, hasId ? id : null));
    }

    async Task PumpStderrAsync()
    {
        try
        {
            while (await _error.ReadLineAsync() is { } line)
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
        // Closing stdin ends a well-behaved server; otherwise it is killed after a grace period.
        try { _stdin.Close(); }
        catch (IOException) { }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (_process != null)
            try { await _process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { _process.Kill(entireProcessTree: true); }
        await Task.WhenAll(_stdout, _stderrPump);
        _process?.Dispose();
    }
}
