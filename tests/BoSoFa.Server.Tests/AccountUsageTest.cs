using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BoSoFa.Daemon;
using BoSoFa.Daemon.Harnesses.Claude;
using BoSoFa.Daemon.Sessions;
using BoSoFa.Protocol;
using BoSoFa.Server.Machines;
using Xunit;

namespace BoSoFa.Server.Tests;

[Collection("Replay")]
public class AccountUsageTest
{
    const string RateLimitEvent = """
        {"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1794180000,"rateLimitType":"five_hour","unifiedWindows":{"five_hour":{"utilization":0.05,"resetsAt":1794180000,"status":"allowed"},"seven_day":{"utilization":0.19,"resetsAt":1794294000,"status":"allowed"}}}}
        """;

    static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void LimitsAreReadFromTheEventClaudeCodeSends()
    {
        var now = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
        var report = ClaudeUsage.LimitsOf(Parse(RateLimitEvent), now)!;
        Assert.Equal(now, report.At);
        Assert.Equal(new UsageWindow(0.05, DateTimeOffset.FromUnixTimeSeconds(1794180000).UtcDateTime, "allowed"), report.FiveHour);
        Assert.Equal(0.19, report.SevenDay!.Utilization);
    }

    [Theory]
    [InlineData("""{"type":"rate_limit_event"}""")]
    [InlineData("""{"rate_limit_info":"nope"}""")]
    [InlineData("""{"rate_limit_info":{"status":"allowed"}}""")]
    [InlineData("""{"rate_limit_info":{"unifiedWindows":[]}}""")]
    [InlineData("""{"rate_limit_info":{"unifiedWindows":{"five_hour":"full"}}}""")]
    [InlineData("""{"rate_limit_info":{"unifiedWindows":{"five_hour":{"utilization":"high"}}}}""")]
    public void AnEventOfAnotherShapeIsIgnored(string json) => Assert.Null(ClaudeUsage.LimitsOf(Parse(json), DateTime.UtcNow));

    [Fact]
    public void AnUnusableResetTimeIsLeftOut()
    {
        var report = ClaudeUsage.LimitsOf(Parse("""{"rate_limit_info":{"unifiedWindows":{"five_hour":{"utilization":0.5,"resetsAt":"soon"},"seven_day":{"utilization":0.1,"resetsAt":99999999999999}}}}"""), DateTime.UtcNow)!;
        Assert.Null(report.FiveHour!.ResetsAt);
        Assert.Null(report.SevenDay!.ResetsAt);
    }

    [Fact]
    public async Task AnAccountShowsTheLimitsOfItsLatestTurn()
    {
        ReplayTransport.SignalTimeoutOverride = TimeSpan.Zero;
        var directory = Path.Combine(Path.GetTempPath(), "bosofa-usage-" + Guid.NewGuid().ToString("N"));
        var work = Directory.CreateDirectory(Path.Combine(directory, "work")).FullName;
        var fixture = Path.Combine(directory, "with-limits.jsonl");
        var lines = (await File.ReadAllLinesAsync(SessionTest.Fixture)).ToList();
        lines.Insert(lines.FindIndex(line => line.Contains("\"type\":\"result\"")), RateLimitEvent);
        await File.WriteAllLinesAsync(fixture, lines);
        try
        {
            await using var server = await TestServer.StartAsync(Path.Combine(directory, "server"));
            using var boris = server.Client(new CookieContainer());
            (await boris.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("boris@example.com", null))).EnsureSuccessStatusCode();
            using var anna = server.Client(new CookieContainer());
            (await anna.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("anna@example.com", null))).EnsureSuccessStatusCode();
            var account = await (await boris.PostAsJsonAsync("/api/v1/accounts",
                new CreateHarnessAccountRequest(AccountKind.ClaudeOAuthToken, "Mine", "sk-ant-oat01-abcdefghijklmnopqrstuvwxyz"))).Content
                .ReadFromJsonAsync<HarnessAccountDto>();
            Assert.Null(account!.Usage);

            var daemonDirectory = Path.Combine(directory, "daemon");
            var state = await SessionTest.EnrollAsync(server.BaseAddress, boris, daemonDirectory);
            using var key = new DaemonStore(daemonDirectory).LoadOrCreateKey();
            using var outbox = new Outbox(Path.Combine(daemonDirectory, "outbox"));
            await using var host = new SessionHost(daemonDirectory, outbox,
                new(ReplayFixture: fixture, Accounts: new FixedLease()), TextWriter.Null);
            using var stop = new CancellationTokenSource();
            var running = new DaemonLink(state, key, TextWriter.Null, outbox, host).RunAsync(stop.Token);
            await Eventually(async () => (await boris.GetFromJsonAsync<MachineDto[]>("/api/v1/machines"))!.Single().Online);

            var created = await boris.PostAsJsonAsync("/api/v1/sessions",
                new CreateSessionRequest(state.MachineId, Harnesses.Replay, work, "Go", null, null, account.Id));
            created.EnsureSuccessStatusCode();
            var session = (await created.Content.ReadFromJsonAsync<SessionDto>())!.Id;
            await Eventually(async () =>
            {
                var pending = await boris.GetFromJsonAsync<RuntimeRequestDto[]>($"/api/v1/sessions/{session}/requests");
                if (pending!.Length > 0)
                    await boris.PostAsJsonAsync($"/api/v1/sessions/{session}/requests/{pending[0].Id}/answer", new AnswerRuntimeRequest(true, null));
                return (await UsageAsync(boris)) != null;
            });
            var usage = (await UsageAsync(boris))!;
            Assert.Equal(0.05, usage.FiveHour!.Utilization);
            Assert.Equal(0.19, usage.SevenDay!.Utilization);
            Assert.Empty((await anna.GetFromJsonAsync<HarnessAccountDto[]>("/api/v1/accounts"))!);
            await stop.CancelAsync();
            await running;

            // A late report from another session does not overwrite newer figures.
            var publisher = await server.Node.Ready;
            var last = (await MachineEndpoints.StoreReportsAsync(publisher, state.MachineId,
                new([new(session, 1, new StatusReport(SessionStatus.Idle))]))).Acks.Single().Seq;
            await MachineEndpoints.StoreReportsAsync(publisher, state.MachineId, new([new(session, last + 1,
                new AccountUsageReport(usage.UpdatedAt.AddHours(-1), new(0.99, null, null), null))]));
            Assert.Equal(0.05, (await UsageAsync(boris))!.FiveHour!.Utilization);
            await MachineEndpoints.StoreReportsAsync(publisher, state.MachineId, new([new(session, last + 2,
                new AccountUsageReport(usage.UpdatedAt.AddHours(1), new(0.5, null, null), null))]));
            Assert.Equal(0.5, (await UsageAsync(boris))!.FiveHour!.Utilization);

            (await boris.DeleteAsync($"/api/v1/accounts/{account.Id}")).EnsureSuccessStatusCode();
        }
        finally
        {
            ReplayTransport.SignalTimeoutOverride = null;
            try { Directory.Delete(directory, true); }
            catch (IOException) { }
        }
    }

    static async Task<AccountUsageDto?> UsageAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<HarnessAccountDto[]>("/api/v1/accounts"))!.Single().Usage;

    static async Task Eventually(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(50);
        }
    }

    sealed class FixedLease : IAccountLeaser
    {
        public Task<AccountLeaseResponse> LeaseAsync(ulong accountId, ulong sessionId, CancellationToken cancellation) =>
            Task.FromResult(new AccountLeaseResponse(AccountKind.ClaudeOAuthToken, "sk-ant-oat01-test"));
    }
}
