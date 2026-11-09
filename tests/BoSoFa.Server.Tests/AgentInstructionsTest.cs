using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BoSoFa.Protocol;
using Tool = BoSoFa.Protocol.MachineTools;

namespace BoSoFa.Server.Tests;

/// <summary>What a server agent session's model is told, and which tools it gets (docs/server-agent.md §7).</summary>
public class AgentInstructionsTest
{
    static async Task SaveProfileAsync(HttpClient client, ProfileDto profile)
    {
        var response = await client.PostAsJsonAsync("/api/v1/profiles", profile);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    static async Task<SessionDto> StartAsync(AgentMachineTest.Machine m, string? systemPrompt = null, string? effort = null, string? group = null, string? profile = null, string? mode = null)
    {
        var response = await m.H.Boris.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest(m.Id, Harnesses.Agent, m.Work, "Go.", mode, "test-model", m.H.AccountId,
            ProfileName: profile, SystemPrompt: systemPrompt, ReasoningEffort: effort, ToolGroup: group));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<SessionDto>())!;
    }

    static string Shared(JsonObject request) => request["instructions"]!.GetValue<string>();
    static string OwnInstructions(JsonObject request) => AgentSessionTest.Input(request)[0]!["content"]![0]!["text"]!.GetValue<string>();

    [Fact]
    public async Task TheLayersComeInOrderAndTheSharedPartFirst()
    {
        await using var m = await AgentMachineTest.Machine.StartAsync();
        await File.WriteAllTextAsync(m.PathOf("AGENTS.md"), "Use tabs here.");
        await SaveProfileAsync(m.H.Boris, new("house", null, null, [], [], "Write in the house style.", "append"));
        m.H.Backend.Answer("Done.");
        var session = await StartAsync(m, "Be brief today.", "high", profile: "house");
        Assert.Equal("high", session.ReasoningEffort);
        await m.H.RunEndedAsync(session.Id);

        var request = m.H.Backend.ModelRequests.Single();
        var shared = Shared(request);
        Assert.True(shared.IndexOf("You are an agent", StringComparison.Ordinal) < shared.IndexOf("Write in the house style.", StringComparison.Ordinal));
        Assert.DoesNotContain(m.Work, shared); // nothing of this session in what many sessions share
        var own = OwnInstructions(request);
        Assert.Contains($"Today is {DateTime.UtcNow:yyyy-MM-dd}", own);
        var (where, project, prompt) = (own.IndexOf(m.Work, StringComparison.Ordinal), own.IndexOf("Use tabs here.", StringComparison.Ordinal), own.IndexOf("Be brief today.", StringComparison.Ordinal));
        Assert.True(where >= 0 && where < project && project < prompt, own);
        Assert.Contains("AGENTS.md", own);
        Assert.Equal("high", (string)request["reasoning"]!["effort"]!);

        var recorded = Assert.Single(await m.H.AllItemsAsync(session.Id), item => item.Kind == SessionItemKind.Instructions);
        Assert.Contains("Write in the house style.", recorded.Text);
        Assert.Contains("Be brief today.", recorded.Text);
    }

    [Fact]
    public async Task AProfileCanReplaceTheBuiltInPrompt()
    {
        await using var m = await AgentMachineTest.Machine.StartAsync();
        await SaveProfileAsync(m.H.Boris, new("own", null, null, [], [], "You are a reviewer.", "replace"));
        m.H.Backend.Answer("Done.");
        await m.H.RunEndedAsync((await StartAsync(m, profile: "own")).Id);
        Assert.Equal("You are a reviewer.", Shared(m.H.Backend.ModelRequests.Single()));
    }

    [Fact]
    public async Task ClaudeMdIsReadWhenThereIsNoAgentsMd()
    {
        await using var m = await AgentMachineTest.Machine.StartAsync();
        await File.WriteAllTextAsync(m.PathOf("CLAUDE.md"), "Claude rules.");
        m.H.Backend.Answer("Done.");
        await m.H.RunEndedAsync((await StartAsync(m)).Id);
        Assert.Contains("Claude rules.", OwnInstructions(m.H.Backend.ModelRequests.Single()));
    }

    /// <summary>Cached prefixes and signed thinking need every request of a conversation to send the same text.</summary>
    [Fact]
    public async Task TheInstructionsDoNotChangeDuringAConversation()
    {
        await using var m = await AgentMachineTest.Machine.StartAsync();
        await File.WriteAllTextAsync(m.PathOf("AGENTS.md"), "First version.");
        await SaveProfileAsync(m.H.Boris, new("house", null, null, [], [], "Profile first.", "append"));
        m.H.Backend.Call("c1", "ask_user", "{\"question\":\"Go on?\"}");
        m.H.Backend.Answer("One.");
        m.H.Backend.Answer("Two.");
        var session = await StartAsync(m, profile: "house");

        var request = await m.H.RequestAsync(session.Id);
        await File.WriteAllTextAsync(m.PathOf("AGENTS.md"), "Second version.");
        await SaveProfileAsync(m.H.Boris, new("house", null, null, [], [], "Profile second.", "append"));
        await m.H.AnswerAsync(session.Id, request.Id, true);
        await m.H.RunEndedAsync(session.Id);
        await m.H.SendAsync(session.Id, "And again.");
        await m.H.RunEndedAsync(session.Id, 2);

        var requests = m.H.Backend.ModelRequests.ToArray();
        Assert.Equal(3, requests.Length);
        Assert.All(requests, sent => Assert.Equal(Shared(requests[0]), Shared(sent)));
        Assert.All(requests, sent => Assert.Equal(OwnInstructions(requests[0]), OwnInstructions(sent)));
        Assert.Contains("First version.", OwnInstructions(requests[2]));
        Assert.Contains("Profile first.", Shared(requests[2]));
        Assert.Single(await m.H.AllItemsAsync(session.Id), item => item.Kind == SessionItemKind.Instructions);
    }

    [Fact]
    public async Task AnOfflineMachineLeavesANoticeAndTheSessionGoesOn()
    {
        await using var m = await AgentMachineTest.Machine.StartAsync(connect: false);
        m.H.Backend.Answer("Done.");
        var items = await m.H.RunEndedAsync((await StartAsync(m)).Id);
        Assert.Contains(items, item => item.Kind == SessionItemKind.Notice && item.Text.Contains("instructions could not be read"));
        Assert.Equal("completed", items[^1].Text);
    }

    [Fact]
    public async Task ThePromptReachesAnthropicAsTwoBlocksWithTheCachedOneFirst()
    {
        await using var m = await AgentMachineTest.Machine.StartAsync();
        var account = await (await m.H.Boris.PostAsJsonAsync("/api/v1/accounts", new CreateHarnessAccountRequest(AccountKind.AnthropicApiKey, "Claude", "sk-ant-api03-testtesttesttesttesttesttest")))
            .Content.ReadFromJsonAsync<HarnessAccountDto>();
        m.H.AccountId = account!.Id;
        m.H.Backend.AnthropicAnswer("Done.");
        var response = await m.H.Boris.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest(m.Id, Harnesses.Agent, m.Work, "Go.", null, "claude-opus-5-5", m.H.AccountId,
            SystemPrompt: "Be brief today.", ReasoningEffort: "xhigh"));
        response.EnsureSuccessStatusCode();
        await m.H.RunEndedAsync((await response.Content.ReadFromJsonAsync<SessionDto>())!.Id);
        var request = m.H.Backend.ModelRequests.Single();
        var system = request["system"]!.AsArray();
        Assert.NotNull(system[0]!["cache_control"]);
        Assert.Contains("You are an agent", (string)system[0]!["text"]!);
        Assert.Null(system[1]!["cache_control"]);
        Assert.Contains("Be brief today.", (string)system[1]!["text"]!);
        Assert.Equal("xhigh", (string)request["output_config"]!["effort"]!);
    }

    [Fact]
    public async Task TheToolGroupDecidesWhichMachineToolsExist()
    {
        await using var m = await AgentMachineTest.Machine.StartAsync();
        await SaveProfileAsync(m.H.Boris, new("readonly", null, null, [], [], MaxToolGroup: AgentToolGroups.Read));

        async Task<string[]> OfferedAsync(string? group, string? profile, string expected)
        {
            var before = m.H.Backend.ModelRequests.Count;
            m.H.Backend.Answer("Done.");
            var session = await StartAsync(m, group: group, profile: profile);
            Assert.Equal(expected, session.ToolGroup);
            await m.H.RunEndedAsync(session.Id);
            return AgentSessionTest.Tools(m.H.Backend.ModelRequests.ElementAt(before));
        }

        var full = await OfferedAsync(null, null, AgentToolGroups.Full);
        Assert.Contains(Tool.Shell, full);
        Assert.Contains(Tool.ReadFile, full);

        var read = await OfferedAsync(AgentToolGroups.Read, null, AgentToolGroups.Read);
        Assert.Contains(Tool.ReadFile, read);
        Assert.DoesNotContain(Tool.WriteFile, read);
        Assert.DoesNotContain(Tool.Shell, read);

        var narrowed = await OfferedAsync(AgentToolGroups.Full, "readonly", AgentToolGroups.Read); // asking for more than the profile allows
        Assert.DoesNotContain(Tool.Shell, narrowed);

        var none = await OfferedAsync(AgentToolGroups.None, null, AgentToolGroups.None);
        Assert.DoesNotContain(Tool.ReadFile, none);
        Assert.Contains("ask_user", none);

        m.H.Backend.Call("c1", Tool.Shell, "{\"command\":\"echo hi\"}");
        m.H.Backend.Answer("No shell.");
        var refused = await StartAsync(m, group: AgentToolGroups.Read, mode: "bypassPermissions");
        Assert.Contains("no tool named", (await m.H.RunEndedAsync(refused.Id)).First(item => item.Kind == SessionItemKind.ToolResult).Text);
    }

    [Fact]
    public async Task BadSettingsAreRefused()
    {
        await using var m = await AgentMachineTest.Machine.StartAsync(connect: false);
        Task<HttpResponseMessage> Create(string harness, string? prompt = null, string? effort = null, string? group = null) => m.H.Boris.PostAsJsonAsync("/api/v1/sessions",
            new CreateSessionRequest(m.Id, harness, m.Work, "Go.", null, "test-model", harness == Harnesses.Agent ? m.H.AccountId : null, SystemPrompt: prompt, ReasoningEffort: effort, ToolGroup: group));
        Assert.Equal(HttpStatusCode.BadRequest, (await Create(Harnesses.Agent, effort: "extreme")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Create(Harnesses.Agent, group: "some")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Create(Harnesses.Claude, prompt: "No.")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await m.H.Boris.PostAsJsonAsync("/api/v1/profiles", new ProfileDto("x", null, null, [], [], "p", "merge"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await m.H.Boris.PostAsJsonAsync("/api/v1/profiles", new ProfileDto("x", null, null, [], [], MaxToolGroup: "some"))).StatusCode);
    }

    [Fact]
    public async Task AProfileKeepsItsNewSettings()
    {
        await using var m = await AgentMachineTest.Machine.StartAsync(connect: false);
        await SaveProfileAsync(m.H.Boris, new("house", "m", null, [], [], "Prompt.", "replace", AgentToolGroups.None));
        var saved = (await m.H.Boris.GetFromJsonAsync<ProfileDto[]>("/api/v1/profiles"))!.Single(profile => profile.Name == "house");
        Assert.Equal(("Prompt.", "replace", AgentToolGroups.None), (saved.SystemPrompt, saved.SystemPromptMode, saved.MaxToolGroup));
    }
}
