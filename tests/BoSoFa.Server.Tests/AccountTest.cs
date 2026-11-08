using System.Net;
using System.Net.Http.Json;
using System.Text;
using BoSoFa.Daemon;
using BoSoFa.Daemon.Sessions;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Tests;

[Collection("Replay")]
public class AccountTest
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    const string Token = "sk-ant-oat01-TestTokenThatIsLongEnough_0123456789";

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

    /// <summary>Remembers what the server leased, so the test sees what the harness would get.</summary>
    sealed class RecordingLeaser(IAccountLeaser inner) : IAccountLeaser
    {
        public List<AccountLeaseResponse> Leases { get; } = [];

        public async Task<AccountLeaseResponse> LeaseAsync(ulong accountId, ulong sessionId, CancellationToken cancellation)
        {
            var lease = await inner.LeaseAsync(accountId, sessionId, cancellation);
            lock (Leases) Leases.Add(lease);
            return lease;
        }
    }

    [Fact]
    public async Task AccountSecretsAreEncryptedAndLeasedOnlyToTheSessionsMachine()
    {
        await using var server = await TestServer.StartAsync();
        using var boris = await SignIn(server, "boris@example.com");
        using var alice = await SignIn(server, "alice@example.com");

        Assert.Equal(HttpStatusCode.BadRequest, (await boris.PostAsJsonAsync("/api/v1/accounts",
            new CreateHarnessAccountRequest(AccountKind.ClaudeOAuthToken, "Wrong", "sk-ant-api03-not-a-subscription-token-123"))).StatusCode);
        var added = await boris.PostAsJsonAsync("/api/v1/accounts",
            new CreateHarnessAccountRequest(AccountKind.ClaudeOAuthToken, "My Max plan", Token));
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var account = (await added.Content.ReadFromJsonAsync<HarnessAccountDto>())!;
        Assert.Equal(("My Max plan", "claude", "…6789"), (account.Label, account.Harness, account.SecretHint));
        Assert.True(account.ExpiresAt > DateTime.UtcNow.AddDays(360));
        Assert.DoesNotContain(Token, await boris.GetStringAsync("/api/v1/accounts")); // never back to a browser
        Assert.Empty((await alice.GetFromJsonAsync<HarnessAccountDto[]>("/api/v1/accounts"))!); // personal

        var daemonDirectory = Path.Combine(Path.GetTempPath(), "bosofa-account-daemon-" + Guid.NewGuid().ToString("N"));
        try
        {
            var state = await SessionTest.EnrollAsync(server.BaseAddress, boris, daemonDirectory);
            using var key = new DaemonStore(daemonDirectory).LoadOrCreateKey();
            var leaser = new RecordingLeaser(new ServerAccountLeaser(state, key));
            using var outbox = new Outbox(Path.Combine(daemonDirectory, "outbox"));
            await using var host = new SessionHost(daemonDirectory, outbox,
                new(ReplayFixture: SessionTest.Fixture, Accounts: leaser), TextWriter.Null);
            using var stop = new CancellationTokenSource();
            var running = new DaemonLink(state, key, TextWriter.Null, outbox, host).RunAsync(stop.Token);

            // Alice cannot run a session with Boris's account (nor on his machine).
            Assert.Equal(HttpStatusCode.Forbidden, (await alice.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest(
                state.MachineId, Harnesses.Replay, daemonDirectory, "hi", null, null, account.Id))).StatusCode);

            var created = await boris.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest(state.MachineId,
                Harnesses.Replay, daemonDirectory, "Create hello.txt", null, null, account.Id));
            var session = (await created.Content.ReadFromJsonAsync<SessionDto>())!;
            Assert.Equal((account.Id, "My Max plan"), (session.AccountId, session.AccountLabel));
            await Eventually(() => Task.FromResult(leaser.Leases.Count), count => count > 0);
            Assert.Equal(new AccountLeaseResponse(AccountKind.ClaudeOAuthToken, Token), Assert.Single(leaser.Leases));

            // Another machine (even with a valid token) gets nothing for this session.
            using var otherKey = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            var other = await SessionTest.EnrollAsync(server.BaseAddress, boris,
                Path.Combine(daemonDirectory, "other"));
            using var otherClient = new ServerClient(server.BaseAddress);
            var otherToken = await otherClient.GetTokenAsync(other.MachineId,
                new DaemonStore(Path.Combine(daemonDirectory, "other")).LoadOrCreateKey(), default);
            var refused = await Assert.ThrowsAsync<HttpRequestException>(() =>
                otherClient.LeaseAccountAsync(otherToken.AccessToken, account.Id, session.Id, default));
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

            // Removed: no more leases.
            Assert.Equal(HttpStatusCode.Forbidden, (await alice.DeleteAsync($"/api/v1/accounts/{account.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await boris.DeleteAsync($"/api/v1/accounts/{account.Id}")).StatusCode);
            var gone = await Assert.ThrowsAsync<HttpRequestException>(() =>
                leaser.LeaseAsync(account.Id, session.Id, default));
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);

            await stop.CancelAsync();
            await running.WaitAsync(Timeout);
        }
        finally
        {
            try { Directory.Delete(daemonDirectory, true); }
            catch (IOException) { }
        }

        // The event log and the database hold only ciphertext; the label (plain metadata) shows the scan reads them.
        var needle = Encoding.UTF8.GetBytes(Token);
        var label = Encoding.UTF8.GetBytes("My Max plan");
        var labelFound = false;
        foreach (var file in Directory.EnumerateFiles(server.DataDirectory, "*", SearchOption.AllDirectories))
        {
            byte[] content;
            try { content = await File.ReadAllBytesAsync(file); }
            catch (IOException) { continue; } // locked by the running server
            Assert.True(content.AsSpan().IndexOf(needle) < 0, $"{file} contains the secret in the clear");
            labelFound |= content.AsSpan().IndexOf(label) >= 0;
        }
        Assert.True(labelFound, "The scan found no stored events at all.");
    }
}
