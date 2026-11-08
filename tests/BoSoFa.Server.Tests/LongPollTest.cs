using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using BoSoFa.Daemon;
using BoSoFa.Daemon.Harnesses.Claude;
using BoSoFa.Daemon.Sessions;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Tests;

[Collection("Replay")]
public class LongPollTest
{
    /// <summary>A whole session over long-polling: commands down, reports up, and a call answered at once.</summary>
    [Fact]
    public async Task ASessionRunsWithoutAWebSocket()
    {
        ReplayTransport.SignalTimeoutOverride = TimeSpan.FromMinutes(1);
        ReplayTransport.Reset("after-tool");
        ReplayTransport.Reset("subscribed");
        ReplayTransport.Release("subscribed");
        await using var server = await TestServer.StartAsync();
        using var boris = server.Client(new CookieContainer());
        (await boris.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("boris@example.com", null))).EnsureSuccessStatusCode();
        var dataDirectory = Path.Combine(server.DataDirectory, "daemon");
        var state = await SessionTest.EnrollAsync(server.BaseAddress, boris, dataDirectory);
        var work = Directory.CreateDirectory(Path.Combine(server.DataDirectory, "work")).FullName;
        using var key = new DaemonStore(dataDirectory).LoadOrCreateKey();
        using var outbox = new Outbox(Path.Combine(dataDirectory, "outbox"));
        await using var host = new SessionHost(dataDirectory, outbox, new(ReplayFixture: SessionTest.Fixture), TextWriter.Null);
        using var stop = new CancellationTokenSource();
        var log = new StringWriter();
        var running = new DaemonLink(state, key, log, outbox, host, DaemonTransport.LongPoll).RunAsync(stop.Token);
        await Eventually(async () => (await boris.GetFromJsonAsync<MachineDto[]>("/api/v1/machines"))!.Single().Online);
        Assert.Contains("long-polling", log.ToString());

        var session = (await (await boris.PostAsJsonAsync("/api/v1/sessions",
            new CreateSessionRequest(state.MachineId, Harnesses.Replay, work, "Create hello.txt", null, null))).Content.ReadFromJsonAsync<SessionDto>())!;
        RuntimeRequestDto[] requests = [];
        await Eventually(async () => (requests = (await boris.GetFromJsonAsync<RuntimeRequestDto[]>($"/api/v1/sessions/{session.Id}/requests"))!).Length > 0);
        (await boris.PostAsJsonAsync($"/api/v1/sessions/{session.Id}/requests/{requests[0].Id}/answer", new AnswerRuntimeRequest(true, null))).EnsureSuccessStatusCode();
        ReplayTransport.Release("after-tool");
        await Eventually(async () => (await boris.GetFromJsonAsync<SessionDto>($"/api/v1/sessions/{session.Id}"))!.Status == SessionStatus.Idle);

        var roots = await boris.GetFromJsonAsync<FileRootsResult>($"/api/v1/machines/{state.MachineId}/files/roots");
        Assert.Equal(work, Assert.Single(roots!.Roots));

        await stop.CancelAsync();
        try { await running; }
        catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task ADaemonFallsBackToLongPollingWhenWebSocketsAreRefused()
    {
        await using var server = await TestServer.StartAsync(extraArgs: ["--BoSoFa:RefuseDaemonWebSockets=true"]);
        using var boris = server.Client(new CookieContainer());
        (await boris.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("boris@example.com", null))).EnsureSuccessStatusCode();
        var dataDirectory = Path.Combine(server.DataDirectory, "daemon");
        var state = await SessionTest.EnrollAsync(server.BaseAddress, boris, dataDirectory);
        using var key = new DaemonStore(dataDirectory).LoadOrCreateKey();
        using var stop = new CancellationTokenSource();
        var running = new DaemonLink(state, key, TextWriter.Null).RunAsync(stop.Token);
        await Eventually(async () => (await boris.GetFromJsonAsync<MachineDto[]>("/api/v1/machines"))!.Single().Online);
        await stop.CancelAsync();
        try { await running; }
        catch (OperationCanceledException) { }
    }

    /// <summary>Without sticky load balancing a poll can land on any node.</summary>
    [Fact]
    public async Task APollReachesTheNodeHoldingTheChannel()
    {
        var root = Path.Combine(Path.GetTempPath(), "bosofa-poll-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var first = await TestServer.StartClusterNodeAsync(root, "node-1");
            using var boris = first.Client(new CookieContainer());
            (await boris.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("boris@example.com", null))).EnsureSuccessStatusCode();
            await using var second = await TestServer.StartClusterNodeAsync(root, "node-2");
            var dataDirectory = Path.Combine(root, "daemon");
            var state = await SessionTest.EnrollAsync(first.BaseAddress, boris, dataDirectory);
            using var key = new DaemonStore(dataDirectory).LoadOrCreateKey();
            using var tokens = new ServerClient(first.BaseAddress);
            HttpClient Daemon(TestServer node, string token)
            {
                var client = node.Client();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return client;
            }
            var token = (await tokens.GetTokenAsync(state.MachineId, key, default)).AccessToken;
            using var onFirst = Daemon(first, token);
            using var onSecond = Daemon(second, token);

            var opened = await (await onFirst.PostAsJsonAsync(DaemonPoll.Path, new HelloMessage(Cli.Version, ProtocolVersion.Current, "linux", "x64", "box")))
                .Content.ReadFromJsonAsync<PollOpened>();
            await Eventually(async () => (await boris.GetFromJsonAsync<MachineDto[]>("/api/v1/machines"))!.Single().Online);
            PollBatch? batch = null;
            await Eventually(async () =>
            {
                var response = await onSecond.GetAsync($"{DaemonPoll.Path}/{opened!.ConnectionId}/receive?after=0");
                if (response.StatusCode != HttpStatusCode.OK) return false;
                batch = await response.Content.ReadFromJsonAsync<PollBatch>();
                return batch!.Messages.Length > 0;
            });
            Assert.IsType<WelcomeMessage>(batch!.Messages[0]);

            var ping = new StringContent("""[{"t":"ping","sequence":7}]""", Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.NoContent, (await onSecond.PostAsync($"{DaemonPoll.Path}/{opened!.ConnectionId}/send", ping)).StatusCode);
            await Eventually(async () =>
            {
                batch = await (await onFirst.GetAsync($"{DaemonPoll.Path}/{opened.ConnectionId}/receive?after={batch!.LastSequence}")).Content.ReadFromJsonAsync<PollBatch>();
                return batch!.Messages.OfType<PongMessage>().Any(pong => pong.Sequence == 7);
            });
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch (IOException) { }
        }
    }

    static async Task Eventually(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(50);
        }
    }
}
