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

        public bool AcceptsImages { get; init; }
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
                        Send(new JsonObject { ["id"] = requestId, ["result"] = new JsonObject { ["protocolVersion"] = 1, ["agentCapabilities"] = new JsonObject { ["loadSession"] = true, ["promptCapabilities"] = new JsonObject { ["image"] = AcceptsImages }, ["sessionCapabilities"] = new JsonObject { ["fork"] = new JsonObject(), ["close"] = new JsonObject() } } } });
                        break;
                    case "session/new":
                        Send(new JsonObject { ["id"] = requestId, ["result"] = new JsonObject { ["sessionId"] = "s-1" } });
                        break;
                    case "session/fork":
                        Send(new JsonObject { ["id"] = requestId, ["result"] = new JsonObject { ["sessionId"] = "s-2" } });
                        break;
                    case "session/close":
                        Send(new JsonObject { ["id"] = requestId, ["result"] = new JsonObject() });
                        break;
                    case "session/prompt" when message.GetProperty("params").GetProperty("sessionId").GetString() == "s-2":
                        Send(new JsonObject
                        {
                            ["method"] = "session/update",
                            ["params"] = new JsonObject { ["sessionId"] = "s-2", ["update"] = new JsonObject { ["sessionUpdate"] = "agent_message_chunk", ["content"] = new JsonObject { ["type"] = "text", ["text"] = "On the side." } } }
                        });
                        Send(new JsonObject { ["id"] = requestId, ["result"] = new JsonObject { ["stopReason"] = "end_turn" } });
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImagesGoInlineOnlyToAnAgentThatTakesThem(bool accepts)
    {
        var file = Path.Combine(Path.GetTempPath(), "bosofa-acp-" + Guid.NewGuid().ToString("N") + ".png");
        await File.WriteAllBytesAsync(file, [1, 2, 3]);
        try
        {
            await using var agent = new FakeAgent { AcceptsImages = accepts };
            var sink = new RecordingSink();
            var state = new SessionState { SessionId = 1, Harness = Harnesses.OpenCode, WorkingDirectory = Path.GetTempPath(), PermissionMode = "default" };
            await using IHarnessSession session = new AcpSession(state, sink, () => Task.FromResult(new AcpLaunch(agent.Client, new JsonArray())), TextWriter.Null);
            await session.SendAsync(new HarnessPrompt("look", [new(file, "image/png")]), DispatchMode.Queue);
            await WaitFor(() => agent.Received.Any(message => message.TryGetProperty("method", out var m) && m.GetString() == "session/prompt"), "the prompt");
            var prompt = agent.Received.Single(message => message.TryGetProperty("method", out var m) && m.GetString() == "session/prompt")
                .GetProperty("params").GetProperty("prompt");
            Assert.Equal("look", prompt[0].GetProperty("text").GetString());
            if (accepts)
            {
                Assert.Equal(("image", "image/png", "AQID"), (prompt[1].GetProperty("type").GetString(), prompt[1].GetProperty("mimeType").GetString(), prompt[1].GetProperty("data").GetString()));
                Assert.DoesNotContain(sink.Reports.OfType<ItemReport>(), item => item.Text.Contains("cannot read images"));
            }
            else
            {
                Assert.Equal(1, prompt.GetArrayLength());
                Assert.Contains(sink.Reports.OfType<ItemReport>(), item => item.Text.Contains("cannot read images"));
            }
        }
        finally { File.Delete(file); }
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

    [Fact]
    public async Task ASideQuestionForksTheSessionAndClosesTheFork()
    {
        await using var agent = new FakeAgent();
        var state = new SessionState { SessionId = 1, Harness = Harnesses.OpenCode, WorkingDirectory = Path.GetTempPath(), PermissionMode = "default", HarnessSessionId = "s-1" };
        await using IHarnessSession session = new AcpSession(state, new RecordingSink(), () => Task.FromResult(new AcpLaunch(agent.Client, new JsonArray())), TextWriter.Null);

        Assert.Equal("On the side.", await session.AnswerSideQuestionAsync("What next?", CancellationToken.None));
        string? Method(JsonElement message) => message.TryGetProperty("method", out var m) ? m.GetString() : null;
        Assert.Equal("s-1", agent.Received.Single(message => Method(message) == "session/fork").GetProperty("params").GetProperty("sessionId").GetString());
        Assert.Equal("s-2", agent.Received.Single(message => Method(message) == "session/close").GetProperty("params").GetProperty("sessionId").GetString());
        Assert.Empty(agent.Prompts); // the main session heard nothing
    }

    [Fact]
    public async Task ClearingWaitsItsTurnAndOpensANewSession()
    {
        await using var agent = new FakeAgent();
        var sink = new RecordingSink();
        var state = new SessionState { SessionId = 1, Harness = Harnesses.OpenCode, WorkingDirectory = Path.GetTempPath(), PermissionMode = "bypassPermissions" };
        await using IHarnessSession session = new AcpSession(state, sink, () => Task.FromResult(new AcpLaunch(agent.Client, new JsonArray())), TextWriter.Null);

        await session.SendAsync("first", DispatchMode.Queue);
        await WaitFor(() => sink.Reports.OfType<ItemReport>().Any(item => item is { Kind: SessionItemKind.ToolResult }), "the tool result");
        await session.ClearAsync();
        await session.SendAsync("after", DispatchMode.Queue);
        await session.SendAsync("now", DispatchMode.Steer);

        await WaitFor(() => sink.Reports.OfType<RunEndedReport>().Count() == 3, "all three turns");
        Assert.Equal(["first", "now", "after"], agent.Prompts);
        Assert.Equal(2, agent.Received.Count(message => message.TryGetProperty("method", out var m) && m.GetString() == "session/new"));
        // The line falls between the turns it separates.
        var order = sink.Reports.Where(report => report is RunEndedReport or ItemReport { Kind: SessionItemKind.Cleared })
            .Select(report => report is RunEndedReport ? "run" : "cleared");
        Assert.Equal(["run", "run", "cleared", "run"], order);
        await WaitFor(() => sink.Reports.OfType<StatusReport>().Last().Status == SessionStatus.Idle, "idle");
    }
}
