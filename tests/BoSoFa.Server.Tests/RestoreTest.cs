using System.Net;
using System.Net.Http.Json;
using BoSoFa.Daemon;
using BoSoFa.Daemon.Harnesses.Claude;
using BoSoFa.Daemon.Sessions;
using BoSoFa.Protocol;
using BoSoFa.Server.Machines;
using Xunit;

namespace BoSoFa.Server.Tests;

/// <summary>A server restored from a backup forgets, and hands out again, IDs and seqs its daemons still remember.</summary>
[Collection("Replay")]
public class RestoreTest
{
    static async Task<T> Eventually<T>(Func<Task<T>> probe, Func<T, bool> done, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            var value = await probe();
            if (done(value)) return value;
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for " + what);
            await Task.Delay(50);
        }
    }

    static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var directory in Directory.GetDirectories(from)) Copy(directory, Path.Combine(to, Path.GetFileName(directory)));
    }

    /// <summary>Waits for the turn's approval, grants it and waits for the turn to end.</summary>
    static async Task FinishTurnAsync(HttpClient client, ulong session, int results)
    {
        var requests = await Eventually(() => client.GetFromJsonAsync<RuntimeRequestDto[]>($"/api/v1/sessions/{session}/requests")!,
            pending => pending!.Length > 0, "the approval");
        (await client.PostAsJsonAsync($"/api/v1/sessions/{session}/requests/{requests![0].Id}/answer",
            new AnswerRuntimeRequest(true, null))).EnsureSuccessStatusCode();
        await Eventually(() => client.GetFromJsonAsync<SessionItemDto[]>($"/api/v1/sessions/{session}/items")!,
            items => items!.Count(item => item.Kind == SessionItemKind.ToolResult) == results, "the tool result");
        await Eventually(() => client.GetFromJsonAsync<SessionDto>($"/api/v1/sessions/{session}")!,
            dto => dto!.Status == SessionStatus.Idle, "the end of the turn");
    }

    [Fact]
    public async Task ADaemonKeepsWorkingWithAServerRestoredFromABackup()
    {
        ReplayTransport.SignalTimeoutOverride = TimeSpan.Zero;
        var dataDirectory = Path.Combine(Path.GetTempPath(), "bosofa-restore-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(dataDirectory, "backup");
        var daemonDirectory = Path.Combine(dataDirectory, "daemon");
        var work = Directory.CreateDirectory(Path.Combine(dataDirectory, "work")).FullName;
        try
        {
            await using var server = await TestServer.StartAsync(Path.Combine(dataDirectory, "server"));
            var cookies = new CookieContainer();
            using var boris = server.Client(cookies);
            (await boris.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("boris@example.com", null))).EnsureSuccessStatusCode();
            var state = await SessionTest.EnrollAsync(server.BaseAddress, boris, daemonDirectory);
            using var key = new DaemonStore(daemonDirectory).LoadOrCreateKey();
            using var outbox = new Outbox(Path.Combine(daemonDirectory, "outbox"));
            await using var host = new SessionHost(daemonDirectory, outbox, new(ReplayFixture: SessionTest.Fixture), TextWriter.Null);
            using var stop = new CancellationTokenSource();
            var running = new DaemonLink(state, key, TextWriter.Null, outbox, host).RunAsync(stop.Token);

            async Task OnlineAsync() => await Eventually(() => boris.GetFromJsonAsync<MachineDto[]>("/api/v1/machines")!,
                machines => machines!.Single().Online, "the daemon");
            async Task<ulong> StartAsync(string prompt)
            {
                var created = await boris.PostAsJsonAsync("/api/v1/sessions",
                    new CreateSessionRequest(state.MachineId, Harnesses.Replay, work, prompt, null, null));
                created.EnsureSuccessStatusCode();
                var session = (await created.Content.ReadFromJsonAsync<SessionDto>())!.Id;
                await FinishTurnAsync(boris, session, 1);
                return session;
            }
            async Task<int> ItemsAsync(ulong session) =>
                (await boris.GetFromJsonAsync<SessionItemDto[]>($"/api/v1/sessions/{session}/items"))!.Length;
            async Task SendAsync(ulong session, int results)
            {
                (await boris.PostAsJsonAsync($"/api/v1/sessions/{session}/messages", new SendMessageRequest("Again"))).EnsureSuccessStatusCode();
                await FinishTurnAsync(boris, session, results);
            }

            await OnlineAsync();
            var kept = await StartAsync("Before the backup");
            await Eventually(() => Task.FromResult(outbox.Count), count => count == 0, "the outbox");
            var itemsAtBackup = await ItemsAsync(kept);

            await server.StopAsync();
            Copy(server.DataDirectory, backup);
            await server.StartAppAsync();
            await OnlineAsync();

            // Everything from here is forgotten by the restore; the daemon has seen it and been told it is stored.
            var forgotten = await StartAsync("After the backup");
            await SendAsync(kept, 2);
            await Eventually(() => Task.FromResult(outbox.Count), count => count == 0, "the outbox");

            await server.StopAsync();
            Directory.Delete(server.DataDirectory, true);
            Copy(backup, server.DataDirectory);
            await server.StartAppAsync();
            await OnlineAsync();
            Assert.Equal(itemsAtBackup, await ItemsAsync(kept));

            // The same session and command IDs are handed out again, to a different session.
            var reused = await StartAsync("After the restore");
            Assert.Equal(forgotten, reused);
            var items = await boris.GetFromJsonAsync<SessionItemDto[]>($"/api/v1/sessions/{reused}/items");
            Assert.Equal("After the restore", items!.First(item => item.Kind == SessionItemKind.User).Text);
            Assert.DoesNotContain(items!, item => item.Kind == SessionItemKind.Notice);

            // A session that survived goes on, and says what it lost.
            await SendAsync(kept, 2);
            var keptItems = await boris.GetFromJsonAsync<SessionItemDto[]>($"/api/v1/sessions/{kept}/items");
            Assert.Contains(keptItems!, item => item is { Kind: SessionItemKind.Notice } && item.Text.Contains("restored from a backup"));

            await stop.CancelAsync();
            await running;

            // Without the daemon's word that the server had them, a gap is only a resend still on its way.
            var publisher = await server.Node.Ready;
            var report = new ItemReport(SessionItemKind.Notice, DateTime.UtcNow, "after a gap");
            var last = (await MachineEndpoints.StoreReportsAsync(publisher, state.MachineId,
                new([new(reused, 1, report)]))).Acks.Single().Seq;
            var before = await ItemsAsync(reused);
            var ack = await MachineEndpoints.StoreReportsAsync(publisher, state.MachineId, new([new(reused, last + 5, report, last)]));
            Assert.Equal(last, ack.Acks.Single().Seq);
            Assert.Equal(before, await ItemsAsync(reused));
            ack = await MachineEndpoints.StoreReportsAsync(publisher, state.MachineId, new([new(reused, last + 5, report, last + 1)]));
            Assert.Equal(last + 5, ack.Acks.Single().Seq);
            Assert.True(await ItemsAsync(reused) > before);
        }
        finally
        {
            ReplayTransport.SignalTimeoutOverride = null;
            try { Directory.Delete(dataDirectory, true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task ACommandIsTheSameOnlyWithTheSameIdAndIssueTime()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bosofa-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var issued = new DateTime(2026, 11, 8, 10, 0, 0, DateTimeKind.Utc);
        var command = new InterruptCommand(99);
        try
        {
            using var outbox = new Outbox(Path.Combine(directory, "outbox"));
            await using (var host = new SessionHost(directory, outbox, new(), TextWriter.Null))
            {
                Assert.True(await host.ApplyAsync(1, issued, command));
                Assert.False(await host.ApplyAsync(1, issued, command));
                Assert.True(await host.ApplyAsync(1, issued.AddSeconds(1), command));
            }
            await using (var host = new SessionHost(directory, outbox, new(), TextWriter.Null))
                Assert.False(await host.ApplyAsync(1, issued, command));
        }
        finally { Directory.Delete(directory, true); }
    }
}
