using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BoSoFa.Daemon;
using BoSoFa.Daemon.Sessions;
using BoSoFa.Protocol;
using Xunit.Abstractions;
using Tool = BoSoFa.Protocol.MachineTools;

namespace BoSoFa.Server.Tests;

/// <summary>Server agent sessions whose tools run on a real daemon, against a fake model provider (docs/server-agent.md §6).</summary>
public class AgentMachineTest(ITestOutputHelper output)
{
    /// <summary>Uploads as the person, which puts the blob where the daemon's own upload would.</summary>
    sealed class TestUploader(HttpClient client) : IBlobUploader
    {
        public async Task<BlobRef> UploadAsync(string file, CancellationToken cancellation)
        {
            var response = await client.PostAsync("/api/v1/blobs", new ByteArrayContent(await File.ReadAllBytesAsync(file, cancellation)), cancellation);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<BlobRef>(cancellation))!;
        }
    }

    internal sealed class Machine : IAsyncDisposable
    {
        public AgentSessionTest.Harness H = null!;
        public string Root = null!, Work = null!;
        public ulong Id;
        DaemonState _state = null!;
        System.Security.Cryptography.ECDsa _key = null!;
        Outbox _outbox = null!;
        SessionHost _host = null!;
        CancellationTokenSource? _stop;
        Task? _link;
        readonly TaskCompletionSource _firstConnect = new(TaskCreationOptions.RunContinuationsAsynchronously);

        string DaemonDirectory => Path.Combine(Root, "daemon");

        public static async Task<Machine> StartAsync(bool connect = true, string? liveKey = null)
        {
            var machine = new Machine { H = await AgentSessionTest.Harness.StartAsync(liveKey), Root = Directory.CreateTempSubdirectory("bosofa-agent-").FullName };
            machine.Work = Directory.CreateDirectory(Path.Combine(machine.Root, "work")).FullName;
            machine._state = await SessionTest.EnrollAsync(machine.H.Server.BaseAddress, machine.H.Boris, machine.DaemonDirectory);
            machine.Id = machine._state.MachineId;
            machine._key = new DaemonStore(machine.DaemonDirectory).LoadOrCreateKey();
            machine._outbox = new Outbox(Path.Combine(machine.DaemonDirectory, "outbox"));
            machine._host = new SessionHost(machine.DaemonDirectory, machine._outbox, new(Uploader: new TestUploader(machine.H.Boris)), TextWriter.Null);
            if (connect) await machine.ConnectAsync();
            return machine;
        }

        public async Task ConnectAsync()
        {
            _stop = new();
            var link = new DaemonLink(_state, _key, TextWriter.Null, _outbox, _host);
            var welcomed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            link.Connected += _ => welcomed.TrySetResult();
            _link = link.RunAsync(_stop.Token);
            await welcomed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await AgentSessionTest.Eventually(() => H.Boris.GetFromJsonAsync<MachineDto[]>("/api/v1/machines")!, found => found!.Single(machine => machine.Id == Id).Online);
        }

        public async Task DisconnectAsync()
        {
            await _stop!.CancelAsync();
            await _link!;
            _stop.Dispose();
            _stop = null;
        }

        public async Task<SessionDto> StartSessionAsync(string prompt, string? mode = null, string? profile = null)
        {
            var response = await H.Boris.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest(Id, Harnesses.Agent, Work, prompt, mode, "test-model", H.AccountId, ProfileName: profile));
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<SessionDto>())!;
        }

        public string PathOf(string name) => Path.Combine(Work, name);

        public async ValueTask DisposeAsync()
        {
            if (_stop != null) await DisconnectAsync();
            await _host.DisposeAsync();
            _outbox.Dispose();
            _key.Dispose();
            await H.DisposeAsync();
            foreach (var file in new DirectoryInfo(Root).EnumerateFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal;
            try { Directory.Delete(Root, true); }
            catch (IOException) { }
        }
    }

    static string Sleep(int seconds) => OperatingSystem.IsWindows() ? $"Start-Sleep {seconds}" : $"sleep {seconds}";

    static string Args(params (string Name, object Value)[] arguments)
    {
        var map = new JsonObject();
        foreach (var (name, value) in arguments) map[name] = value is int number ? number : (string)value;
        return map.ToJsonString();
    }

    static void Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error.Result);
    }

    [Fact]
    public async Task TheAgentReadsAskingNobodyAndChangesThingsWithApproval()
    {
        await using var m = await Machine.StartAsync();
        Git(m.Work, "init", "-q", "-b", "main");
        await File.WriteAllTextAsync(m.PathOf("a.txt"), "one\n");
        Git(m.Work, "add", "-A");
        Git(m.Work, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "first");

        m.H.Backend.Call("c1", Tool.ReadFile, Args(("path", "a.txt")));
        m.H.Backend.Call("c2", Tool.EditFile, Args(("path", "a.txt"), ("old_text", "one"), ("new_text", "two")));
        m.H.Backend.Call("c3", Tool.Shell, Args(("command", "echo checked")));
        m.H.Backend.Answer("Done.");
        var session = await m.StartSessionAsync("Change one to two.");
        Assert.Equal(m.Id, session.MachineId);
        Assert.Equal(m.Work, session.WorkingDirectory);
        Assert.True(session.CanControl);

        var edit = await m.H.RequestAsync(session.Id);
        Assert.Equal(Tool.EditFile, edit.ToolName);
        Assert.Equal("a.txt", edit.Description);
        Assert.Equal("one\n", await File.ReadAllTextAsync(m.PathOf("a.txt")));
        await m.H.AnswerAsync(session.Id, edit.Id, true);

        var shell = await AgentSessionTest.Eventually(() => m.H.RequestAsync(session.Id), request => request.ToolName == Tool.Shell);
        Assert.Equal("echo checked", shell.Description);
        Assert.Equal("two\n", await File.ReadAllTextAsync(m.PathOf("a.txt")));
        await m.H.AnswerAsync(session.Id, shell.Id, true);

        var items = await m.H.RunEndedAsync(session.Id);
        Assert.Equal([
            SessionItemKind.User, SessionItemKind.ToolUse, SessionItemKind.ToolResult, SessionItemKind.ToolUse, SessionItemKind.ToolResult, SessionItemKind.ToolUse,
            SessionItemKind.ToolResult, SessionItemKind.Assistant, SessionItemKind.Diff, SessionItemKind.RunEnded
        ], items.Select(item => item.Kind));
        Assert.Equal("1\tone\n", items[2].Text);
        Assert.StartsWith("tool-", items[2].Ref);
        Assert.Equal("checked", items[6].Text.Trim());
        Assert.Contains("+two", items[8].Text);
        Assert.Contains("\"added\":1", items[8].Data);

        var tools = AgentSessionTest.Tools(m.H.Backend.ModelRequests.First());
        Assert.Contains(Tool.Shell, tools);
        Assert.Contains(Tool.ApplyPatch, tools);
        var instructions = AgentSessionTest.Input(m.H.Backend.ModelRequests.First())[0]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains(m.Work, instructions);
        Assert.DoesNotContain("no machine", instructions);
    }

    [Fact]
    public async Task PermissionModesDecideWhatIsAskedAndOffered()
    {
        await using var m = await Machine.StartAsync();
        m.H.Backend.Call("c1", Tool.WriteFile, Args(("path", "a.txt"), ("content", "x")));
        m.H.Backend.Call("c2", Tool.Shell, Args(("command", "echo hi")));
        m.H.Backend.Answer("Done.");
        var accept = await m.StartSessionAsync("Go.", "acceptEdits");
        var request = await m.H.RequestAsync(accept.Id); // the write went through; only the shell asks
        Assert.Equal(Tool.Shell, request.ToolName);
        Assert.Equal("x", await File.ReadAllTextAsync(m.PathOf("a.txt")));
        await m.H.AnswerAsync(accept.Id, request.Id, false, "Not now");
        var items = await m.H.RunEndedAsync(accept.Id);
        var denied = items.Last(item => item.Kind == SessionItemKind.ToolResult);
        Assert.True(denied.IsError);
        Assert.Contains("Not now", denied.Text);

        m.H.Backend.Call("c3", Tool.Shell, Args(("command", "echo free")));
        m.H.Backend.Answer("Done.");
        var bypass = await m.StartSessionAsync("Go.", "bypassPermissions");
        Assert.Equal("free", (await m.H.RunEndedAsync(bypass.Id)).Last(item => item.Kind == SessionItemKind.ToolResult).Text.Trim());

        m.H.Backend.Call("c4", Tool.WriteFile, Args(("path", "b.txt"), ("content", "x")));
        m.H.Backend.Answer("Planned.");
        var plan = await m.StartSessionAsync("Plan.", "plan");
        var result = (await m.H.RunEndedAsync(plan.Id)).Last(item => item.Kind == SessionItemKind.ToolResult);
        Assert.Contains("no tool named", result.Text);
        Assert.False(File.Exists(m.PathOf("b.txt")));
        var offered = AgentSessionTest.Tools(m.H.Backend.ModelRequests.Last());
        Assert.Contains(Tool.ReadFile, offered);
        Assert.DoesNotContain(Tool.WriteFile, offered);
        Assert.DoesNotContain(Tool.Shell, offered);
    }

    [Fact]
    public async Task AnOfflineMachineIsReportedAndNothingRunsLater()
    {
        await using var m = await Machine.StartAsync(connect: false);
        m.H.Backend.Call("c1", Tool.WriteFile, Args(("path", "a.txt"), ("content", "x")));
        m.H.Backend.Answer("It is offline.");
        var session = await m.StartSessionAsync("Go.", "bypassPermissions");
        var result = (await m.H.RunEndedAsync(session.Id)).First(item => item.Kind == SessionItemKind.ToolResult);
        Assert.True(result.IsError);
        Assert.Contains("offline", result.Text);
        await m.ConnectAsync();
        await Task.Delay(500);
        Assert.False(File.Exists(m.PathOf("a.txt")));
    }

    [Fact]
    public async Task AnInterruptStopsARunningCommand()
    {
        await using var m = await Machine.StartAsync();
        var marker = m.PathOf("finished.txt");
        var started = m.PathOf("started.txt");
        var command = OperatingSystem.IsWindows()
            ? $"Set-Content '{started}' x; {Sleep(4)}; Set-Content '{marker}' x"
            : $"touch '{started}'; {Sleep(4)}; touch '{marker}'";
        m.H.Backend.Call("c1", Tool.Shell, Args(("command", command)));
        var session = await m.StartSessionAsync("Go.", "bypassPermissions");
        await AgentSessionTest.Eventually(() => Task.FromResult(File.Exists(started)), exists => exists);
        (await m.H.Boris.PostAsync($"/api/v1/sessions/{session.Id}/interrupt", null)).EnsureSuccessStatusCode();
        var items = await m.H.RunEndedAsync(session.Id);
        Assert.Equal("interrupted", items[^1].Text);
        await Task.Delay(TimeSpan.FromSeconds(5));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task AToolSurvivesTheDaemonsConnectionDroppingAndRunsOnce()
    {
        await using var m = await Machine.StartAsync();
        var log = m.PathOf("log.txt");
        var command = OperatingSystem.IsWindows()
            ? $"Add-Content '{log}' x; {Sleep(3)}; Write-Output finished"
            : $"echo x >> '{log}'; {Sleep(3)}; echo finished";
        m.H.Backend.Call("c1", Tool.Shell, Args(("command", command)));
        m.H.Backend.Answer("Done.");
        var session = await m.StartSessionAsync("Go.", "bypassPermissions");
        await AgentSessionTest.Eventually(() => Task.FromResult(File.Exists(log)), exists => exists);
        await m.DisconnectAsync();
        await m.ConnectAsync();
        var result = (await m.H.RunEndedAsync(session.Id)).First(item => item.Kind == SessionItemKind.ToolResult);
        Assert.False(result.IsError, result.Text);
        Assert.Equal("finished", result.Text.Trim());
        Assert.Single(await File.ReadAllLinesAsync(log));
    }

    [Fact]
    public async Task UploadFileAsksAndGivesThePersonADownload()
    {
        await using var m = await Machine.StartAsync();
        await File.WriteAllTextAsync(m.PathOf("report.txt"), "the report");
        m.H.Backend.Call("c1", Tool.UploadFile, Args(("path", "report.txt")));
        m.H.Backend.Call("c2", Tool.UploadFile, Args(("path", "../outside.txt")));
        m.H.Backend.Answer("Done.");
        var session = await m.StartSessionAsync("Share it.");
        var request = await m.H.RequestAsync(session.Id);
        Assert.Equal((Tool.UploadFile, "report.txt"), (request.ToolName, request.Description));
        await m.H.AnswerAsync(session.Id, request.Id, true);
        var second = await AgentSessionTest.Eventually(() => m.H.RequestAsync(session.Id), next => next.Id != request.Id);
        await m.H.AnswerAsync(session.Id, second.Id, true);
        var items = await m.H.RunEndedAsync(session.Id);
        var notice = Assert.Single(items, item => item.Kind == SessionItemKind.Notice && item.Text.StartsWith("Uploaded", StringComparison.Ordinal));
        Assert.Contains("\"fileName\":\"report.txt\"", notice.Data);
        Assert.Contains(items, item => item is { Kind: SessionItemKind.ToolResult, IsError: true } && item.Text.Contains("outside"));

        m.H.Backend.Answer("Planned.");
        var plan = await m.StartSessionAsync("Plan.", "plan");
        await m.H.RunEndedAsync(plan.Id);
        Assert.DoesNotContain(Tool.UploadFile, AgentSessionTest.Tools(m.H.Backend.ModelRequests.Last()));
    }

    [Fact]
    public async Task ReadingAnImageShowsItToTheModel()
    {
        await using var m = await Machine.StartAsync();
        await File.WriteAllBytesAsync(m.PathOf("shot.png"), Convert.FromBase64String(FakeAgentBackend.RedPng));
        m.H.Backend.Call("c1", Tool.ReadFile, Args(("path", "shot.png")));
        m.H.Backend.Answer("It is red.");
        var session = await m.StartSessionAsync("What colour is shot.png?");
        var result = (await m.H.RunEndedAsync(session.Id)).Single(item => item.Kind == SessionItemKind.ToolResult);
        Assert.Contains("image/png", result.Text);
        Assert.Contains("\"mediaType\":\"image/png\"", result.Data);

        var output = m.H.Backend.ModelRequests.Last()["input"]!.AsArray().Single(entry => (string?)entry!["type"] == "function_call_output")!["output"]!.AsArray();
        Assert.Equal(["input_text", "input_image"], output.Select(part => (string?)part!["type"]));
        Assert.Equal("data:image/png;base64," + FakeAgentBackend.RedPng, (string)output[1]!["image_url"]!);
    }

    [Fact]
    public async Task AStdioMcpServerRunsOnTheMachine()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var m = await Machine.StartAsync();
        var script = AgentToolHostTest.WriteFakeMcpServer(m.Root);
        (await m.H.Boris.PostAsJsonAsync("/api/v1/mcp-servers", new PublishMcpServer("local", McpTransport.Stdio, "/bin/bash", [script], null, new() { ["MCP_SECRET"] = "s3" }))).EnsureSuccessStatusCode();
        (await m.H.Boris.PostAsJsonAsync("/api/v1/profiles", new ProfileDto("onmachine", null, null, ["local", "facts"], []))).EnsureSuccessStatusCode();
        m.H.Backend.Call("c1", "mcp__local__where", "{}");
        m.H.Backend.Answer("Done.");
        var session = await m.StartSessionAsync("Where?", "bypassPermissions", "onmachine");
        var result = (await m.H.RunEndedAsync(session.Id)).First(item => item.Kind == SessionItemKind.ToolResult);
        Assert.False(result.IsError, result.Text);
        Assert.Equal("in work with s3", result.Text);
        var offered = AgentSessionTest.Tools(m.H.Backend.ModelRequests.First());
        Assert.Contains("mcp__local__where", offered);
        Assert.Contains("mcp__facts__lookup", offered);

        var refused = await m.H.Boris.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest(0, Harnesses.Agent, "", "x", null, "test-model", m.H.AccountId, ProfileName: "onmachine"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task CreatingASessionChecksTheMachine()
    {
        await using var m = await Machine.StartAsync(connect: false);
        Task<HttpResponseMessage> Create(HttpClient client, ulong machine, string directory, bool worktree = false) =>
            client.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest(machine, Harnesses.Agent, directory, "x", null, "test-model", m.H.AccountId, UseWorktree: worktree));
        Assert.Equal(HttpStatusCode.BadRequest, (await Create(m.H.Boris, m.Id, " ")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Create(m.H.Boris, m.Id, m.Work, worktree: true)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Create(m.H.Boris, 999_999, m.Work)).StatusCode);
    }

    /// <summary>The goal of the machine tools step, on the real API: with approvals on, a model finds and fixes the cause of a failing test.</summary>
    [ProviderTest.LiveOpenAIFact]
    public async Task LiveOpenAIFixesAFailingTest()
    {
        if (OperatingSystem.IsWindows()) return;
        var key = (await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("BOSOFA_TEST_OPENAI_KEY_FILE")!)).Trim();
        var model = Environment.GetEnvironmentVariable("BOSOFA_TEST_OPENAI_MODEL") is { Length: > 0 } configured ? configured : "gpt-5.4-mini";
        await using var m = await Machine.StartAsync(liveKey: key);
        await FixesAFailingTestAsync(m, model);
    }

    [ProviderTest.LiveAnthropicFact]
    public async Task LiveAnthropicFixesAFailingTest()
    {
        if (OperatingSystem.IsWindows()) return;
        var key = (await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("BOSOFA_TEST_ANTHROPIC_KEY_FILE")!)).Trim();
        var model = Environment.GetEnvironmentVariable("BOSOFA_TEST_ANTHROPIC_MODEL") is { Length: > 0 } configured ? configured : "claude-opus-5-5";
        await using var m = await Machine.StartAsync(liveKey: "sk-notusednotusednotusednotused"); // only so that the real endpoints are used
        var response = await m.H.Boris.PostAsJsonAsync("/api/v1/accounts", new CreateHarnessAccountRequest(AccountKind.AnthropicApiKey, "My Claude API", key));
        Assert.True(response.IsSuccessStatusCode, $"The key was refused: {(int)response.StatusCode}");
        m.H.AccountId = (await response.Content.ReadFromJsonAsync<HarnessAccountDto>())!.Id;
        await FixesAFailingTestAsync(m, model);
    }

    async Task FixesAFailingTestAsync(Machine m, string model)
    {
        Git(m.Work, "init", "-q", "-b", "main");
        await File.WriteAllTextAsync(m.PathOf("add.sh"), "add() {\n  echo $(($1 - $2))\n}\n");
        await File.WriteAllTextAsync(m.PathOf("test.sh"), "source ./add.sh\n[ \"$(add 2 3)\" = 5 ] && echo PASS || { echo \"FAIL: expected 5, got $(add 2 3)\"; exit 1; }\n");
        Git(m.Work, "add", "-A");
        Git(m.Work, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "first");

        var response = await m.H.Boris.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest(m.Id, Harnesses.Agent, m.Work,
            "The test in test.sh fails (run it with: bash test.sh). Find the cause, fix it, and run the test again to confirm.", null, model, m.H.AccountId));
        response.EnsureSuccessStatusCode();
        var session = (await response.Content.ReadFromJsonAsync<SessionDto>())!;

        var approved = new List<string>();
        var deadline = DateTime.UtcNow.AddMinutes(4);
        SessionItemDto[] items;
        while (true)
        {
            items = await m.H.ItemsAsync(session.Id);
            if (items.Any(item => item.Kind == SessionItemKind.RunEnded)) break;
            Assert.True(DateTime.UtcNow < deadline, "The agent did not finish.");
            foreach (var request in (await m.H.Boris.GetFromJsonAsync<RuntimeRequestDto[]>($"/api/v1/sessions/{session.Id}/requests"))!)
            {
                approved.Add(request.ToolName);
                await m.H.AnswerAsync(session.Id, request.Id, true);
            }
            await Task.Delay(100);
        }
        output.WriteLine($"{model}: {string.Join(' ', items.Select(item => item.Kind))}; approved {string.Join(' ', approved)}; {items[^1].Data}");
        Assert.Equal("completed", items[^1].Text);
        Assert.Contains(Tool.Shell, approved);
        Assert.Contains("+  echo $(($1 + $2))", Assert.Single(items, item => item.Kind == SessionItemKind.Diff).Text.Replace("\r", ""));
        Assert.Contains("$1 + $2", await File.ReadAllTextAsync(m.PathOf("add.sh")));
    }

    [Fact]
    public async Task ApplyPatchIsOfferedOnlyToModelsTrainedOnIt()
    {
        await using var m = await Machine.StartAsync();
        var account = await (await m.H.Boris.PostAsJsonAsync("/api/v1/accounts", new CreateHarnessAccountRequest(AccountKind.AnthropicApiKey, "My Claude API", "sk-ant-api03-testtesttesttesttesttesttest")))
            .Content.ReadFromJsonAsync<HarnessAccountDto>();
        m.H.AccountId = account!.Id;
        m.H.Backend.AnthropicAnswer("Done.");
        var response = await m.H.Boris.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest(m.Id, Harnesses.Agent, m.Work, "Go.", null, "claude-opus-5-5", m.H.AccountId));
        response.EnsureSuccessStatusCode();
        await m.H.RunEndedAsync((await response.Content.ReadFromJsonAsync<SessionDto>())!.Id);
        var offered = AgentSessionTest.Tools(m.H.Backend.ModelRequests.First());
        Assert.Contains(Tool.EditFile, offered);
        Assert.DoesNotContain(Tool.ApplyPatch, offered);
    }

    [Fact]
    public async Task ANoMachineSessionStillGetsNoMachineTools()
    {
        await using var h = await AgentSessionTest.Harness.StartAsync();
        h.Backend.Call("c1", Tool.Shell, Args(("command", "echo hi")));
        h.Backend.Answer("No shell here.");
        var session = await h.StartSessionAsync(profile: null);
        Assert.Contains("no tool named", (await h.RunEndedAsync(session.Id)).First(item => item.Kind == SessionItemKind.ToolResult).Text);
        Assert.DoesNotContain(Tool.ReadFile, AgentSessionTest.Tools(h.Backend.ModelRequests.First()));
    }
}
