using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BoSoFa.Daemon;
using BoSoFa.Daemon.Sessions;
using BoSoFa.Protocol;
using BoSoFa.Server.Agents;

namespace BoSoFa.Server.Tests;

/// <summary>docs/secrets.md §4.3: an agent learns the names it can refer to, never a value.</summary>
public class SecretNamesTest
{
    const string BorisOwn = "boris_own_deploy_token_0123456789", Team = "the_teams_deploy_token_GHIJKL", Vault = "boris_vault_token_ABCDEFGH",
        Alices = "alices_private_token_ZYXWVU", Prod = "prod_deploy_token_9876543210";

    static async Task<HttpClient> SignIn(TestServer server, string email)
    {
        var client = server.Client();
        (await client.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest(email, null))).EnsureSuccessStatusCode();
        return client;
    }

    static async Task Ok(Task<HttpResponseMessage> request)
    {
        var response = await request;
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    static async Task<(DaemonState State, ServerAccountLeaser Daemon)> MachineAsync(TestServer server, HttpClient owner, string name)
    {
        var directory = Path.Combine(server.DataDirectory, "daemon-" + name);
        var state = await SessionTest.EnrollAsync(server.BaseAddress, owner, directory);
        await Ok(owner.PutAsJsonAsync($"/api/v1/machines/{state.MachineId}/name", new RenameMachineRequest(name)));
        return (state, new ServerAccountLeaser(state, new DaemonStore(directory).LoadOrCreateKey()));
    }

    static async Task<ulong> StartAsync(TestServer server, HttpClient client, ulong machineId, string profile)
    {
        var response = await client.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest(machineId, Harnesses.Replay,
            server.DataDirectory, "Deploy it", null, null, null, false, profile));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<SessionDto>())!.Id;
    }

    [Fact]
    public async Task ADaemonGetsTheNamesItsSessionsCreatorCanReferTo()
    {
        await using var server = await TestServer.StartAsync();
        using var boris = await SignIn(server, "boris@example.com"); // the admin
        using var alice = await SignIn(server, "alice@example.com");
        var (build, buildDaemon) = await MachineAsync(server, boris, "build-mac");
        var (laptop, laptopDaemon) = await MachineAsync(server, boris, "laptop");
        await Ok(boris.PostAsJsonAsync("/api/v1/profiles", new ProfileDto("plain", null, null, [], [])));
        await Ok(boris.PutAsJsonAsync("/api/v1/secrets/Personal/DEPLOY_TOKEN", new SetSecretRequest(SecretKind.Secret, BorisOwn, Machines: [build.MachineId])));
        await Ok(boris.PutAsJsonAsync("/api/v1/secrets/Shared/DEPLOY_TOKEN", new SetSecretRequest(SecretKind.Secret, Team)));
        await Ok(boris.PutAsJsonAsync("/api/v1/secrets/Personal/VAULT_TOKEN", new SetSecretRequest(SecretKind.Secret, Vault, HostOnly: true)));
        await Ok(boris.PutAsJsonAsync("/api/v1/secrets/Shared/GRAFANA_URL", new SetSecretRequest(SecretKind.Variable, "https://grafana.example.com")));
        await Ok(alice.PutAsJsonAsync("/api/v1/secrets/Personal/ALICE_TOKEN", new SetSecretRequest(SecretKind.Secret, Alices)));

        // On the build machine his own value of DEPLOY_TOKEN wins; Alice's values are not his to know of.
        var onBuild = await StartAsync(server, boris, build.MachineId, "plain");
        Assert.Equal([
            new("DEPLOY_TOKEN", SecretKind.Secret, SecretScope.Personal),
            new("GRAFANA_URL", SecretKind.Variable, SecretScope.Shared),
            new SessionSecretName("VAULT_TOKEN", SecretKind.Secret, SecretScope.Personal, HostOnly: true)
        ], await buildDaemon.ListSecretNamesAsync(onBuild, default));
        // Elsewhere his own is not for the machine, so the team's is the one a reference gets.
        var onLaptop = await StartAsync(server, boris, laptop.MachineId, "plain");
        Assert.Equal(SecretScope.Shared, (await laptopDaemon.ListSecretNamesAsync(onLaptop, default)).Single(name => name.Name == "DEPLOY_TOKEN").Scope);
        // Another machine cannot ask for a session's names.
        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => laptopDaemon.ListSecretNamesAsync(onBuild, default));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        // An environment's value comes first.
        await Ok(boris.PutAsJsonAsync("/api/v1/environments/production", new SaveEnvironmentRequest("", [], [])));
        await Ok(boris.PutAsJsonAsync("/api/v1/environments/production/secrets/DEPLOY_TOKEN", new SetSecretRequest(SecretKind.Secret, Prod)));
        await Ok(boris.PostAsJsonAsync("/api/v1/profiles", new ProfileDto("deploy", null, null, [], [], EnvironmentName: "production")));
        var inProduction = await StartAsync(server, boris, build.MachineId, "deploy");
        var names = await buildDaemon.ListSecretNamesAsync(inProduction, default);
        Assert.Equal(SecretScope.Environment, names.Single(name => name.Name == "DEPLOY_TOKEN").Scope);

        // What the agent is told names references, never a value.
        var text = SecretNames.Describe(names);
        Assert.Contains("${secret:DEPLOY_TOKEN} (secret, environment)", text);
        Assert.Contains("${var:GRAFANA_URL} (variable, shared)", text);
        Assert.Contains("${secret:VAULT_TOKEN} (secret, personal, for MCP servers only", text);
        foreach (var value in new[] { BorisOwn, Team, Vault, Alices, Prod, "grafana.example.com" }) Assert.DoesNotContain(value, text);
    }

    sealed class Names(SessionSecretName[] names) : IProfileSource
    {
        public ConcurrentQueue<ulong> Asked { get; } = new();
        public Task<SessionProfileResponse> GetProfileAsync(ulong sessionId, CancellationToken cancellation) => throw new NotSupportedException();
        public Task DownloadBlobAsync(string sha256, string file, CancellationToken cancellation) => throw new NotSupportedException();

        public Task<SessionSecretName[]> ListSecretNamesAsync(ulong sessionId, CancellationToken cancellation)
        {
            Asked.Enqueue(sessionId);
            return Task.FromResult(names);
        }
    }

    sealed class Sink : ISessionSink
    {
        public void Report(ulong sessionId, SessionReport report) { }
        public void Delta(ulong sessionId, string itemRef, string text) { }
    }

    [Fact]
    public async Task TheDaemonsMcpEndpointListsTheNames()
    {
        var source = new Names([new("DEPLOY_TOKEN", SecretKind.Secret, SecretScope.Shared)]);
        await using var endpoint = new McpEndpoint(_ => null, new Sink(), null, source);
        var server = endpoint.ServerFor(7);
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse(server.Secrets["Authorization"]);

        async Task<JsonElement> Call(string method, string parameters)
        {
            var response = await client.PostAsync(server.Url, new StringContent($"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"{method}\",\"params\":{parameters}}}",
                Encoding.UTF8, "application/json"));
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("result");
        }

        var tools = (await Call("tools/list", "{}")).GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()).ToList();
        Assert.Contains(McpEndpoint.SecretsTool, tools);
        Assert.Contains($"mcp__{McpEndpoint.ServerName}__{McpEndpoint.SecretsTool}", McpEndpoint.PreApprovedClaudeTools);
        var result = await Call("tools/call", $"{{\"name\":\"{McpEndpoint.SecretsTool}\",\"arguments\":{{}}}}");
        Assert.False(result.GetProperty("isError").GetBoolean());
        Assert.Contains("${secret:DEPLOY_TOKEN} (secret, shared)", result.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal([7UL], source.Asked);

        // Without a server to ask, the tool says so.
        await using var alone = new McpEndpoint(_ => null, new Sink(), null);
        var aloneServer = alone.ServerFor(7);
        client.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse(aloneServer.Secrets["Authorization"]);
        var response = await client.PostAsync(aloneServer.Url, new StringContent(
            $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{{\"name\":\"{McpEndpoint.SecretsTool}\",\"arguments\":{{}}}}}}", Encoding.UTF8, "application/json"));
        Assert.True(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task TheServerAgentListsTheNamesInEveryModeWithoutAsking()
    {
        var calls = 0;
        var (toolbox, problems) = await AgentToolbox.LoadAsync(new HttpClient(), [], null, false, AgentToolGroups.Full,
            (_, _, _) => Task.FromResult(new McpToolResult("", true)), default,
            _ =>
            {
                calls++;
                return Task.FromResult(new McpToolResult(SecretNames.Describe([new("GRAFANA_URL", SecretKind.Variable, SecretScope.Shared)]), false));
            });
        Assert.Empty(problems);
        foreach (var mode in new[] { "plan", "default", "bypassPermissions" })
        {
            Assert.Contains(toolbox.SpecsFor(mode), spec => spec.Name == AgentToolbox.SecretsTool);
            Assert.Equal(AgentToolKind.Secrets, toolbox.KindOf(AgentToolbox.SecretsTool, mode));
            Assert.False(AgentToolbox.NeedsApproval(AgentToolKind.Secrets, mode));
        }
        Assert.False(toolbox.RunsOnMachine(AgentToolbox.SecretsTool));
        var result = await toolbox.CallAsync(AgentToolbox.SecretsTool, new JsonObject(), new AgentToolCall("1:1", "tool-1", 1), default);
        Assert.Equal((false, 1), (result.IsError, calls));
        Assert.Contains("${var:GRAFANA_URL} (variable, shared)", result.Text);
    }
}
