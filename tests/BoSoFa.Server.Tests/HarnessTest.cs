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
        public Func<string, string, JsonElement, Task>? OnControlRequest { get; set; }
        public Action<string>? OnControlCancel { get; set; }
        public int? ExitCode => null;
        public string StandardErrorTail => "";

        public Task SendUserAsync(string text)
        {
            lock (Calls) Calls.Add("user:" + text);
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

    [Fact]
    public async Task SteeringARunningClaudeTurnInterruptsItBeforeSending()
    {
        var transport = new RecordingTransport();
        var state = new SessionState
        {
            SessionId = 1, Harness = Harnesses.Claude, WorkingDirectory = Path.GetTempPath(), PermissionMode = "default"
        };
        await using IHarnessSession session = new ClaudeSession(state, new NullSink(), _ => transport, TextWriter.Null);

        await session.SendAsync("first", DispatchMode.Steer); // nothing runs yet: steering is a plain send
        await session.SendAsync("queued", DispatchMode.Queue); // the CLI queues it behind the running turn
        await session.SendAsync("steered", DispatchMode.Steer);

        Assert.Equal(["user:first", "user:queued", "interrupt", "user:steered"], transport.Calls);
    }
}
