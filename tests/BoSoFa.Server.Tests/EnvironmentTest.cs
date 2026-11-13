using System.Net;
using System.Net.Http.Json;
using BoSoFa.Daemon;
using BoSoFa.Protocol;
using BoSoFa.Server.Domain;
using BTDB.Replication.EventSourcing;

namespace BoSoFa.Server.Tests;

/// <summary>Step 6 of docs/secrets.md: values limited to machines, where they went, rotation reminders, and environments whose
/// reviewers approve each session.</summary>
public class EnvironmentTest
{
    const string Prod = "prod_deploy_token_0123456789", AlicesOwn = "alices_own_deploy_token_ABCDEF", Team = "the_teams_deploy_token_GHIJKL",
        Rotated = "rotated_deploy_token_9876543210";

    static async Task<HttpClient> SignIn(TestServer server, string email)
    {
        var client = server.Client();
        (await client.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest(email, null))).EnsureSuccessStatusCode();
        return client;
    }

    static async Task<ulong> IdOf(HttpClient client) => (await client.GetFromJsonAsync<UserDto>("/api/v1/me"))!.Id;

    static async Task Ok(Task<HttpResponseMessage> request)
    {
        var response = await request;
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    static async Task<string> Refused(Task<HttpResponseMessage> request, HttpStatusCode status = HttpStatusCode.BadRequest)
    {
        var response = await request;
        Assert.Equal(status, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>A machine of <paramref name="owner"/>, named, with what its daemon resolves profiles with.</summary>
    static async Task<(DaemonState State, ServerAccountLeaser Daemon)> MachineAsync(TestServer server, HttpClient owner, string name)
    {
        var directory = Path.Combine(server.DataDirectory, "daemon-" + name);
        var state = await SessionTest.EnrollAsync(server.BaseAddress, owner, directory);
        await Ok(owner.PutAsJsonAsync($"/api/v1/machines/{state.MachineId}/name", new RenameMachineRequest(name)));
        return (state, new ServerAccountLeaser(state, new DaemonStore(directory).LoadOrCreateKey()));
    }

    static async Task<SessionDto> StartAsync(TestServer server, HttpClient client, ulong machineId, string profile)
    {
        var response = await client.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest(machineId, Harnesses.Replay,
            server.DataDirectory, "Deploy it", null, null, null, false, profile));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<SessionDto>())!;
    }

    /// <summary>The commands the machine has yet to take for a session: what started it, here.</summary>
    static int CommandsFor(TestServer server, ulong machineId, ulong sessionId)
    {
        using var query = server.Node.Publisher!.StartQuery();
        return Ambient.Relation<IMachineCommandTable>().Count(command => command.MachineId == machineId && System.Text.RegularExpressions.Regex.IsMatch(command.Command, $"\"sessionId\":{sessionId}[,}}]"));
    }

    static async Task<SessionItemDto[]> Items(HttpClient client, ulong sessionId) =>
        (await client.GetFromJsonAsync<SessionItemDto[]>($"/api/v1/sessions/{sessionId}/items"))!;

    static async Task<HttpStatusCode> ResolveStatus(ServerAccountLeaser daemon, ulong sessionId)
    {
        try
        {
            await daemon.GetProfileAsync(sessionId, default);
            return HttpStatusCode.OK;
        }
        catch (HttpRequestException exception) { return exception.StatusCode!.Value; }
    }

    [Fact]
    public async Task AValueGoesOnlyToItsMachinesAndOneThatWasOnARemovedMachineIsDueForRotation()
    {
        await using var server = await TestServer.StartAsync();
        using var boris = await SignIn(server, "boris@example.com"); // the admin
        using var alice = await SignIn(server, "alice@example.com");
        var (build, buildDaemon) = await MachineAsync(server, boris, "build-mac");
        var (laptop, laptopDaemon) = await MachineAsync(server, boris, "laptop");
        var (alicesBox, _) = await MachineAsync(server, alice, "alices-box");
        await Ok(boris.PostAsJsonAsync("/api/v1/profiles", new ProfileDto("deploy", null, null, [], [],
            Environment: new() { ["DEPLOY_TOKEN"] = "${secret:DEPLOY_TOKEN}" })));

        // A person's own values go to their own machines only.
        Assert.Contains("not your machine", await Refused(boris.PutAsJsonAsync("/api/v1/secrets/Personal/DEPLOY_TOKEN",
            new SetSecretRequest(SecretKind.Secret, AlicesOwn, Machines: [alicesBox.MachineId]))));
        var set = (await (await boris.PutAsJsonAsync("/api/v1/secrets/Personal/DEPLOY_TOKEN",
            new SetSecretRequest(SecretKind.Secret, Prod, Machines: [build.MachineId], RotateEveryDays: 30))).Content.ReadFromJsonAsync<SecretDto>())!;
        Assert.Equal([build.MachineId], set.Machines!);
        Assert.Equal(set.SetAt.AddDays(30), set.RotateBy);
        Assert.Contains("0 (never) to", await Refused(boris.PutAsJsonAsync("/api/v1/secrets/Personal/OTHER",
            new SetSecretRequest(SecretKind.Secret, Team, RotateEveryDays: -1))));

        // On the build machine the session gets the value; elsewhere it is not there, and the start names why.
        var onBuild = await StartAsync(server, boris, build.MachineId, "deploy");
        Assert.Equal(Prod, (await buildDaemon.GetProfileAsync(onBuild.Id, default)).Environment!["DEPLOY_TOKEN"]);
        var onLaptop = await StartAsync(server, boris, laptop.MachineId, "deploy");
        Assert.Equal(HttpStatusCode.Conflict, await ResolveStatus(laptopDaemon, onLaptop.Id));
        // A wider value of the name serves the machines the narrower one is not for.
        await Ok(boris.PutAsJsonAsync("/api/v1/secrets/Shared/DEPLOY_TOKEN", new SetSecretRequest(SecretKind.Secret, Team)));
        Assert.Equal(Team, (await laptopDaemon.GetProfileAsync(onLaptop.Id, default)).Environment!["DEPLOY_TOKEN"]);

        // Its owner sees where it went; a member does not see where a shared one went.
        var mine = Assert.Single((await boris.GetFromJsonAsync<SecretDto[]>("/api/v1/secrets"))!, secret => secret.Scope == SecretScope.Personal);
        Assert.Equal([("build-mac", 1)], mine.UsedOn!.Select(use => (use.Machine, use.Version)));
        Assert.Null(Assert.Single((await alice.GetFromJsonAsync<SecretDto[]>("/api/v1/secrets"))!).UsedOn);
        var audit = await boris.GetStringAsync("/api/v1/audit");
        Assert.Contains("DEPLOY_TOKEN version 1 (personal)", audit);
        Assert.Contains("DEPLOY_TOKEN version 1 (shared)", audit);

        // Settings change without a new version: now any of Boris's machines gets his own.
        var settings = (await (await boris.PutAsJsonAsync("/api/v1/secrets/Personal/DEPLOY_TOKEN/settings",
            new SecretSettingsRequest(false, [], 0))).Content.ReadFromJsonAsync<SecretDto>())!;
        Assert.Equal((1, 0, (DateTime?)null), (settings.Version, settings.RotateEveryDays, settings.RotateBy));
        Assert.Equal(Prod, (await laptopDaemon.GetProfileAsync(onLaptop.Id, default)).Environment!["DEPLOY_TOKEN"]);

        // A removed machine held the current versions: they are due for rotation, until rotated.
        await Ok(boris.DeleteAsync($"/api/v1/machines/{laptop.MachineId}"));
        var flagged = (await boris.GetFromJsonAsync<SecretDto[]>("/api/v1/secrets"))!;
        Assert.All(flagged, secret => Assert.Equal("It was given to laptop, which was removed.", secret.RotationReason));
        Assert.Equal(["build-mac"], flagged.Single(secret => secret.Scope == SecretScope.Personal).UsedOn!.Select(use => use.Machine));
        var rotated = (await (await boris.PutAsJsonAsync("/api/v1/secrets/Personal/DEPLOY_TOKEN",
            new SetSecretRequest(SecretKind.Secret, Rotated))).Content.ReadFromJsonAsync<SecretDto>())!;
        Assert.Equal((2, (string?)null), (rotated.Version, rotated.RotationReason));
        Assert.Empty(rotated.Machines!); // a rotation keeps the settings it does not name
    }

    [Fact]
    public async Task AnEnvironmentsSessionStartsOnceAReviewerApprovesIt()
    {
        await using var server = await TestServer.StartAsync();
        using var boris = await SignIn(server, "boris@example.com"); // the admin
        using var alice = await SignIn(server, "alice@example.com");
        using var carol = await SignIn(server, "carol@example.com");
        var (box, daemon) = await MachineAsync(server, alice, "alices-box");
        var (other, _) = await MachineAsync(server, boris, "other");
        var carolId = await IdOf(carol);
        var aliceId = await IdOf(alice);

        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PutAsJsonAsync("/api/v1/environments/production", new SaveEnvironmentRequest("", [], []))).StatusCode);
        await Ok(boris.PutAsJsonAsync("/api/v1/environments/production", new SaveEnvironmentRequest("Live customers", [carolId, aliceId], [])));
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PutAsJsonAsync("/api/v1/environments/production/secrets/DEPLOY_TOKEN",
            new SetSecretRequest(SecretKind.Secret, Prod))).StatusCode);
        await Ok(boris.PutAsJsonAsync("/api/v1/environments/production/secrets/DEPLOY_TOKEN", new SetSecretRequest(SecretKind.Secret, Prod)));
        await Ok(alice.PutAsJsonAsync("/api/v1/secrets/Personal/DEPLOY_TOKEN", new SetSecretRequest(SecretKind.Secret, AlicesOwn)));
        Assert.Contains("There is no environment staging", await Refused(boris.PostAsJsonAsync("/api/v1/profiles",
            new ProfileDto("deploy", null, null, [], [], EnvironmentName: "staging"))));
        await Ok(boris.PostAsJsonAsync("/api/v1/profiles", new ProfileDto("deploy", null, null, [], [],
            Environment: new() { ["DEPLOY_TOKEN"] = "${secret:DEPLOY_TOKEN}" }, EnvironmentName: "production")));
        var environment = Assert.Single((await alice.GetFromJsonAsync<EnvironmentDto[]>("/api/v1/environments"))!);
        Assert.Equal(("production", "Live customers", "…6789"), (environment.Name, environment.Description, Assert.Single(environment.Values).Hint));
        Assert.Equal(["alice@example.com", "carol@example.com"], environment.Reviewers.Select(reviewer => reviewer.Name).Order());

        // The session waits: nothing went to the machine, nothing resolves, and it takes no message.
        var session = await StartAsync(server, alice, box.MachineId, "deploy");
        Assert.Equal(SessionStatus.Starting, session.Status);
        Assert.Equal(0, CommandsFor(server, box.MachineId, session.Id));
        Assert.Contains(await Items(alice, session.Id), item => item.Text.Contains("starts once carol@example.com approves it"));
        Assert.Equal(HttpStatusCode.Conflict, await ResolveStatus(daemon, session.Id));
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync($"/api/v1/sessions/{session.Id}/messages", new SendMessageRequest("Hurry"))).StatusCode);

        // Only a reviewer other than its creator sees and answers it.
        Assert.Empty((await alice.GetFromJsonAsync<EnvironmentApprovalDto[]>("/api/v1/environment-approvals"))!);
        Assert.Empty((await boris.GetFromJsonAsync<EnvironmentApprovalDto[]>("/api/v1/environment-approvals"))!);
        var waiting = Assert.Single((await carol.GetFromJsonAsync<EnvironmentApprovalDto[]>("/api/v1/environment-approvals"))!);
        Assert.Equal((session.Id, "production", "alice@example.com", "alices-box", "deploy"),
            (waiting.SessionId, waiting.Environment, waiting.RequestedBy, waiting.Machine, waiting.Profile));
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PostAsJsonAsync($"/api/v1/environment-approvals/{session.Id}",
            new AnswerEnvironmentApprovalRequest(true))).StatusCode);
        await Ok(carol.PostAsJsonAsync($"/api/v1/environment-approvals/{session.Id}", new AnswerEnvironmentApprovalRequest(true)));
        Assert.Equal(1, CommandsFor(server, box.MachineId, session.Id));
        Assert.Contains(await Items(alice, session.Id), item => item.Text == "carol@example.com approved the use of the production environment.");
        // The environment's value wins over Alice's own.
        Assert.Equal(Prod, (await daemon.GetProfileAsync(session.Id, default)).Environment!["DEPLOY_TOKEN"]);
        Assert.Contains("DEPLOY_TOKEN version 1 (environment production)", await boris.GetStringAsync("/api/v1/audit"));
        Assert.Empty((await carol.GetFromJsonAsync<EnvironmentApprovalDto[]>("/api/v1/environment-approvals"))!);

        // A rejected session fails and never reaches its machine.
        var rejected = await StartAsync(server, alice, box.MachineId, "deploy");
        await Ok(carol.PostAsJsonAsync($"/api/v1/environment-approvals/{rejected.Id}", new AnswerEnvironmentApprovalRequest(false)));
        Assert.Equal(SessionStatus.Failed, (await alice.GetFromJsonAsync<SessionDto>($"/api/v1/sessions/{rejected.Id}"))!.Status);
        Assert.Contains(await Items(alice, rejected.Id), item => item.IsError && item.Text.Contains("did not approve"));
        Assert.Equal(0, CommandsFor(server, box.MachineId, rejected.Id));

        // A session stopped while it waits is no longer asked about.
        var stopped = await StartAsync(server, alice, box.MachineId, "deploy");
        await Ok(alice.PostAsync($"/api/v1/sessions/{stopped.Id}/stop", null));
        Assert.Equal(SessionStatus.Stopped, (await alice.GetFromJsonAsync<SessionDto>($"/api/v1/sessions/{stopped.Id}"))!.Status);
        Assert.Empty((await carol.GetFromJsonAsync<EnvironmentApprovalDto[]>("/api/v1/environment-approvals"))!);
        Assert.Equal(0, CommandsFor(server, box.MachineId, stopped.Id));

        // An environment for other machines: no session starts here, and the approved one resolves no more.
        await Ok(boris.PutAsJsonAsync("/api/v1/environments/production", new SaveEnvironmentRequest("Live customers", [carolId], [other.MachineId])));
        Assert.Contains("not for this machine", await Refused(alice.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest(box.MachineId,
            Harnesses.Replay, server.DataDirectory, "Deploy it", null, null, null, false, "deploy"))));
        Assert.Equal(HttpStatusCode.Conflict, await ResolveStatus(daemon, session.Id));

        // An environment a profile names stays; once none does, it goes with its values.
        Assert.Contains("The profile deploy uses production", await Refused(boris.DeleteAsync("/api/v1/environments/production")));
        await Ok(boris.PostAsJsonAsync("/api/v1/profiles", new ProfileDto("deploy", null, null, [], [])));
        await Ok(boris.DeleteAsync("/api/v1/environments/production"));
        Assert.Empty((await boris.GetFromJsonAsync<EnvironmentDto[]>("/api/v1/environments"))!);
        using var query = server.Node.Publisher!.StartQuery();
        Assert.DoesNotContain(Ambient.Relation<ISecretTable>(), secret => secret.Name == "DEPLOY_TOKEN" && secret.OwnerId != aliceId);
    }
}
