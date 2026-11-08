using System.IO.Pipes;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using BoSoFa.Daemon;
using BoSoFa.Daemon.Harnesses;
using BoSoFa.Daemon.Harnesses.Codex;
using BoSoFa.Daemon.Sessions;
using BoSoFa.Protocol;
using Xunit;

namespace BoSoFa.Server.Tests;

[Collection("Replay")]
public class CodexUsageTest
{
    // Shape from Codex 0.167 on a real account.
    const string Limits = """
        {"ordinaryUsageAllowed":true,"rateLimits":{"limitId":"codex","primary":{"usedPercent":6,"windowDurationMins":10080,"resetsAt":1794516477},"secondary":null,"credits":{"hasCredits":true,"unlimited":false,"balance":"126.4442102500"},"planType":"prolite"}}
        """;

    /// <summary>Answers <c>codex app-server</c> requests from a table, over pipes.</summary>
    sealed class FakeCodex : IAsyncDisposable
    {
        readonly AnonymousPipeServerStream _toServer = new(PipeDirection.Out), _fromServer = new(PipeDirection.In);
        readonly StreamReader _reader;
        readonly StreamWriter _writer;
        readonly Task _running;
        readonly Dictionary<string, string?> _results;

        public FakeCodex(Dictionary<string, string?> results)
        {
            _results = results;
            _reader = new StreamReader(new AnonymousPipeClientStream(PipeDirection.In, _toServer.ClientSafePipeHandle));
            _writer = new StreamWriter(new AnonymousPipeClientStream(PipeDirection.Out, _fromServer.ClientSafePipeHandle)) { AutoFlush = true };
            Client = JsonRpcProcess.Connect(_toServer, _fromServer, includeVersion: false);
            _running = Task.Run(ServeAsync);
        }

        public JsonRpcProcess Client { get; }

        async Task ServeAsync()
        {
            while (await _reader.ReadLineAsync() is { } line)
            {
                var message = JsonDocument.Parse(line).RootElement;
                if (!message.TryGetProperty("id", out var id)) continue;
                var method = message.GetProperty("method").GetString()!;
                var response = new JsonObject { ["id"] = JsonNode.Parse(id.GetRawText()) };
                if (_results.TryGetValue(method, out var result) && result != null) response["result"] = JsonNode.Parse(result);
                else if (result == null && _results.ContainsKey(method)) response["error"] = new JsonObject { ["code"] = -32000, ["message"] = "no" };
                else response["result"] = new JsonObject();
                await _writer.WriteLineAsync(response.ToJsonString());
            }
            _writer.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _running.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task PlanAndLimitsAreReadWithoutATurn()
    {
        await using var codex = new FakeCodex(new()
        {
            ["account/read"] = """{"account":{"type":"chatgpt","planType":"prolite"}}""",
            ["account/rateLimits/read"] = Limits
        });
        var usage = await CodexUsage.ReadAsync(codex.Client);
        Assert.True(usage.SignedIn);
        Assert.Equal("prolite", usage.Plan);
        Assert.Equal("126.4442102500", usage.Credits);
        var week = Assert.Single(usage.Windows);
        Assert.Equal(10080, week.Minutes);
        Assert.Equal(0.06, week.Usage.Utilization, 5);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794516477).UtcDateTime, week.Usage.ResetsAt);
    }

    [Fact]
    public async Task ASignedOutCodexSaysSoAndAFailedLimitsReadKeepsThePlan()
    {
        await using (var codex = new FakeCodex(new() { ["account/read"] = """{"account":null,"requiresOpenaiAuth":true}""" }))
            Assert.Equal(CodexUsage.NotSignedIn, await CodexUsage.ReadAsync(codex.Client));
        await using (var codex = new FakeCodex(new()
                     {
                         ["account/read"] = """{"account":{"type":"apiKey"}}""", ["account/rateLimits/read"] = null
                     }))
        {
            var usage = await CodexUsage.ReadAsync(codex.Client);
            Assert.True(usage.SignedIn);
            Assert.Empty(usage.Windows);
        }
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"rateLimits":"none"}""")]
    [InlineData("""{"rateLimits":{"primary":"full","secondary":3}}""")]
    [InlineData("""{"rateLimits":{"primary":{"usedPercent":"six","windowDurationMins":300}}}""")]
    [InlineData("""{"rateLimits":{"primary":{"usedPercent":6}}}""")]
    public void AnotherShapeGivesNoWindows(string json) =>
        Assert.Empty(CodexUsage.Of("plus", JsonDocument.Parse(json).RootElement).Windows);

    [Fact]
    public async Task OnlySomeoneWhoControlsTheMachineCanAskAndNoSignInIsNotAnError()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bosofa-codex-usage-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var server = await TestServer.StartAsync(Path.Combine(directory, "server"));
            using var boris = server.Client(new CookieContainer());
            (await boris.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("boris@example.com", null))).EnsureSuccessStatusCode();
            using var anna = server.Client(new CookieContainer());
            (await anna.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("anna@example.com", null))).EnsureSuccessStatusCode();
            var daemonDirectory = Path.Combine(directory, "daemon");
            var state = await SessionTest.EnrollAsync(server.BaseAddress, boris, daemonDirectory);
            using var key = new DaemonStore(daemonDirectory).LoadOrCreateKey();
            using var outbox = new Outbox(Path.Combine(daemonDirectory, "outbox"));
            await using var host = new SessionHost(daemonDirectory, outbox, new(), TextWriter.Null);
            using var stop = new CancellationTokenSource();
            var running = new DaemonLink(state, key, TextWriter.Null, outbox, host).RunAsync(stop.Token);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!(await boris.GetFromJsonAsync<MachineDto[]>("/api/v1/machines"))!.Single().Online)
            {
                Assert.True(DateTime.UtcNow < deadline, "The daemon did not connect.");
                await Task.Delay(50);
            }

            var path = $"/api/v1/machines/{state.MachineId}/codex-usage";
            var usage = await boris.GetFromJsonAsync<CodexUsageResult>(path);
            Assert.False(usage!.SignedIn);
            Assert.Empty(usage.Windows);
            Assert.Equal(HttpStatusCode.Forbidden, (await anna.GetAsync(path)).StatusCode);

            await stop.CancelAsync();
            await running;
        }
        finally
        {
            try { Directory.Delete(directory, true); }
            catch (IOException) { }
        }
    }
}
