using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BoSoFa.Daemon.Harnesses;
using BoSoFa.Daemon.Harnesses.Codex;
using BoSoFa.Daemon.Sessions;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Tests;

public class CodexSessionTest
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    sealed class RecordingSink : ISessionSink
    {
        public ConcurrentQueue<SessionReport> Reports { get; } = new();
        public ConcurrentQueue<(string Ref, string Text)> Deltas { get; } = new();
        public void Report(ulong sessionId, SessionReport report) => Reports.Enqueue(report);
        public void Delta(ulong sessionId, string itemRef, string text) => Deltas.Enqueue((itemRef, text));
    }

    /// <summary>Plays <c>codex app-server</c> over pipes, per the 0.167 protocol.</summary>
    sealed class FakeCodex : IAsyncDisposable
    {
        readonly AnonymousPipeServerStream _toServer = new(PipeDirection.Out), _fromServer = new(PipeDirection.In);
        readonly StreamReader _reader;
        readonly StreamWriter _writer;
        readonly Task _running;
        readonly TaskCompletionSource<JsonElement> _approval = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _finishTurn = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _turns;

        public FakeCodex()
        {
            _reader = new StreamReader(new AnonymousPipeClientStream(PipeDirection.In, _toServer.ClientSafePipeHandle), new UTF8Encoding(false));
            _writer = new StreamWriter(new AnonymousPipeClientStream(PipeDirection.Out, _fromServer.ClientSafePipeHandle), new UTF8Encoding(false)) { AutoFlush = true };
            Client = JsonRpcProcess.Connect(_toServer, _fromServer, includeVersion: false);
            _running = Task.Run(ServeAsync);
        }

        public JsonRpcProcess Client { get; }
        public ConcurrentQueue<JsonElement> Received { get; } = new();

        /// <summary>Approves the sign-in, as the person would in a browser.</summary>
        public void CompleteLogin() => Send(new JsonObject
        {
            ["method"] = "account/login/completed", ["params"] = new JsonObject { ["success"] = true, ["loginId"] = "login-1" }
        });

        public void FinishTurn() => _finishTurn.TrySetResult();
        public Task<JsonElement> Approval => _approval.Task;

        void Send(JsonObject message)
        {
            lock (_writer) _writer.WriteLine(message.ToJsonString());
        }

        void Notify(string method, JsonObject parameters) => Send(new JsonObject { ["method"] = method, ["params"] = parameters });

        async Task ServeAsync()
        {
            while (await _reader.ReadLineAsync() is { } line)
            {
                var message = JsonDocument.Parse(line).RootElement.Clone();
                Received.Enqueue(message);
                var method = message.TryGetProperty("method", out var m) ? m.GetString() : null;
                if (method == null)
                {
                    if (message.GetProperty("id").GetRawText() == "\"approval-1\"") _approval.TrySetResult(message.GetProperty("result"));
                    continue;
                }
                if (!message.TryGetProperty("id", out var id)) continue; // a notification (initialized)
                JsonObject Respond(JsonObject result) => new() { ["id"] = JsonNode.Parse(id.GetRawText()), ["result"] = result };
                switch (method)
                {
                    case "initialize": Send(Respond(new() { ["codexHome"] = "/x", ["platformFamily"] = "unix", ["platformOs"] = "macos", ["userAgent"] = "bosofa/0.167" })); break;
                    case "account/read": Send(Respond(new() { ["account"] = null, ["requiresOpenaiAuth"] = true })); break;
                    case "account/login/start": Send(Respond(new() { ["type"] = "chatgptDeviceCode", ["loginId"] = "login-1", ["verificationUrl"] = "https://auth.example/device", ["userCode"] = "ABCD-EFGH" })); break;
                    case "thread/start": Send(Respond(new() { ["thread"] = new JsonObject { ["id"] = "thread-1" }, ["model"] = "gpt-test" })); break;
                    case "turn/steer": Send(Respond(new() { ["turnId"] = "turn-1" })); break;
                    case "thread/compact/start":
                        Send(Respond(new()));
                        _ = Task.Run(PlayCompactionAsync);
                        break;
                    case "thread/fork": Send(Respond(new() { ["thread"] = new JsonObject { ["id"] = "fork-1" }, ["model"] = "gpt-test" })); break;
                    case "turn/start" when message.GetProperty("params").GetProperty("threadId").GetString() == "fork-1":
                        Send(Respond(new() { ["turn"] = new JsonObject { ["id"] = "side-1", ["status"] = "inProgress" } }));
                        _ = Task.Run(PlaySideTurn);
                        break;
                    case "turn/start":
                        var turn = ++_turns;
                        Send(Respond(new() { ["turn"] = new JsonObject { ["id"] = $"turn-{turn}", ["status"] = "inProgress" } }));
                        _ = Task.Run(() => PlayTurnAsync(turn));
                        break;
                }
            }
            _writer.Dispose();
        }

        async Task PlayTurnAsync(int number)
        {
            var id = $"turn-{number}";
            JsonObject Turn(string status) => new() { ["id"] = id, ["status"] = status, ["items"] = new JsonArray(), ["durationMs"] = 1500 };
            JsonObject Item(string turnId, JsonObject item) => new() { ["threadId"] = "thread-1", ["turnId"] = turnId, ["item"] = item };
            Notify("turn/started", new() { ["threadId"] = "thread-1", ["turn"] = Turn("inProgress") });
            if (number == 1)
            {
                Notify("item/agentMessage/delta", new() { ["itemId"] = "msg-1", ["delta"] = "Running " });
                Notify("item/completed", Item(id, new() { ["id"] = "msg-1", ["type"] = "agentMessage", ["text"] = "Running ls." }));
                Notify("item/started", Item(id, new() { ["id"] = "cmd-1", ["type"] = "commandExecution", ["command"] = "ls", ["cwd"] = "/work", ["status"] = "inProgress" }));
                Send(new JsonObject
                {
                    ["id"] = "approval-1", ["method"] = "item/commandExecution/requestApproval",
                    ["params"] = new JsonObject { ["itemId"] = "cmd-1", ["threadId"] = "thread-1", ["turnId"] = id, ["startedAtMs"] = 1, ["command"] = "ls", ["cwd"] = "/work", ["reason"] = "list files" }
                });
                await _approval.Task;
                await _finishTurn.Task;
                Notify("item/completed", Item(id, new() { ["id"] = "cmd-1", ["type"] = "commandExecution", ["command"] = "ls", ["cwd"] = "/work", ["status"] = "completed", ["aggregatedOutput"] = "a.txt", ["exitCode"] = 0 }));
                Notify("thread/tokenUsage/updated", new() { ["threadId"] = "thread-1", ["turnId"] = id, ["tokenUsage"] = new JsonObject { ["last"] = new JsonObject { ["inputTokens"] = 100, ["outputTokens"] = 7 } } });
            }
            Notify("turn/completed", new() { ["threadId"] = "thread-1", ["turn"] = Turn("completed") });
        }

        /// <summary>Answers a side question on the fork; the main thread hears nothing of it.</summary>
        void PlaySideTurn()
        {
            JsonObject Turn(string status) => new() { ["id"] = "side-1", ["status"] = status, ["items"] = new JsonArray(), ["durationMs"] = 300 };
            Notify("turn/started", new() { ["threadId"] = "fork-1", ["turn"] = Turn("inProgress") });
            Notify("item/completed", new() { ["threadId"] = "thread-1", ["turnId"] = "turn-9", ["item"] = new JsonObject { ["id"] = "x", ["type"] = "agentMessage", ["text"] = "Not this." } });
            Notify("item/completed", new() { ["threadId"] = "fork-1", ["turnId"] = "side-1", ["item"] = new JsonObject { ["id"] = "s-1", ["type"] = "agentMessage", ["text"] = "It lists files." } });
            Notify("turn/completed", new() { ["threadId"] = "fork-1", ["turn"] = Turn("completed") });
        }

        /// <summary>Codex compacts in a turn of its own, with a context compaction item.</summary>
        void PlayCompactionAsync()
        {
            JsonObject Turn(string status) => new() { ["id"] = "compact-1", ["status"] = status, ["items"] = new JsonArray(), ["durationMs"] = 800 };
            Notify("turn/started", new() { ["threadId"] = "thread-1", ["turn"] = Turn("inProgress") });
            Notify("item/completed", new() { ["threadId"] = "thread-1", ["turnId"] = "compact-1", ["item"] = new JsonObject { ["id"] = "c-1", ["type"] = "contextCompaction" } });
            Notify("turn/completed", new() { ["threadId"] = "thread-1", ["turn"] = Turn("completed") });
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
    public async Task SignsInStreamsAsksForApprovalAndQueuesAndSteers()
    {
        await using var server = new FakeCodex();
        var sink = new RecordingSink();
        var state = new SessionState { SessionId = 1, Harness = Harnesses.Codex, WorkingDirectory = Path.GetTempPath(), PermissionMode = "default" };
        await using IHarnessSession session = new CodexSession(state, sink, () => Task.FromResult(new CodexLaunch(server.Client, null)), TextWriter.Null);

        await session.SendAsync("first", DispatchMode.Queue);

        // Not signed in: the device code appears in the transcript and nothing runs until it is approved.
        await WaitFor(() => sink.Reports.OfType<ItemReport>().Any(item => item.Kind == SessionItemKind.Notice), "the device code");
        var notice = sink.Reports.OfType<ItemReport>().Single(item => item.Kind == SessionItemKind.Notice);
        Assert.Contains("https://auth.example/device", notice.Text);
        Assert.Contains("ABCD-EFGH", notice.Text);
        Assert.DoesNotContain(server.Received, message => Method(message) == "turn/start");
        server.CompleteLogin();

        await WaitFor(() => sink.Reports.OfType<ApprovalRequestedReport>().Any(), "the approval request");
        var approval = sink.Reports.OfType<ApprovalRequestedReport>().Single();
        Assert.Equal(("Bash", "cmd-1", "list files"), (approval.ToolName, approval.ToolUseId, approval.Description));
        Assert.Contains("\"command\":\"ls\"", approval.Input);
        Assert.Contains(("msg-1", "Running "), sink.Deltas);
        Assert.Contains(sink.Reports, report => report is StatusReport { HarnessSessionId: "thread-1", Model: "gpt-test" });

        await session.SendAsync("later", DispatchMode.Queue);
        await session.SendAsync("redirect", DispatchMode.Steer);
        await WaitFor(() => server.Received.Any(message => Method(message) == "turn/steer"), "the steer");
        var steer = server.Received.Single(message => Method(message) == "turn/steer").GetProperty("params");
        Assert.Equal("turn-1", steer.GetProperty("expectedTurnId").GetString());
        Assert.Equal("redirect", steer.GetProperty("input")[0].GetProperty("text").GetString());

        await session.AnswerAsync(approval.Key, allow: true, null);
        Assert.Equal("accept", (await server.Approval.WaitAsync(Timeout)).GetProperty("decision").GetString());
        server.FinishTurn();

        // The queued message starts the next turn after this one completed.
        await WaitFor(() => sink.Reports.OfType<RunEndedReport>().Count() == 2, "both turns to end");
        var run = sink.Reports.OfType<RunEndedReport>().First();
        Assert.Equal(("completed", false, 100L, 7L, 1500L), (run.Outcome, run.IsError, run.InputTokens, run.OutputTokens, run.DurationMs));
        var turns = server.Received.Where(message => Method(message) == "turn/start").Select(message =>
            message.GetProperty("params").GetProperty("input")[0].GetProperty("text").GetString()).ToList();
        Assert.Equal(["first", "later"], turns);
        var items = sink.Reports.OfType<ItemReport>().ToList();
        Assert.Contains(items, item => item is { Kind: SessionItemKind.Assistant, Text: "Running ls.", Ref: "msg-1" });
        Assert.Contains(items, item => item is { Kind: SessionItemKind.ToolUse, ToolName: "Bash", Text: "ls" });
        Assert.Contains(items, item => item is { Kind: SessionItemKind.ToolResult, Text: "a.txt", IsError: false });
        await WaitFor(() => sink.Reports.OfType<StatusReport>().Last().Status == SessionStatus.Idle, "idle");

        // The safe defaults went with the thread, and Codex's envelope has no jsonrpc field.
        var thread = server.Received.Single(message => Method(message) == "thread/start").GetProperty("params");
        Assert.Equal(("untrusted", "workspace-write"), (thread.GetProperty("approvalPolicy").GetString(), thread.GetProperty("sandbox").GetString()));
        Assert.All(server.Received, message => Assert.False(message.TryGetProperty("jsonrpc", out _)));
    }

    [Fact]
    public async Task ImagesGoAsLocalFiles()
    {
        await using var server = new FakeCodex();
        var sink = new RecordingSink();
        var state = new SessionState { SessionId = 1, Harness = Harnesses.Codex, WorkingDirectory = Path.GetTempPath(), PermissionMode = "default" };
        await using IHarnessSession session = new CodexSession(state, sink, () => Task.FromResult(new CodexLaunch(server.Client, null)), TextWriter.Null);
        await session.SendAsync(new HarnessPrompt("look", [new("/tmp/a.png", "image/png")]), DispatchMode.Queue);
        await WaitFor(() => sink.Reports.OfType<ItemReport>().Any(item => item.Kind == SessionItemKind.Notice), "the device code");
        server.CompleteLogin();
        await WaitFor(() => server.Received.Any(message => Method(message) == "turn/start"), "the turn");
        var input = server.Received.First(message => Method(message) == "turn/start").GetProperty("params").GetProperty("input");
        Assert.Equal("look", input[0].GetProperty("text").GetString());
        Assert.Equal(("localImage", "/tmp/a.png"), (input[1].GetProperty("type").GetString(), input[1].GetProperty("path").GetString()));
    }

    [Fact]
    public async Task CompactingWaitsForTheRunningTurnAndIsReported()
    {
        await using var server = new FakeCodex();
        var sink = new RecordingSink();
        var state = new SessionState { SessionId = 1, Harness = Harnesses.Codex, WorkingDirectory = Path.GetTempPath(), PermissionMode = "default" };
        await using IHarnessSession session = new CodexSession(state, sink, () => Task.FromResult(new CodexLaunch(server.Client, null)), TextWriter.Null);
        await session.SendAsync("List the files", DispatchMode.Queue);
        await WaitFor(() => sink.Reports.OfType<ItemReport>().Any(item => item.Kind == SessionItemKind.Notice), "the device code");
        server.CompleteLogin();
        await WaitFor(() => sink.Reports.OfType<ApprovalRequestedReport>().Any(), "the approval");

        // Asked during the turn, it waits for that turn to end.
        await session.CompactAsync();
        await Task.Delay(200);
        Assert.DoesNotContain(server.Received, message => Method(message) == "thread/compact/start");
        await session.AnswerAsync(sink.Reports.OfType<ApprovalRequestedReport>().Single().Key, true, null);
        server.FinishTurn();
        await WaitFor(() => server.Received.Any(message => Method(message) == "thread/compact/start"), "the compaction");
        await WaitFor(() => sink.Reports.OfType<RunEndedReport>().Count() == 2, "the compaction's turn to end");
        Assert.Contains(sink.Reports.OfType<ItemReport>(), item => item is { Kind: SessionItemKind.Notice, Text: "The conversation was compacted." });
        Assert.IsType<StatusReport>(sink.Reports.Last());
        Assert.Equal(SessionStatus.Idle, ((StatusReport)sink.Reports.Last()).Status);
    }

    [Fact]
    public async Task ClearingWaitsForTheRunningTurnAndOpensANewThread()
    {
        await using var server = new FakeCodex();
        var sink = new RecordingSink();
        var state = new SessionState { SessionId = 1, Harness = Harnesses.Codex, WorkingDirectory = Path.GetTempPath(), PermissionMode = "default" };
        await using IHarnessSession session = new CodexSession(state, sink, () => Task.FromResult(new CodexLaunch(server.Client, null)), TextWriter.Null);
        await session.SendAsync("List the files", DispatchMode.Queue);
        await WaitFor(() => sink.Reports.OfType<ItemReport>().Any(item => item.Kind == SessionItemKind.Notice), "the device code");
        server.CompleteLogin();
        await WaitFor(() => sink.Reports.OfType<ApprovalRequestedReport>().Any(), "the approval");

        // Asked during the turn, it waits for that turn to end.
        await session.ClearAsync();
        await Task.Delay(200);
        Assert.Single(server.Received, message => Method(message) == "thread/start");
        await session.AnswerAsync(sink.Reports.OfType<ApprovalRequestedReport>().Single().Key, true, null);
        server.FinishTurn();
        await WaitFor(() => sink.Reports.OfType<ItemReport>().Any(item => item.Kind == SessionItemKind.Cleared), "the clear");
        Assert.Equal(2, server.Received.Count(message => Method(message) == "thread/start"));
        Assert.DoesNotContain(server.Received, message => Method(message) == "thread/resume");
        await WaitFor(() => sink.Reports.Last() is StatusReport { Status: SessionStatus.Idle }, "idle");
        Assert.Single(sink.Reports.OfType<RunEndedReport>()); // starting over is not a turn
    }

    [Fact]
    public async Task ASideQuestionRunsOnAnEphemeralReadOnlyFork()
    {
        await using var server = new FakeCodex();
        var state = new SessionState { SessionId = 1, Harness = Harnesses.Codex, WorkingDirectory = Path.GetTempPath(), PermissionMode = "bypassPermissions", HarnessSessionId = "thread-1", ReasoningEffort = "high" };
        await using IHarnessSession session = new CodexSession(state, new RecordingSink(), () => Task.FromResult(new CodexLaunch(server.Client, "sk-test")), TextWriter.Null);

        Assert.Equal("It lists files.", await session.AnswerSideQuestionAsync("What does it do?", CancellationToken.None));
        var fork = server.Received.Single(message => Method(message) == "thread/fork").GetProperty("params");
        Assert.Equal(("thread-1", true, "read-only", "never"), (fork.GetProperty("threadId").GetString(), fork.GetProperty("ephemeral").GetBoolean(),
            fork.GetProperty("sandbox").GetString(), fork.GetProperty("approvalPolicy").GetString()));
        var turn = server.Received.Single(message => Method(message) == "turn/start").GetProperty("params");
        Assert.Equal("fork-1", turn.GetProperty("threadId").GetString());
        Assert.Equal("high", turn.GetProperty("effort").GetString()); // the session's level
        Assert.Contains("What does it do?", turn.GetProperty("input")[0].GetProperty("text").GetString());
    }

    static string? Method(JsonElement message) => message.TryGetProperty("method", out var method) ? method.GetString() : null;
}
