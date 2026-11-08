using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace BoSoFa.Daemon.Harnesses.Claude;

/// <summary>
/// Plays a recorded Claude Code stdout transcript (JSON lines) through the same adapter, for tests and demos without
/// a model (plan §5.4 replay adapter). Lines with a <c>replay</c> field are directives:
/// <list type="bullet">
/// <item><c>{"replay":"await_user"}</c> waits for the next user message;</item>
/// <item><c>{"replay":"await_answer"}</c> waits until the last <c>can_use_tool</c> request was answered;</item>
/// <item><c>{"replay":"await_signal","name":"x","ms":2000}</c> waits for <see cref="Release"/> or the timeout;</item>
/// <item><c>{"replay":"delay","ms":100}</c>.</item>
/// </list>
/// <c>{{run}}</c> anywhere in a line becomes a number unique to this replay, for IDs that must not repeat (request
/// IDs). The replay ends after the last line, like a process that exited with code 0.
/// </summary>
public sealed class ReplayTransport : IClaudeTransport
{
    static readonly ConcurrentDictionary<string, TaskCompletionSource> Signals = new();
    static int _runs;

    readonly Channel<JsonElement> _messages = Channel.CreateUnbounded<JsonElement>();
    readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    readonly SemaphoreSlim _userMessages = new(0);
    readonly ConcurrentDictionary<string, TaskCompletionSource> _answers = new();
    readonly CancellationTokenSource _stop = new();
    readonly Task _playing;

    /// <summary>Replaces the timeout of every <c>await_signal</c>, so tests can wait as long as they need.</summary>
    public static TimeSpan? SignalTimeoutOverride { get; set; }

    public ReplayTransport(string fixturePath)
    {
        var run = Interlocked.Increment(ref _runs).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var lines = File.ReadAllLines(fixturePath).Select(line => line.Replace("{{run}}", run)).ToArray();
        _playing = Task.Run(() => PlayAsync(lines));
    }

    public static void Release(string name) =>
        Signals.GetOrAdd(name, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    public static void Reset(string name) => Signals.TryRemove(name, out _);

    public ChannelReader<JsonElement> Messages => _messages.Reader;
    public Func<string, string, JsonElement, Task>? OnControlRequest { get; set; }
    public Action<string>? OnControlCancel { get; set; }
    public int? ExitCode => _playing.IsCompleted ? 0 : null;
    public string StandardErrorTail => "";

    public Task SendUserAsync(string text)
    {
        _userMessages.Release();
        return Task.CompletedTask;
    }

    public Task<JsonElement> InitializeAsync(TimeSpan timeout) => Task.FromResult(ClaudeJson.Empty);
    public Task<JsonElement> InterruptAsync(TimeSpan timeout) => Task.FromResult(ClaudeJson.Empty);

    public Task RespondAsync(string requestId, JsonElement response)
    {
        if (_answers.TryRemove(requestId, out var answered)) answered.TrySetResult();
        return Task.CompletedTask;
    }

    public Task RespondErrorAsync(string requestId, string error) => RespondAsync(requestId, ClaudeJson.Empty);

    async Task PlayAsync(string[] lines)
    {
        var cancellation = _stop.Token;
        TaskCompletionSource? lastQuestion = null;
        try
        {
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement.Clone();
                switch (ClaudeJson.Str(root, "replay"))
                {
                    case "await_user":
                        await _userMessages.WaitAsync(cancellation);
                        continue;
                    case "await_answer":
                        if (lastQuestion != null) await lastQuestion.Task.WaitAsync(cancellation);
                        continue;
                    case "await_signal":
                        var signal = Signals.GetOrAdd(ClaudeJson.Str(root, "name") ?? "",
                            _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
                        try
                        {
                            await signal.Task.WaitAsync(
                                SignalTimeoutOverride ?? TimeSpan.FromMilliseconds(ClaudeJson.Long(root, "ms")), cancellation);
                        }
                        catch (TimeoutException) { }
                        continue;
                    case "delay":
                        await Task.Delay(TimeSpan.FromMilliseconds(ClaudeJson.Long(root, "ms")), cancellation);
                        continue;
                }
                if (ClaudeJson.Str(root, "type") == "control_request" &&
                    ClaudeJson.Str(ClaudeJson.Obj(root, "request") ?? ClaudeJson.Empty, "subtype") == "can_use_tool")
                {
                    lastQuestion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    _answers[ClaudeJson.Str(root, "request_id") ?? ""] = lastQuestion;
                }
                ClaudeLines.Dispatch(root, _pending, OnControlRequest, OnControlCancel, RespondErrorAsync, _messages.Writer);
            }
        }
        catch (OperationCanceledException) { }
        finally { _messages.Writer.TryComplete(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _playing;
        _stop.Dispose();
    }
}
