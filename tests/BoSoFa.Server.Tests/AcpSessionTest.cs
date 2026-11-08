using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BoSoFa.Daemon.Harnesses;
using BoSoFa.Daemon.Harnesses.Acp;
using BoSoFa.Daemon.Sessions;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Tests;

public class AcpSessionTest
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    sealed class RecordingSink : ISessionSink
    {
        public ConcurrentQueue<SessionReport> Reports { get; } = new();
        public ConcurrentQueue<(string Ref, string Text)> Deltas { get; } = new();
        public void Report(ulong sessionId, SessionReport report) => Reports.Enqueue(report);
        public void Delta(ulong sessionId, string itemRef, string text) => Deltas.Enqueue((itemRef, text));
    }

    /// <summary>Plays an ACP agent over pipes.</summary>
    sealed class FakeAgent : IAsyncDisposable
    {
        readonly AnonymousPipeServerStream _toServer = new(PipeDirection.Out), _fromServer = new(PipeDirection.In);
        readonly StreamReader _reader;
        readonly StreamWriter _writer;
        readonly Task _running;
        readonly TaskCompletionSource<JsonElement> _permission = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _prompts;

        public FakeAgent()
        {
            _reader = new StreamReader(new AnonymousPipeClientStream(PipeDirection.In, _toServer.ClientSafePipeHandle), new UTF8Encoding(false));
            _writer = new StreamWriter(new AnonymousPipeClientStream(PipeDirection.Out, _fromServer.ClientSafePipeHandle), new UTF8Encoding(false)) { AutoFlush = true };
            Client = JsonRpcProcess.Connect(_toServer, _fromServer, includeVersion: true);
            _running = Task.Run(ServeAsync);
        }

        public JsonRpcProcess Client { get; }
        public ConcurrentQueue<JsonElement> Received { get; } = new();
        public List<string> Prompts { get; } = [];
        public Task<JsonElement> Permission => _permission.Task;

        void Send(JsonObject message)
        {
            message["jsonrpc"] = "2.0";
            lock (_writer) _writer.WriteLine(message.ToJsonString());
        }

        void Update(JsonObject update) => Send(new JsonObject
        {
            ["method"] = "session/update", ["params"] = new JsonObject { ["sessionId"] = "s-1", ["update"] = update }
        });

        async Task ServeAsync()
        {
            while (await _reader.ReadLineAsync() is { } line)
            {
                var message = JsonDocument.Parse(line).RootElement.Clone();
                Received.Enqueue(message);
                var method = message.TryGetProperty("method", out var m) ? m.GetString() : null;
                if (method == null)
                {
                    if (message.GetProperty("id").GetRawText() == "\"perm-1\"") _permission.TrySetResult(message.GetProperty("result"));
                    continue;
                }
                if (method == "session/cancel") _cancelled.TrySetResult();
                if (!message.TryGetProperty("id", out var id)) continue;
                var requestId = JsonNode.Parse(id.GetRawText());
                switch (method)
                {
                    case "initialize":
                        Send(new JsonObject { ["id"] = requestId, ["result"] = new JsonObject { ["protocolVersion"] = 1, ["agentCapabilities"] = new JsonObject { ["loadSession"] = true } } });
                        break;
                    case "session/new":
                        Send(new JsonObject { ["id"] = requestId, ["result"] = new JsonObject { ["sessionId"] = "s-1" } });
                        break;
                    case "session/prompt":
                        var number = ++_prompts;
                        lock (Prompts) Prompts.Add(message.GetProperty("params").GetProperty("prompt")[0].GetProperty("text").GetString()!);
                        _ = Task.Run(() => PlayAsync(number, requestId));
                        break;
                }
            }
            _writer.Dispose();
        }

        async Task PlayAsync(int number, JsonNode? requestId)
        {
            var stop = "end_turn";
            if (number == 1)
            {
                Update(new() { ["sessionUpdate"] = "agent_thought_chunk", ["content"] = new JsonObject { ["type"] = "text", ["text"] = "Hmm." } });
                Update(new() { ["sessionUpdate"] = "agent_message_chunk", ["content"] = new JsonObject { ["type"] = "text", ["text"] = "Editing " } });
                Update(new() { ["sessionUpdate"] = "agent_message_chunk", ["content"] = new JsonObject { ["type"] = "text", ["text"] = "now." } });
                Update(new() { ["sessionUpdate"] = "tool_call", ["toolCallId"] = "call-1", ["title"] = "Edit a.txt", ["kind"] = "edit", ["status"] = "pending", ["rawInput"] = new JsonObject { ["path"] = "a.txt" } });
                Send(new JsonObject
                {
                    ["id"] = "perm-1", ["method"] = "session/request_permission",
                    ["params"] = new JsonObject
                    {
                        ["sessionId"] = "s-1",
                        ["toolCall"] = new JsonObject { ["toolCallId"] = "call-1", ["title"] = "Edit a.txt", ["kind"] = "edit", ["rawInput"] = new JsonObject { ["path"] = "a.txt" } },
                        ["options"] = new JsonArray(
                            new JsonObject { ["optionId"] = "yes", ["name"] = "Allow", ["kind"] = "allow_once" },
                            new JsonObject { ["optionId"] = "no", ["name"] = "Reject", ["kind"] = "reject_once" })
                    }
                });
                await _permission.Task;
                Update(new() { ["sessionUpdate"] = "tool_call_update", ["toolCallId"] = "call-1", ["status"] = "completed", ["content"] = new JsonArray(new JsonObject { ["type"] = "content", ["content"] = new JsonObject { ["type"] = "text", ["text"] = "done" } }) });
                await _cancelled.Task; // this turn runs until it is steered away
                stop = "cancelled";
            }
            Send(new JsonObject { ["id"] = requestId, ["result"] = new JsonObject { ["stopReason"] = stop } });
        }

        public async ValueTask DisposeAsync() => await _running.WaitAsync(Timeout);
    }

    static async Task WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for " + what);
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task StreamsAsksPermissionAndSteeringCancelsThenRunsFirst()
    {
        await using var agent = new FakeAgent();
        var sink = new RecordingSink();
        var state = new SessionState { SessionId = 1, Harness = Harnesses.OpenCode, WorkingDirectory = Path.GetTempPath(), PermissionMode = "default" };
        await using IHarnessSession session = new AcpSession(state, sink, () => Task.FromResult(new AcpLaunch(agent.Client, new JsonArray())), TextWriter.Null);

        await session.SendAsync("first", DispatchMode.Queue);
        await WaitFor(() => sink.Reports.OfType<ApprovalRequestedReport>().Any(), "the permission prompt");
        var approval = sink.Reports.OfType<ApprovalRequestedReport>().Single();
        Assert.Equal(("edit", "call-1", "Edit a.txt"), (approval.ToolName, approval.ToolUseId, approval.Description));
        Assert.Equal(new[] { ("acp-1-1", "Editing "), ("acp-1-1", "now.") }, sink.Deltas.ToArray());
        var items = sink.Reports.OfType<ItemReport>().ToList();
        Assert.Contains(items, item => item is { Kind: SessionItemKind.Thinking, Text: "Hmm." });
        Assert.Contains(items, item => item is { Kind: SessionItemKind.Assistant, Text: "Editing now.", Ref: "acp-1-1" }); // the deltas' ref
        Assert.Contains(sink.Reports, report => report is StatusReport { HarnessSessionId: "s-1" });

        await session.AnswerAsync(approval.Key, allow: true, null);
        var outcome = (await agent.Permission.WaitAsync(Timeout)).GetProperty("outcome");
        Assert.Equal(("selected", "yes"), (outcome.GetProperty("outcome").GetString(), outcome.GetProperty("optionId").GetString()));
        await WaitFor(() => sink.Reports.OfType<ItemReport>().Any(item => item is { Kind: SessionItemKind.ToolResult, Text: "done\n" }), "the tool result");

        await session.SendAsync("later", DispatchMode.Queue);
        await session.SendAsync("now", DispatchMode.Steer);

        await WaitFor(() => sink.Reports.OfType<RunEndedReport>().Count() == 3, "all three turns");
        Assert.Equal(["first", "now", "later"], agent.Prompts); // the steered message goes ahead of the queue
        Assert.Equal(["cancelled", "completed", "completed"], sink.Reports.OfType<RunEndedReport>().Select(run => run.Outcome));
        await WaitFor(() => sink.Reports.OfType<StatusReport>().Last().Status == SessionStatus.Idle, "idle");
        Assert.All(agent.Received, message => Assert.Equal("2.0", message.GetProperty("jsonrpc").GetString()));
    }
}
