using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using BoSoFa.Daemon;
using BoSoFa.Daemon.Harnesses.Claude;
using BoSoFa.Daemon.Sessions;
using BoSoFa.Protocol;
using Xunit.Abstractions;

namespace BoSoFa.Server.Tests;

/// <summary>Shares the replay transport's static signals with <see cref="ClusterTest"/>, so they never run in parallel.</summary>
[CollectionDefinition("Replay")]
public sealed class ReplayCollection;

[Collection("Replay")]
public class SessionTest(ITestOutputHelper output)
{
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    void Step(string name) => output.WriteLine($"{_clock.Elapsed.TotalSeconds,6:0.0} s  {name}");

    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    internal static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "claude", "write-file.jsonl");

    static async Task<T> Eventually<T>(Func<Task<T>> probe, Func<T, bool> done)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            var value = await probe();
            if (done(value) || DateTime.UtcNow > deadline) return value;
            await Task.Delay(50);
        }
    }

    /// <summary>Enrolls a machine for the signed-in client through the API, as <c>bosofad enroll</c> does.</summary>
    internal static async Task<DaemonState> EnrollAsync(Uri server, HttpClient owner, string dataDirectory)
    {
        var store = new DaemonStore(dataDirectory);
        using var key = store.LoadOrCreateKey();
        using var daemon = new HttpClient { BaseAddress = server, Timeout = TimeSpan.FromSeconds(30) };
        var started = await (await daemon.PostAsJsonAsync("/api/daemon/v1/enroll/start", new EnrollStartRequest("replay-box",
            "linux", "x64", Cli.Version, DaemonStore.PublicKey(key)))).Content.ReadFromJsonAsync<EnrollStartResponse>();
        (await owner.PostAsync($"/api/v1/enrollments/{started!.UserCode}/approve", null)).EnsureSuccessStatusCode();
        var poll = await (await daemon.PostAsJsonAsync("/api/daemon/v1/enroll/poll", new EnrollPollRequest(started.EnrollmentId)))
            .Content.ReadFromJsonAsync<EnrollPollResponse>();
        var state = new DaemonState(server, poll!.MachineId!.Value);
        store.SaveState(state);
        return state;
    }

    /// <summary>The plan's M1 scenario on the replay harness: start a session from the API, see deltas live, approve
    /// the tool, lose the server mid-turn, and find the whole transcript once it is back.</summary>
    [Fact]
    public async Task SessionRunsWithApprovalAndCatchesUpAfterTheServerWasDown()
    {
        ReplayTransport.SignalTimeoutOverride = TimeSpan.FromMinutes(1);
        ReplayTransport.Reset("after-tool");
        ReplayTransport.Reset("subscribed");
        await using var server = await TestServer.StartAsync();
        var cookies = new CookieContainer();
        using var boris = server.Client(cookies);
        (await boris.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("boris@example.com", null))).EnsureSuccessStatusCode();
        var dataDirectory = Path.Combine(server.DataDirectory, "daemon");
        var state = await EnrollAsync(server.BaseAddress, boris, dataDirectory);
        var work = Directory.CreateDirectory(Path.Combine(server.DataDirectory, "work")).FullName;

        using var key = new DaemonStore(dataDirectory).LoadOrCreateKey();
        using var outbox = new Outbox(Path.Combine(dataDirectory, "outbox"));
        await using var host = new SessionHost(dataDirectory, outbox, new(ReplayFixture: Fixture), TextWriter.Null);
        using var stop = new CancellationTokenSource();
        var link = new DaemonLink(state, key, TextWriter.Null, outbox, host);
        var running = link.RunAsync(stop.Token);

        Step("daemon started");
        var created = await boris.PostAsJsonAsync("/api/v1/sessions",
            new CreateSessionRequest(state.MachineId, Harnesses.Replay, work, "Create hello.txt", null, null));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var session = (await created.Content.ReadFromJsonAsync<SessionDto>())!;
        Assert.Equal((SessionStatus.Starting, "Create hello.txt", true), (session.Status, session.Title, session.CanControl));

        // Live: subscribe from the start and collect what arrives.
        var live = new ConcurrentQueue<LiveServerMessage>();
        using var socket = new ClientWebSocket();
        socket.Options.Cookies = cookies;
        await socket.ConnectAsync(new UriBuilder(server.BaseAddress) { Scheme = "ws", Path = LiveSocket.Path }.Uri, default);
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes<LiveClientMessage>(new LiveSubscribe(session.Id, 0),
            ProtocolJsonContext.Default.LiveClientMessage), WebSocketMessageType.Text, true, default);
        var listening = ListenAsync(socket, live);
        await Eventually(() => Task.FromResult(live.Count), count => count > 0); // subscribed: deltas are not replayed
        ReplayTransport.Release("subscribed");

        var requests = await Eventually(() => boris.GetFromJsonAsync<RuntimeRequestDto[]>($"/api/v1/sessions/{session.Id}/requests")!,
            r => r!.Length > 0);
        Step("approval pending");
        var approval = Assert.Single(requests!);
        Assert.Equal(("Write", RuntimeRequestStatus.Pending), (approval.ToolName, approval.Status));
        Assert.Contains("hello from bosofa", approval.Input);
        var waiting = await boris.GetFromJsonAsync<SessionDto>($"/api/v1/sessions/{session.Id}");
        Assert.Equal(SessionStatus.WaitingForApproval, waiting!.Status);

        // The live connection saw the streamed text, the persisted items and the pending request.
        await Eventually(() => Task.FromResult(live.ToArray()),
            messages => messages.OfType<LiveSessionUpdate>().Any(update => update.Requests.Length == 1));
        Assert.Contains(live, message => message is LiveDelta { Ref: "msg_01Replay:0", Text: "I'll create " });
        var liveItems = live.OfType<LiveSessionUpdate>().SelectMany(update => update.Items).ToList();
        Assert.Equal(liveItems.Select(item => item.Seq), Enumerable.Range(1, liveItems.Count).Select(seq => (long)seq));
        Assert.Contains(liveItems, item => item is { Kind: SessionItemKind.Assistant, Ref: "msg_01Replay:0" });

        (await boris.PostAsJsonAsync($"/api/v1/sessions/{session.Id}/requests/{approval.Id}/answer",
            new AnswerRuntimeRequest(true, null))).EnsureSuccessStatusCode();
        await Eventually(() => boris.GetFromJsonAsync<SessionItemDto[]>($"/api/v1/sessions/{session.Id}/items")!,
            items => items!.Any(item => item.Kind == SessionItemKind.ToolResult));

        Step("tool result stored");
        // The server goes away mid-turn; the daemon finishes the turn into its outbox.
        await server.StopAsync();
        ReplayTransport.Release("after-tool");
        await Eventually(() => Task.FromResult(outbox.After(0, 1000, int.MaxValue)),
            entries => entries.Any(entry => entry.Record.Report is RunEndedReport));
        Step("turn finished offline");
        Assert.True(outbox.Count > 0);
        await server.StartAppAsync();

        var items = await Eventually(() => boris.GetFromJsonAsync<SessionItemDto[]>($"/api/v1/sessions/{session.Id}/items")!,
            items => items!.Any(item => item.Kind == SessionItemKind.RunEnded));
        Step("caught up");
        Assert.Equal(
            [SessionItemKind.User, SessionItemKind.Assistant, SessionItemKind.ToolUse, SessionItemKind.ToolResult,
                SessionItemKind.Assistant, SessionItemKind.RunEnded],
            items!.Select(item => item.Kind));
        Assert.Equal(Enumerable.Range(1, items!.Length).Select(seq => (long)seq), items.Select(item => item.Seq));
        Assert.Equal(("hello.txt", "Write"), (items[2].Text, items[2].ToolName));
        Assert.Equal("Done: hello.txt now says hello from bosofa.", items[4].Text);
        var after = await Eventually(() => boris.GetFromJsonAsync<SessionDto>($"/api/v1/sessions/{session.Id}")!,
            s => s!.Status == SessionStatus.Idle);
        Assert.Equal(SessionStatus.Idle, after!.Status);
        Assert.Equal(0.00090624, after.CostUsd, 8);
        Assert.Equal(0, (await Eventually(() => Task.FromResult(outbox.Count), count => count == 0)));

        Step("outbox acknowledged");
        // A second turn replays again; this time the tool is denied.
        (await boris.PostAsJsonAsync($"/api/v1/sessions/{session.Id}/messages", new SendMessageRequest("Again")))
            .EnsureSuccessStatusCode();
        var second = await Eventually(() => boris.GetFromJsonAsync<RuntimeRequestDto[]>($"/api/v1/sessions/{session.Id}/requests")!,
            r => r!.Length > 0);
        (await boris.PostAsJsonAsync($"/api/v1/sessions/{session.Id}/requests/{second![0].Id}/answer",
            new AnswerRuntimeRequest(false, "Not now"))).EnsureSuccessStatusCode();
        var final = await Eventually(() => boris.GetFromJsonAsync<SessionItemDto[]>($"/api/v1/sessions/{session.Id}/items")!,
            all => all!.Count(item => item.Kind == SessionItemKind.RunEnded) == 2);
        Assert.Equal(2, final!.Count(item => item.Kind == SessionItemKind.RunEnded));
        Assert.Equal("Again", final![6].Text);

        Step("second turn done");
        // Someone else sees the session but cannot control it.
        using var alice = server.Client();
        (await alice.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("alice@example.com", null))).EnsureSuccessStatusCode();
        Assert.False((await alice.GetFromJsonAsync<SessionDto>($"/api/v1/sessions/{session.Id}"))!.CanControl);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await alice.PostAsJsonAsync($"/api/v1/sessions/{session.Id}/messages", new SendMessageRequest("hi"))).StatusCode);

        await stop.CancelAsync();
        await running.WaitAsync(Timeout);
        socket.Abort();
        try { await listening; }
        catch (Exception) { /* aborted */ }
    }

    internal static async Task ListenAsync(WebSocket socket, ConcurrentQueue<LiveServerMessage> messages)
    {
        var buffer = new byte[1024 * 1024];
        while (true)
        {
            var length = 0;
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(length), default);
                if (result.MessageType == WebSocketMessageType.Close) return;
                length += result.Count;
                if (result.EndOfMessage) break;
            }
            messages.Enqueue(JsonSerializer.Deserialize(buffer.AsSpan(0, length), ProtocolJsonContext.Default.LiveServerMessage)!);
        }
    }

    [Fact]
    public void OutboxKeepsUnacknowledgedRecordsAcrossRestartsAndNeverReusesASeq()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bosofa-outbox-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var outbox = new Outbox(directory))
            {
                Assert.Equal(1, outbox.Append(7, new StatusReport(SessionStatus.Running)));
                Assert.Equal(2, outbox.Append(7, new ItemReport(SessionItemKind.Assistant, DateTime.UtcNow, "hi")));
                Assert.Equal(1, outbox.Append(8, new StatusReport(SessionStatus.Idle)));
                outbox.Ack([new(7, 1)]);
            }
            File.AppendAllText(Directory.GetFiles(directory, "*.jsonl").Single(), "{\"sessionId\":7,\"se"); // torn write
            using (var outbox = new Outbox(directory))
            {
                Assert.Equal([(7ul, 2L), (8ul, 1L)],
                    outbox.After(0, 100, int.MaxValue).Select(entry => (entry.Record.SessionId, entry.Record.Seq)));
                Assert.Equal(3, outbox.Append(7, new StatusReport(SessionStatus.Idle)));
                Assert.Single(outbox.After(0, 1, int.MaxValue)); // batch limits
                outbox.Ack([new(7, 3), new(8, 1)]);
                Assert.Equal(0, outbox.Count);
            }
            using (var outbox = new Outbox(directory))
            {
                Assert.Equal(0, outbox.Count);
                Assert.Equal(4, outbox.Append(7, new StatusReport(SessionStatus.Running)));
                Assert.Single(Directory.GetFiles(directory, "*.jsonl")); // fully acknowledged segments are gone
            }
        }
        finally { Directory.Delete(directory, true); }
    }
}
