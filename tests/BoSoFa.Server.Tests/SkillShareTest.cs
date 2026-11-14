using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BoSoFa.Daemon;
using BoSoFa.Daemon.Sessions;
using BoSoFa.Protocol;
using BoSoFa.Server.Agents;

namespace BoSoFa.Server.Tests;

/// <summary>Signing in through SkillShare, its skills and each person's own SkillShare MCP servers in sessions (docs/skillshare.md).</summary>
[Collection("Replay")]
public class SkillShareTest
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    static readonly FakeSkillShare.Person Boris = new("sub-boris", "boris@example.com", "Boris", Admin: true, Oid: "oid-boris");
    static readonly FakeSkillShare.Person Alice = new("sub-alice", "alice@example.com", "Alice", Oid: "oid-alice");

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

    static void AssertNoFileHolds(string directory, IEnumerable<string> secrets)
    {
        var needles = secrets.Select(Encoding.UTF8.GetBytes).ToArray();
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            byte[] content;
            try { content = File.ReadAllBytes(file); }
            catch (IOException) { continue; }
            foreach (var needle in needles) Assert.True(content.AsSpan().IndexOf(needle) < 0, $"{file} holds a SkillShare token in the clear");
        }
    }

    [Fact]
    public async Task SigningInThroughSkillShareKeepsAGrantAndTheRoleFollowsSkillShare()
    {
        await using var skillShare = await FakeSkillShare.StartAsync();
        await using var server = await TestServer.StartAsync(extraArgs: skillShare.ServerArgs());

        // The first person would be an admin by default; SkillShare says she is not one.
        using var alice = await skillShare.SignInAsync(server, Alice);
        var me = (await alice.GetFromJsonAsync<UserDto>("/api/v1/me"))!;
        Assert.Equal((UserRole.Member, "Alice", "alice@example.com"), (me.Role, me.Name, me.Email));
        Assert.Contains("offline_access", skillShare.LastAuthorizeScope!.Split(' '));
        Assert.Contains("mcp.use", skillShare.LastAuthorizeScope.Split(' '));
        var status = (await alice.GetFromJsonAsync<SkillShareStatusDto>("/api/v1/skillshare"))!;
        Assert.Equal((true, SkillShareGrantStatus.Active, "SkillShare"), (status.Enabled, status.Grant, status.Name));
        Assert.Null(status.ActiveGrants); // only admins see the figures

        using var boris = await skillShare.SignInAsync(server, Boris);
        Assert.Equal(UserRole.Admin, (await boris.GetFromJsonAsync<UserDto>("/api/v1/me"))!.Role);
        Assert.Equal(2, (await boris.GetFromJsonAsync<SkillShareStatusDto>("/api/v1/skillshare"))!.ActiveGrants);

        // Made an admin in SkillShare, she is one here at her next sign-in.
        using var again = await skillShare.SignInAsync(server, Alice with { Admin = true });
        Assert.Equal(me.Id, (await again.GetFromJsonAsync<UserDto>("/api/v1/me"))!.Id);
        Assert.Equal(UserRole.Admin, (await again.GetFromJsonAsync<UserDto>("/api/v1/me"))!.Role);
        Assert.Contains("user.role", await boris.GetStringAsync("/api/v1/audit"));

        await server.StopAsync();
        AssertNoFileHolds(server.DataDirectory, skillShare.RefreshTokensIssued);
        await server.StartAppAsync();
    }

    [Fact]
    public async Task APersonOfThePreviousProviderKeepsTheirUser()
    {
        await using var skillShare = await FakeSkillShare.StartAsync();
        await using var server = await TestServer.StartAsync(extraArgs: skillShare.ServerArgs("--BoSoFa:Oidc:MigrateFromIssuer", "dev"));
        using var before = server.Client();
        (await before.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("alice@example.com", "Alice"))).EnsureSuccessStatusCode();
        var old = (await before.GetFromJsonAsync<UserDto>("/api/v1/me"))!;

        using var alice = await skillShare.SignInAsync(server, Alice with { Email = "Alice@Example.com", Admin = true });
        Assert.Equal(old.Id, (await alice.GetFromJsonAsync<UserDto>("/api/v1/me"))!.Id);
        Assert.Contains("user.migrated", await alice.GetStringAsync("/api/v1/audit"));

        // The old user was taken over once: someone else with that email is someone new.
        using var other = await skillShare.SignInAsync(server, new("sub-other", "alice@example.com", "Other"));
        Assert.NotEqual(old.Id, (await other.GetFromJsonAsync<UserDto>("/api/v1/me"))!.Id);
    }

    [Fact]
    public async Task ASessionGetsASkillShareSkillAndThePersonsOwnMcpServer()
    {
        await using var skillShare = await FakeSkillShare.StartAsync();
        var published = DateTime.UtcNow.AddHours(-1);
        skillShare.Skills.Add(new("42", "Grafana fetch", "Fetches logs", "1.0", "---\nname: grafanafetch\n---\nFetch it.\n", published));
        skillShare.Skills.Add(new("42", "Grafana fetch", "Fetches logs", "2.0", "---\nname: grafanafetch\n---\nFetch it better.\n", published.AddMinutes(1)));
        skillShare.Mcps["sub-boris"] = new() { ["youtrack"] = true, ["teams"] = false };
        await using var server = await TestServer.StartAsync(extraArgs: skillShare.ServerArgs());
        using var boris = await skillShare.SignInAsync(server, Boris);

        // The first sign-in reads the catalog; an admin can ask again.
        (await boris.PostAsync("/api/v1/skillshare/sync", null)).EnsureSuccessStatusCode();
        var skills = await Eventually(async () => (await boris.GetFromJsonAsync<SkillShareSkillDto[]>("/api/v1/skillshare/skills"))!, found => found.Length == 1);
        var skill = Assert.Single(skills);
        Assert.Equal(("grafana-fetch", "2.0", 2), (skill.BundleName, skill.LatestVersion, skill.Versions.Length));
        var mcps = (await boris.GetFromJsonAsync<SkillShareMcpDto[]>("/api/v1/skillshare/mcps"))!;
        Assert.Equal([("teams", false), ("youtrack", true)], mcps.Select(server => (server.Id, server.On)).OrderBy(pair => pair.Id).ToArray());

        // A profile may name SkillShare's skills and servers; what is not there is refused.
        Assert.Equal(HttpStatusCode.NotFound, (await boris.PostAsJsonAsync("/api/v1/profiles", new ProfileDto("broken", null, null, [],
            [new("43", SkillSources.Latest, SkillSources.SkillShare)]))).StatusCode);
        (await boris.PostAsJsonAsync("/api/v1/profiles", new ProfileDto("ops", null, null, ["skillshare:youtrack"],
            [new("42", "1.0", SkillSources.SkillShare)]))).EnsureSuccessStatusCode();

        var directory = Path.Combine(Path.GetTempPath(), "bosofa-skillshare-" + Guid.NewGuid().ToString("N"));
        try
        {
            var state = await SessionTest.EnrollAsync(server.BaseAddress, boris, directory);
            using var key = new DaemonStore(directory).LoadOrCreateKey();
            var source = new ServerAccountLeaser(state, key);
            using var outbox = new Outbox(Path.Combine(directory, "outbox"));
            await using var host = new SessionHost(directory, outbox, new(ReplayFixture: SessionTest.Fixture, Profiles: source), TextWriter.Null);
            using var stop = new CancellationTokenSource();
            var running = new DaemonLink(state, key, TextWriter.Null, outbox, host).RunAsync(stop.Token);

            // No profile: the session's own skill, at its latest version, and the person's servers that are on.
            var session = (await (await boris.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest(state.MachineId, Harnesses.Replay,
                directory, "hello", null, null, Skills: [new("42", SkillSources.Latest, SkillSources.SkillShare)]))).Content.ReadFromJsonAsync<SessionDto>())!;
            var skillFile = Path.Combine(directory, "session-skills", session.Id.ToString(), "skills", "grafana-fetch", "SKILL.md");
            await Eventually(() => Task.FromResult(File.Exists(skillFile)), found => found);
            Assert.Contains("Fetch it better.", await File.ReadAllTextAsync(skillFile));
            await Eventually(() => Task.FromResult(skillShare.Downloads.Count), count => count > 0);
            Assert.Contains("boris@example.com 42 2.0", skillShare.Downloads);
            // SkillShare had not hashed 2.0 before; the mirror learned the hash from the archive.
            Assert.Contains((await boris.GetFromJsonAsync<SkillShareSkillDto[]>("/api/v1/skillshare/skills"))![0].Versions,
                version => version.Version == "2.0" && version.Sha256.Length == 64);

            // The harness knows only the daemon's gateway; the daemon asks the server for the person's token.
            var mcpConfig = Path.Combine(directory, "mcp", $"{session.Id}.json");
            await Eventually(() => Task.FromResult(File.Exists(mcpConfig)), found => found);
            string url, authorization;
            using (var config = JsonDocument.Parse(await File.ReadAllTextAsync(mcpConfig)))
            {
                var servers = config.RootElement.GetProperty("mcpServers");
                Assert.False(servers.TryGetProperty("teams", out _)); // not connected in SkillShare
                var youtrack = servers.GetProperty("youtrack");
                url = youtrack.GetProperty("url").GetString()!;
                authorization = youtrack.GetProperty("headers").GetProperty("Authorization").GetString()!;
            }
            Assert.EndsWith($"/mcp/{session.Id}/youtrack", url);
            using var harness = new HttpClient();
            async Task<HttpResponseMessage> CallAsync(string method)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method }.ToJsonString(), Encoding.UTF8,
                        "application/json")
                };
                request.Headers.TryAddWithoutValidation("Authorization", authorization);
                return await harness.SendAsync(request);
            }
            using (var initialized = await CallAsync("initialize")) Assert.Equal(HttpStatusCode.OK, initialized.StatusCode);
            Assert.Contains("youtrack initialize boris@example.com", skillShare.McpCalls);

            // An expired token is replaced by a fresh one; a revoked grant ends it.
            skillShare.ForgetAccessTokens();
            using (var listed = await CallAsync("tools/list")) Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
            Assert.Contains("youtrack tools/list boris@example.com", skillShare.McpCalls);
            skillShare.Revoke("sub-boris");
            using (var refused = await CallAsync("tools/list")) Assert.Equal(HttpStatusCode.BadGateway, refused.StatusCode);
            Assert.Equal(SkillShareGrantStatus.Expired, (await boris.GetFromJsonAsync<SkillShareStatusDto>("/api/v1/skillshare"))!.Grant);
            Assert.Contains("skillshare.token", await boris.GetStringAsync("/api/v1/audit"));

            // Another machine never gets the session's token.
            var otherDirectory = Path.Combine(directory, "other");
            var other = await SessionTest.EnrollAsync(server.BaseAddress, boris, otherDirectory);
            using var otherKey = new DaemonStore(otherDirectory).LoadOrCreateKey();
            var stolen = await Assert.ThrowsAsync<HttpRequestException>(() =>
                new ServerAccountLeaser(other, otherKey).GetSkillShareTokenAsync(session.Id, "youtrack", false, default));
            Assert.Equal(HttpStatusCode.Forbidden, stolen.StatusCode);

            await stop.CancelAsync();
            await running.WaitAsync(Timeout);
            AssertNoFileHolds(directory, skillShare.RefreshTokensIssued);
        }
        finally
        {
            try { Directory.Delete(directory, true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task TheServerAgentsMcpClientAsksForAFreshTokenWhenRefused()
    {
        await using var skillShare = await FakeSkillShare.StartAsync();
        await using var server = await TestServer.StartAsync(extraArgs: skillShare.ServerArgs());
        skillShare.Mcps["sub-boris"] = new() { ["youtrack"] = true };
        using var boris = await skillShare.SignInAsync(server, Boris);
        var me = (await boris.GetFromJsonAsync<UserDto>("/api/v1/me"))!;
        var client = server.Service<BoSoFa.Server.SkillShare.SkillShareClient>();

        var asked = new List<bool>();
        using var http = new HttpClient();
        var mcp = new McpHttpClient(http, "youtrack", new Uri(skillShare.McpUrl("youtrack")), new Dictionary<string, string>(), async (fresh, stop) =>
        {
            asked.Add(fresh);
            return fresh ? (await client.UserTokenAsync(me.Id, skillShare.McpUrl("youtrack"), true, stop)).Token : "stale-token";
        });
        Assert.Equal("search_issues", Assert.Single(await mcp.ListToolsAsync(default)).Name);
        Assert.Equal([false, true], asked.Take(2));
        Assert.Contains("mcp.use", skillShare.TokenRequests.Last());
    }
}
