using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BoSoFa.Protocol;
using Machine = BoSoFa.Server.Tests.AgentMachineTest.Machine;
using Shell = BoSoFa.Protocol.ShellCommands;

namespace BoSoFa.Server.Tests;

/// <summary>Commands the person runs in a session's folder with <c>!</c> (docs/shell-commands.md), on a real daemon.</summary>
public class ShellCommandTest
{
    static string Sleep(int seconds) => OperatingSystem.IsWindows() ? $"Start-Sleep {seconds}" : $"sleep {seconds}";

    static Task<HttpResponseMessage> RunAsync(Machine m, ulong session, string command, HttpClient? client = null) =>
        (client ?? m.H.Boris).PostAsJsonAsync($"/api/v1/sessions/{session}/shell-commands", new RunShellCommandRequest(command));

    /// <summary>The output of the command run <paramref name="index"/>-th in the session, once it has ended.</summary>
    static async Task<(SessionItemDto Command, SessionItemDto Output, ShellOutputData Data)> OutputAsync(Machine m, ulong session, int index = 0)
    {
        var items = await AgentSessionTest.Eventually(() => m.H.AllItemsAsync(session),
            all => all.Count(item => item.Kind == SessionItemKind.ShellOutput) > index);
        var command = items.Where(item => item.Kind == SessionItemKind.ShellCommand).ElementAt(index);
        var output = items.Single(item => item.Kind == SessionItemKind.ShellOutput && item.Ref == Shell.RefOf(command.Seq));
        return (command, output, JsonSerializer.Deserialize(output.Data!, ProtocolJsonContext.Default.ShellOutputData)!);
    }

    static async Task<SessionDto> IdleAgentSessionAsync(Machine m, string? profile = null)
    {
        m.H.Backend.Answer("Ready.");
        var session = await m.StartSessionAsync("Wait for me.", "bypassPermissions", profile);
        await m.H.RunEndedAsync(session.Id);
        return session;
    }

    [Fact]
    public async Task ACommandRunsInTheSessionsFolderWithItsProfilesVariablesMasked()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var m = await Machine.StartAsync();
        (await m.H.Boris.PutAsJsonAsync("/api/v1/secrets/Personal/DEPLOY", new SetSecretRequest(SecretKind.Secret, "deploy-token-91"))).EnsureSuccessStatusCode();
        (await m.H.Boris.PutAsJsonAsync("/api/v1/secrets/Shared/REGION", new SetSecretRequest(SecretKind.Variable, "eu-west"))).EnsureSuccessStatusCode();
        (await m.H.Boris.PostAsJsonAsync("/api/v1/profiles", new ProfileDto("shellenv", null, null, [], [],
            Environment: new() { ["DEPLOY_TOKEN"] = "${secret:DEPLOY}", ["AWS_REGION"] = "${var:REGION}" }))).EnsureSuccessStatusCode();
        var session = await IdleAgentSessionAsync(m, "shellenv");

        (await RunAsync(m, session.Id, "  pwd; echo \"$DEPLOY_TOKEN in $AWS_REGION\"; exit 3  ")).EnsureSuccessStatusCode();
        var (command, output, data) = await OutputAsync(m, session.Id);
        Assert.Equal("pwd; echo \"$DEPLOY_TOKEN in $AWS_REGION\"; exit 3", command.Text);
        Assert.Null(command.Ref); // the output streams under its ref, which a page drops once an item has it
        Assert.Contains(new DirectoryInfo(m.Work).Name, output.Text);
        Assert.Contains("*** in eu-west", output.Text);
        Assert.DoesNotContain("[Exit code", output.Text); // the item says how it ended
        Assert.True(output.IsError);
        Assert.Equal((Shell.Done, 3, m.Work), (data.State, data.ExitCode, data.Folder));
        Assert.Equal(command.Text, data.Command);

        (await RunAsync(m, session.Id, "true")).EnsureSuccessStatusCode();
        var (_, quiet, quietData) = await OutputAsync(m, session.Id, 1);
        Assert.Equal(("", false, 0), (quiet.Text, quiet.IsError, quietData.ExitCode));

        var audit = await m.H.Boris.GetStringAsync("/api/v1/audit");
        Assert.Contains("session.shell_command", audit);
        Assert.DoesNotContain("deploy-token-91", audit);
        Assert.DoesNotContain("deploy-token-91", await m.H.Boris.GetStringAsync($"/api/v1/sessions/{session.Id}/items"));
    }

    [Fact]
    public async Task OnlyThePersonWhoMayWriteRunsOneAndOnlyOnAMachine()
    {
        await using var m = await Machine.StartAsync();
        var session = await IdleAgentSessionAsync(m);
        using var alice = m.H.Server.Client();
        (await alice.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("alice@example.com", "Alice"))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await RunAsync(m, session.Id, "echo hi", alice)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await RunAsync(m, session.Id, "   ")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await RunAsync(m, session.Id, new string('x', Shell.MaximumCommandLength + 1))).StatusCode);

        m.H.Backend.Answer("No machine here.");
        var nowhere = await m.H.StartSessionAsync(profile: null);
        Assert.Equal(HttpStatusCode.BadRequest, (await RunAsync(m, nowhere.Id, "echo hi")).StatusCode);
    }

    [Fact]
    public async Task CommandsRunBesideEachOtherUpToTheLimitAndStop()
    {
        await using var m = await Machine.StartAsync();
        var session = await IdleAgentSessionAsync(m);
        for (var i = 0; i < Shell.MaximumRunning; i++) (await RunAsync(m, session.Id, $"echo started {i}; {Sleep(60)}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await RunAsync(m, session.Id, "echo one too many")).StatusCode);

        var items = await m.H.AllItemsAsync(session.Id);
        var first = items.First(item => item.Kind == SessionItemKind.ShellCommand);
        (await m.H.Boris.PostAsync($"/api/v1/sessions/{session.Id}/shell-commands/{first.Seq}/stop", null)).EnsureSuccessStatusCode();
        var (command, output, data) = await OutputAsync(m, session.Id);
        Assert.Equal((first.Seq, Shell.Stopped, (int?)null), (command.Seq, data.State, data.ExitCode));
        Assert.True(output.IsError);
        Assert.Equal("started 0", output.Text.Trim()); // what it printed before it stopped
        // Its place is free again; stopping the session stops the rest.
        (await RunAsync(m, session.Id, "echo again")).EnsureSuccessStatusCode();
        (await m.H.Boris.PostAsync($"/api/v1/sessions/{session.Id}/stop", null)).EnsureSuccessStatusCode();
        var all = await AgentSessionTest.Eventually(() => m.H.AllItemsAsync(session.Id),
            found => found.Count(item => item.Kind == SessionItemKind.ShellOutput) == Shell.MaximumRunning + 1);
        Assert.Equal(Shell.MaximumRunning + 1, all.Count(item => item.Kind == SessionItemKind.ShellCommand));
    }

    [Fact]
    public async Task AnOfflineMachineSaysTheCommandDidNotRun()
    {
        await using var m = await Machine.StartAsync();
        var session = await IdleAgentSessionAsync(m);
        await m.DisconnectAsync();
        await AgentSessionTest.Eventually(() => m.H.Boris.GetFromJsonAsync<MachineDto[]>("/api/v1/machines")!, found => !found!.Single(machine => machine.Id == m.Id).Online);
        (await RunAsync(m, session.Id, "echo hi")).EnsureSuccessStatusCode();
        var (_, output, data) = await OutputAsync(m, session.Id);
        Assert.Equal(Shell.Failed, data.State);
        Assert.Contains("did not run", output.Text);
    }

    [Fact]
    public async Task OutputsGoWithTheNextMessageOnlyWhenNamed()
    {
        await using var m = await Machine.StartAsync();
        var session = await IdleAgentSessionAsync(m);
        (await RunAsync(m, session.Id, "echo periwinkle")).EnsureSuccessStatusCode();
        var (command, output, _) = await OutputAsync(m, session.Id);
        // Only the outputs of this session's commands: a command's own item, or one named twice, is refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await m.H.Boris.PostAsJsonAsync($"/api/v1/sessions/{session.Id}/messages",
            new SendMessageRequest("What did it print?", ShellOutputs: [command.Seq]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await m.H.Boris.PostAsJsonAsync($"/api/v1/sessions/{session.Id}/messages",
            new SendMessageRequest("What did it print?", ShellOutputs: [output.Seq, output.Seq]))).StatusCode);

        m.H.Backend.Answer("It printed periwinkle.");
        (await m.H.Boris.PostAsJsonAsync($"/api/v1/sessions/{session.Id}/messages",
            new SendMessageRequest("What did it print?", ShellOutputs: [output.Seq]))).EnsureSuccessStatusCode();
        var items = await m.H.RunEndedAsync(session.Id, 2);
        var asked = items.Last(item => item.Kind == SessionItemKind.User);
        Assert.Equal(("What did it print?", $"shell:{output.Seq}"), (asked.Text, asked.Ref));
        var request = m.H.Backend.ModelRequests.Last().ToJsonString();
        Assert.Contains("The user ran this command in a shell", request);
        Assert.Contains("$ echo periwinkle", request);
        Assert.Contains("exited with code 0", request);

        // A message that names none carries none.
        m.H.Backend.Answer("Fine.");
        await m.H.SendAsync(session.Id, "And now?");
        await m.H.RunEndedAsync(session.Id, 3);
        var last = AgentSessionTest.OfType(m.H.Backend.ModelRequests.Last(), "message").Last().ToJsonString();
        Assert.Contains("And now?", last);
        Assert.DoesNotContain("The user ran this command", last);
    }

    [Fact]
    public async Task AHarnessSessionRunsCommandsInItsFolder()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var m = await Machine.StartAsync(replayFixture: SessionTest.Fixture);
        var response = await m.H.Boris.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest(m.Id, Harnesses.Replay, m.Work, "Write the file.", null, null));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var session = (await response.Content.ReadFromJsonAsync<SessionDto>())!;
        (await RunAsync(m, session.Id, "pwd")).EnsureSuccessStatusCode();
        var (_, output, data) = await OutputAsync(m, session.Id);
        Assert.Equal((Shell.Done, 0), (data.State, data.ExitCode));
        Assert.Contains(new DirectoryInfo(m.Work).Name, output.Text);
    }

    [Fact]
    public void TheModelReadsWhereEachCommandRanAndHowItEnded()
    {
        var done = new ShellOutputData("ls", "/w", Shell.Done, 0, 5);
        var stopped = new ShellOutputData("sleep 9", "/w", Shell.Stopped, null, 5);
        var text = Shell.Prepend("Look.", [(done, "a\nb\n"), (stopped, "")], 100_000);
        Assert.Equal("""
            The user ran this command in a shell, in /w; it exited with code 0.

            $ ls
            a
            b

            The user ran this command in a shell, in /w; the user stopped it before it ended.

            $ sleep 9
            (no output)

            Look.
            """.ReplaceLineEndings("\n"), text);
        // Outputs that do not fit are shortened in their middle, keeping both ends.
        var long_ = Shell.Prepend("Look.", [(done, "FIRST" + new string('x', 50_000) + "LAST")], 2_000);
        Assert.True(long_.Length <= 2_000, long_.Length.ToString());
        Assert.Contains("FIRST", long_);
        Assert.Contains("LAST", long_);
        Assert.Contains("characters cut", long_);
        Assert.Equal("Look.", Shell.Prepend("Look.", [], 10));
    }
}
