using System.Net;
using System.Net.Http.Json;
using BoSoFa.Daemon;
using BoSoFa.Daemon.Harnesses.Claude;
using BoSoFa.Daemon.Sessions;
using BoSoFa.DevCluster;
using BoSoFa.Protocol;
using Xunit.Abstractions;

namespace BoSoFa.Server.Tests;

/// <summary>Plan M2 "done when": the M1 scenario passes while the leader node is killed. Three server processes on
/// Azurite behind a TCP front door; the daemon runs in this process on the replay harness.</summary>
[Collection("Replay")]
public class FailoverTest(Tests.Azurite azurite, ITestOutputHelper output) : IClassFixture<Tests.Azurite>
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    void Step(string name) => output.WriteLine($"{_clock.Elapsed.TotalSeconds,6:0.0} s  {name}");

    /// <summary>Retries while nodes come and go: a probe failing on a dead node's connection is just "not yet".</summary>
    static async Task<T?> Eventually<T>(Func<Task<T>> probe, Func<T, bool> done)
    {
        var deadline = DateTime.UtcNow + Timeout;
        T? value = default;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                value = await probe();
                if (done(value)) return value;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException) { }
            await Task.Delay(200);
        }
        return value;
    }

    /// <summary>A fresh connection per call, so no pooled connection to a killed node is reused.</summary>
    static HttpClient Client(Uri url, CookieContainer cookies) =>
        new(new SocketsHttpHandler { CookieContainer = cookies, PooledConnectionLifetime = TimeSpan.Zero, AllowAutoRedirect = false })
        {
            BaseAddress = url, Timeout = TimeSpan.FromSeconds(30)
        };

    /// <summary>Docs/server-agent.md, step 2 "done when": a session with no machine answers through an HTTP MCP server and survives
    /// its node being killed. A client on another node follows the live text, the node dies mid-answer, and another one asks the
    /// model again and finishes.</summary>
    [Fact]
    public async Task AnAgentSessionSurvivesItsNodeBeingKilled()
    {
        var root = Path.Combine(Path.GetTempPath(), "bosofa-agent-failover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var container = "agent-" + Guid.NewGuid().ToString("N")[..12];
        await using var backend = await FakeAgentBackend.StartAsync();
        string[] extra = ["--BoSoFa:Agent:OpenAIEndpoint", backend.ResponsesUrl];
        var nodes = new List<NodeProcess>();
        try
        {
            nodes.Add(new NodeProcess(root, "node-1", azurite.ConnectionString, container, extraArguments: extra));
            var cookies = new CookieContainer();
            using var first = Client(nodes[0].MainUrl, cookies);
            Assert.NotNull(await Eventually(
                async () => (await first.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("boris@example.com", "Boris")))
                    .EnsureSuccessStatusCode(), _ => true));
            nodes.Add(new NodeProcess(root, "node-2", azurite.ConnectionString, container, extraArguments: extra));
            nodes.Add(new NodeProcess(root, "node-3", azurite.ConnectionString, container, extraArguments: extra));
            using var second = Client(nodes[1].MainUrl, cookies);
            foreach (var node in nodes.Skip(1))
            {
                using var direct = Client(node.MainUrl, cookies);
                Assert.Equal("Boris", (await Eventually(() => direct.GetFromJsonAsync<UserDto>("/api/v1/me")!,
                    user => user?.Name == "Boris"))?.Name);
            }
            Step("three nodes up");

            (await first.PostAsJsonAsync("/api/v1/mcp-servers", new PublishMcpServer("facts", McpTransport.Http, null, null, backend.McpUrl, null))).EnsureSuccessStatusCode();
            (await first.PostAsJsonAsync("/api/v1/profiles", new ProfileDto("research", null, null, ["facts"], []))).EnsureSuccessStatusCode();
            var account = await (await first.PostAsJsonAsync("/api/v1/accounts",
                new CreateHarnessAccountRequest(AccountKind.OpenAIApiKey, "Work OpenAI", "sk-testtesttesttesttesttesttest"))).Content.ReadFromJsonAsync<HarnessAccountDto>();

            // Started through node-1, which therefore runs it. The model hangs mid-answer.
            backend.Hang("Half an answer");
            backend.Call("call_1", "mcp__facts__lookup", "{\"q\":\"life\"}");
            backend.Answer("It is 42.");
            var session = (await (await first.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest(0, Harnesses.Agent, "", "What is the answer?",
                "bypassPermissions", "test-model", account!.Id, ProfileName: "research"))).Content.ReadFromJsonAsync<SessionDto>())!;
            await backend.WaitForModelCallAsync();
            Step("the model is answering on node-1");

            // A client on node-2 sees node-1's stream through the relay.
            using var socket = new System.Net.WebSockets.ClientWebSocket();
            socket.Options.Cookies = cookies;
            await socket.ConnectAsync(new Uri($"ws://{nodes[1].MainUrl.Authority}{LiveSocket.Path}"), CancellationToken.None);
            var live = new System.Collections.Concurrent.ConcurrentQueue<LiveServerMessage>();
            var listening = SessionTest.ListenAsync(socket, live);
            await socket.SendAsync(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes<LiveClientMessage>(new LiveSubscribe(session.Id, 0), ProtocolJsonContext.Default.LiveClientMessage),
                System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
            await Eventually(() => Task.FromResult(live.OfType<LiveDelta>().Count()), count => count > 0);
            Assert.Contains(live, message => message is LiveDelta { Text: "." });
            Step("a client on node-2 follows the text from node-1");

            nodes[0].Kill();
            Step("node-1 killed");

            var items = await Eventually(() => second.GetFromJsonAsync<SessionItemDto[]>($"/api/v1/sessions/{session.Id}/items")!,
                all => all!.Any(item => item.Kind == SessionItemKind.RunEnded));
            Step("the session finished on another node");
            Assert.Equal(
                [SessionItemKind.User, SessionItemKind.Instructions, SessionItemKind.ToolUse, SessionItemKind.ToolResult, SessionItemKind.Assistant, SessionItemKind.RunEnded],
                items!.Select(item => item.Kind));
            Assert.Equal("The answer is 42.", items![3].Text);
            Assert.Equal("It is 42.", items[4].Text);
            Assert.Equal(3, backend.ModelRequests.Count); // once on node-1, twice after it died
            var after = await Eventually(() => second.GetFromJsonAsync<SessionDto>($"/api/v1/sessions/{session.Id}")!, s => s!.Status == SessionStatus.Idle);
            Assert.Equal(SessionStatus.Idle, after!.Status);
            socket.Abort();
            await listening.WaitAsync(Timeout).ContinueWith(_ => { });
        }
        catch
        {
            foreach (var node in nodes) output.WriteLine($"--- {node.NodeId} (restarts {node.Restarts})\n{node.Log}");
            throw;
        }
        finally
        {
            foreach (var node in nodes) await node.DisposeAsync();
            try { Directory.Delete(root, true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task SessionCatchesUpWhenTheLeaderIsKilledMidTurn()
    {
        var root = Path.Combine(Path.GetTempPath(), "bosofa-failover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var container = "failover-" + Guid.NewGuid().ToString("N")[..12];
        ReplayTransport.SignalTimeoutOverride = Timeout;
        ReplayTransport.Reset("after-tool");
        ReplayTransport.Release("subscribed"); // this test does not watch deltas
        var nodes = new List<NodeProcess>();
        try
        {
            // The first node creates the cluster; the others join once its genesis is published.
            nodes.Add(new NodeProcess(root, "node-1", azurite.ConnectionString, container));
            var cookies = new CookieContainer();
            using (var first = Client(nodes[0].MainUrl, cookies))
                Assert.NotNull(await Eventually(
                    async () => (await first.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("boris@example.com", "Boris")))
                        .EnsureSuccessStatusCode(), _ => true));
            nodes.Add(new NodeProcess(root, "node-2", azurite.ConnectionString, container));
            nodes.Add(new NodeProcess(root, "node-3", azurite.ConnectionString, container));
            await using var front = new TcpBalancer(nodes.Select(node => node.MainUrl).ToList());
            using var boris = Client(front.Url, cookies);
            foreach (var node in nodes.Skip(1))
            {
                using var direct = Client(node.MainUrl, cookies);
                Assert.Equal("Boris", (await Eventually(() => direct.GetFromJsonAsync<UserDto>("/api/v1/me")!,
                    user => user?.Name == "Boris"))?.Name);
            }
            Step("three nodes up");

            // The daemon goes through the front door, so it lands on node-1, the leader.
            var daemonDirectory = Path.Combine(root, "daemon");
            var state = await SessionTest.EnrollAsync(front.Url, boris, daemonDirectory);
            var work = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
            using var key = new DaemonStore(daemonDirectory).LoadOrCreateKey();
            using var outbox = new Outbox(Path.Combine(daemonDirectory, "outbox"));
            await using var host = new SessionHost(daemonDirectory, outbox, new(ReplayFixture: SessionTest.Fixture), TextWriter.Null);
            using var stop = new CancellationTokenSource();
            var running = new DaemonLink(state, key, TextWriter.Null, outbox, host).RunAsync(stop.Token);
            await Eventually(() => boris.GetFromJsonAsync<MachineDto[]>("/api/v1/machines")!, m => m!.SingleOrDefault()?.Online == true);

            var session = (await (await boris.PostAsJsonAsync("/api/v1/sessions",
                new CreateSessionRequest(state.MachineId, Harnesses.Replay, work, "Create hello.txt", null, null)))
                .Content.ReadFromJsonAsync<SessionDto>())!;
            var request = (await Eventually(() => boris.GetFromJsonAsync<RuntimeRequestDto[]>($"/api/v1/sessions/{session.Id}/requests")!,
                r => r!.Length > 0))!.Single();
            (await boris.PostAsJsonAsync($"/api/v1/sessions/{session.Id}/requests/{request.Id}/answer",
                new AnswerRuntimeRequest(true, null))).EnsureSuccessStatusCode();
            await Eventually(() => boris.GetFromJsonAsync<SessionItemDto[]>($"/api/v1/sessions/{session.Id}/items")!,
                items => items!.Any(item => item.Kind == SessionItemKind.ToolResult));
            Step("tool approved and its result stored");

            // Kill the leader (which also holds the daemon's connection and owns the EventLog topics) mid-turn; the
            // daemon finishes the turn into its outbox and delivers it to whichever node takes over.
            nodes[0].Kill();
            ReplayTransport.Release("after-tool");
            Step("leader killed");

            var items = await Eventually(() => boris.GetFromJsonAsync<SessionItemDto[]>($"/api/v1/sessions/{session.Id}/items")!,
                all => all!.Any(item => item.Kind == SessionItemKind.RunEnded));
            Step("session caught up");
            Assert.Equal(
                [SessionItemKind.User, SessionItemKind.Assistant, SessionItemKind.ToolUse, SessionItemKind.ToolResult,
                    SessionItemKind.Assistant, SessionItemKind.RunEnded],
                items!.Select(item => item.Kind));
            Assert.Equal(Enumerable.Range(1, items!.Length).Select(seq => (long)seq), items.Select(item => item.Seq));
            var after = await Eventually(() => boris.GetFromJsonAsync<SessionDto>($"/api/v1/sessions/{session.Id}")!,
                s => s!.Status == SessionStatus.Idle);
            Assert.Equal(SessionStatus.Idle, after!.Status);
            Assert.Equal(0, await Eventually(() => Task.FromResult(outbox.Count), count => count == 0));
            Assert.True((await boris.GetFromJsonAsync<MachineDto[]>("/api/v1/machines"))!.Single().Online);

            // A new turn works on the surviving nodes.
            (await boris.PostAsJsonAsync($"/api/v1/sessions/{session.Id}/messages", new SendMessageRequest("Again")))
                .EnsureSuccessStatusCode();
            var second = await Eventually(() => boris.GetFromJsonAsync<RuntimeRequestDto[]>($"/api/v1/sessions/{session.Id}/requests")!,
                r => r!.Length > 0);
            (await boris.PostAsJsonAsync($"/api/v1/sessions/{session.Id}/requests/{second!.Single().Id}/answer",
                new AnswerRuntimeRequest(true, null))).EnsureSuccessStatusCode();
            Assert.Equal(2, (await Eventually(() => boris.GetFromJsonAsync<SessionItemDto[]>($"/api/v1/sessions/{session.Id}/items")!,
                all => all!.Count(item => item.Kind == SessionItemKind.RunEnded) == 2))!.Count(item => item.Kind == SessionItemKind.RunEnded));
            Step("second turn done on the survivors; restarts: " +
                 string.Join(", ", nodes.Select(node => $"{node.NodeId}={node.Restarts}")));

            await stop.CancelAsync();
            await running.WaitAsync(Timeout);
        }
        catch
        {
            foreach (var node in nodes) output.WriteLine($"--- {node.NodeId} (restarts {node.Restarts})\n{node.Log}");
            throw;
        }
        finally
        {
            ReplayTransport.SignalTimeoutOverride = null;
            foreach (var node in nodes) await node.DisposeAsync();
            try { Directory.Delete(root, true); }
            catch (IOException) { }
        }
    }
}
