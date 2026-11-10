using System.Text.Json;
using System.Threading.Channels;
using BoSoFa.Daemon.Harnesses.Claude;
using BoSoFa.Daemon.Sessions;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Tests;

public class HarnessTest
{
    /// <summary>Records what the adapter asks of the CLI; never produces output, so a turn stays running.</summary>
    sealed class RecordingTransport : IClaudeTransport
    {
        readonly Channel<JsonElement> _messages = Channel.CreateUnbounded<JsonElement>();
        public List<string> Calls { get; } = [];
        public ChannelReader<JsonElement> Messages => _messages.Reader;
        /// <summary>A line of the CLI's output.</summary>
        public void Emit(string json) => _messages.Writer.TryWrite(JsonDocument.Parse(json).RootElement.Clone());
        public Func<string, string, JsonElement, Task>? OnControlRequest { get; set; }
        public Action<string>? OnControlCancel { get; set; }
        public int? ExitCode => null;
        public string StandardErrorTail => "";

        public Task SendUserAsync(HarnessPrompt prompt)
        {
            lock (Calls) Calls.Add("user:" + prompt.Text);
            return Task.CompletedTask;
        }

        public Task<JsonElement> InitializeAsync(TimeSpan timeout) => Task.FromResult(default(JsonElement));

        public Task<JsonElement> InterruptAsync(TimeSpan timeout)
        {
            lock (Calls) Calls.Add("interrupt");
            return Task.FromResult(default(JsonElement));
        }

        public Task RespondAsync(string requestId, JsonElement response) => Task.CompletedTask;
        public Task RespondErrorAsync(string requestId, string error) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            _messages.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    sealed class NullSink : ISessionSink
    {
        public void Report(ulong sessionId, SessionReport report) { }
        public void Delta(ulong sessionId, string itemRef, string text) { }
    }

    sealed class RecordingSink : ISessionSink
    {
        public System.Collections.Concurrent.ConcurrentQueue<SessionReport> Reports { get; } = new();
        public void Report(ulong sessionId, SessionReport report) => Reports.Enqueue(report);
        public void Delta(ulong sessionId, string itemRef, string text) { }
    }

    /// <summary>Claude Code takes /compact as a prompt, then says where it compacted and goes on from its own summary.</summary>
    [Fact]
    public async Task CompactingClaudeSendsItsCommandAndShowsTheSummary()
    {
        var transport = new RecordingTransport();
        var sink = new RecordingSink();
        var state = new SessionState { SessionId = 1, Harness = Harnesses.Claude, WorkingDirectory = Path.GetTempPath(), PermissionMode = "default" };
        await using IHarnessSession session = new ClaudeSession(state, sink, _ => Task.FromResult<IClaudeTransport>(transport), TextWriter.Null);

        await session.CompactAsync();
        Assert.Equal(["user:/compact"], transport.Calls);
        transport.Emit("""{"type":"system","subtype":"compact_boundary","compact_metadata":{"trigger":"manual","pre_tokens":25765}}""");
        transport.Emit("""{"type":"user","isCompactSummary":true,"message":{"role":"user","content":"This session is being continued from a previous conversation. Summary: the code word is periwinkle."}}""");
        transport.Emit("""{"type":"result","subtype":"success","is_error":false,"duration_ms":4000}""");

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!sink.Reports.OfType<RunEndedReport>().Any() && DateTime.UtcNow < deadline) await Task.Delay(10);
        var items = sink.Reports.OfType<ItemReport>().ToList();
        Assert.Contains(items, item => item is { Kind: SessionItemKind.Notice, Text: "The conversation was compacted (25,765 tokens before)." });
        Assert.Contains("periwinkle", items.Single(item => item.Kind == SessionItemKind.Compaction).Text);
        Assert.Equal("completed", sink.Reports.OfType<RunEndedReport>().Single().Outcome);
    }

    [Fact]
    public async Task SteeringARunningClaudeTurnInterruptsItBeforeSending()
    {
        var transport = new RecordingTransport();
        var state = new SessionState
        {
            SessionId = 1, Harness = Harnesses.Claude, WorkingDirectory = Path.GetTempPath(), PermissionMode = "default"
        };
        await using IHarnessSession session = new ClaudeSession(state, new NullSink(), _ => Task.FromResult<IClaudeTransport>(transport), TextWriter.Null);

        await session.SendAsync("first", DispatchMode.Steer); // nothing runs yet: steering is a plain send
        await session.SendAsync("queued", DispatchMode.Queue); // the CLI queues it behind the running turn
        await session.SendAsync("steered", DispatchMode.Steer);

        Assert.Equal(["user:first", "user:queued", "interrupt", "user:steered"], transport.Calls);
    }
}
