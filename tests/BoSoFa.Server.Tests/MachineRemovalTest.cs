using System.Net;
using System.Net.Http.Json;
using BoSoFa.Daemon;
using BoSoFa.Daemon.Harnesses.Claude;
using BoSoFa.Daemon.Sessions;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Tests;

/// <summary>Runs a replay session, so it shares the replay transport's signals with <see cref="SessionTest"/>.</summary>
[Collection("Replay")]
public class MachineRemovalTest
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

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

    static async Task<HttpClient> SignIn(TestServer server, string email)
    {
        var client = server.Client();
        (await client.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest(email, null))).EnsureSuccessStatusCode();
        return client;
    }

    [Fact]
    public async Task RemovingAMachineDisconnectsItsDaemonAndStopsItsSessions()
    {
        ReplayTransport.SignalTimeoutOverride = TimeSpan.FromMinutes(1);
        ReplayTransport.Reset("subscribed"); // never released: the turn stays running until the machine goes
        await using var server = await TestServer.StartAsync();
        using var admin = await SignIn(server, "admin@example.com"); // the first person is the admin
        using var boris = await SignIn(server, "boris@example.com");
        using var alice = await SignIn(server, "alice@example.com");
        var dataDirectory = Path.Combine(server.DataDirectory, "daemon");
        var state = await SessionTest.EnrollAsync(server.BaseAddress, boris, dataDirectory);
        var work = Directory.CreateDirectory(Path.Combine(server.DataDirectory, "work")).FullName;

        using var key = new DaemonStore(dataDirectory).LoadOrCreateKey();
        using var outbox = new Outbox(Path.Combine(dataDirectory, "outbox"));
        await using var host = new SessionHost(dataDirectory, outbox, new(ReplayFixture: SessionTest.Fixture), TextWriter.Null);
        using var stop = new CancellationTokenSource();
        var running = new DaemonLink(state, key, TextWriter.Null, outbox, host).RunAsync(stop.Token);
        try
        {
            await Eventually(() => boris.GetFromJsonAsync<MachineDto[]>("/api/v1/machines")!, machines => machines!.Single().Online);
            var session = (await (await boris.PostAsJsonAsync("/api/v1/sessions",
                new CreateSessionRequest(state.MachineId, Harnesses.Replay, work, "Create hello.txt", null, null))).Content.ReadFromJsonAsync<SessionDto>())!;
            var started = await Eventually(() => boris.GetFromJsonAsync<SessionDto>($"/api/v1/sessions/{session.Id}")!,
                s => s!.Status == SessionStatus.Running);
            Assert.Equal(SessionStatus.Running, started!.Status);

            // Someone else may not remove it; its owner may.
            Assert.Equal(HttpStatusCode.Forbidden, (await alice.DeleteAsync($"/api/v1/machines/{state.MachineId}")).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await boris.DeleteAsync($"/api/v1/machines/{state.MachineId}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await boris.DeleteAsync($"/api/v1/machines/{state.MachineId}")).StatusCode);

            // The daemon is disconnected, and signing in again is refused, so it stops instead of retrying.
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => running.WaitAsync(Timeout));
            Assert.Empty((await boris.GetFromJsonAsync<MachineDto[]>("/api/v1/machines"))!);

            // The session stays readable and says why it stopped; nothing can run it any more.
            var after = (await boris.GetFromJsonAsync<SessionDto>($"/api/v1/sessions/{session.Id}"))!;
            Assert.Equal((SessionStatus.Stopped, false), (after.Status, after.CanControl));
            var notice = (await boris.GetFromJsonAsync<SessionItemDto[]>($"/api/v1/sessions/{session.Id}/items"))!.Last();
            Assert.Equal(SessionItemKind.Notice, notice.Kind);
            Assert.Contains("was removed", notice.Text);
            Assert.Equal(HttpStatusCode.NotFound, (await boris.PostAsJsonAsync($"/api/v1/sessions/{session.Id}/messages",
                new SendMessageRequest("Again"))).StatusCode);

            // An admin cleans up someone else's machine.
            var other = await SessionTest.EnrollAsync(server.BaseAddress, alice, Path.Combine(server.DataDirectory, "other"));
            Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/machines/{other.MachineId}")).StatusCode);
            Assert.Empty((await alice.GetFromJsonAsync<MachineDto[]>("/api/v1/machines"))!);
        }
        finally
        {
            ReplayTransport.Release("subscribed");
            await stop.CancelAsync();
        }
    }
}
