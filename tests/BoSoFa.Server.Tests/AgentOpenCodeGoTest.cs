using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Tests;

/// <summary>Agent sessions on an OpenCode Go key, against a fake service (docs/server-agent.md §5.3).</summary>
public class AgentOpenCodeGoTest
{
    const string Key = "go-testtesttesttesttesttesttest";

    static async Task<HarnessAccountDto> AddAccountAsync(AgentSessionTest.Harness h)
    {
        var response = await h.Boris.PostAsJsonAsync("/api/v1/accounts", new CreateHarnessAccountRequest(AccountKind.OpenCodeGoApiKey, "My Go", Key));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var account = (await response.Content.ReadFromJsonAsync<HarnessAccountDto>())!;
        h.AccountId = account.Id;
        return account;
    }

    [Fact]
    public async Task ATranslatedModelGoesToResponsesWithItsSessionHeader()
    {
        await using var h = await AgentSessionTest.Harness.StartAsync();
        var account = await AddAccountAsync(h);
        Assert.True(account.ServesAgent);
        Assert.Equal(Harnesses.Agent, account.Harness);
        h.Backend.Answer("Hello.");
        var session = await h.StartSessionAsync(profile: null, model: "glm-5.3");
        var items = await h.RunEndedAsync(session.Id);
        Assert.Equal("completed", items[^1].Text);
        Assert.Equal(["/go/openai/v1/responses"], h.Backend.ModelPaths);
        Assert.Equal([session.Id.ToString()], h.Backend.GoSessions);
        Assert.Equal("Bearer " + Key, h.Backend.ModelAuthorizations.Single());
        Assert.Null(h.Backend.ModelRequests.Single()["include"]); // only models that speak Responses themselves have it
    }

    [Fact]
    public async Task AQwenModelGoesToMessages()
    {
        await using var h = await AgentSessionTest.Harness.StartAsync();
        await AddAccountAsync(h);
        h.Backend.AnthropicAnswer("Hello.");
        var session = await h.StartSessionAsync(profile: null, model: "qwen3.8-max");
        Assert.Equal("completed", (await h.RunEndedAsync(session.Id))[^1].Text);
        Assert.Equal(["/go/anthropic/v1/messages"], h.Backend.ModelPaths);
        Assert.Equal([session.Id.ToString()], h.Backend.GoSessions);
        Assert.Equal(Key, h.Backend.ModelAuthorizations.Single());
        var request = h.Backend.ModelRequests.Single();
        Assert.Equal(16384, (int)request["max_tokens"]!);
        Assert.Null(request["thinking"]); // not a Claude model, and nobody asked
        Assert.All(request["tools"]!.AsArray(), tool => Assert.Null(tool!["eager_input_streaming"]));
    }

    [Fact]
    public async Task ANativeResponsesModelKeepsItsEncryptedReasoning()
    {
        await using var h = await AgentSessionTest.Harness.StartAsync();
        await AddAccountAsync(h);
        h.Backend.Answer("Hello.");
        var session = await h.StartSessionAsync(profile: null, model: "gpt-6-luna");
        await h.RunEndedAsync(session.Id);
        Assert.Equal("reasoning.encrypted_content", (string)h.Backend.ModelRequests.Single()["include"]![0]!);
    }

    [Fact]
    public async Task TheFormListsChatModelsWithTools()
    {
        await using var h = await AgentSessionTest.Harness.StartAsync();
        var account = await AddAccountAsync(h);
        var models = await h.Boris.GetFromJsonAsync<AgentModelDto[]>($"/api/v1/accounts/{account.Id}/models");
        Assert.Equal([new("glm-5.3", "GLM-5.3", 1000000, 384000), new("qwen3.8-max", "Qwen3.8 Max", 1000000, 384000)], models!);
        Assert.Equal("", h.Backend.GoModelListAuthorizations.Single()); // the list is public, so the key stays home

        using var other = h.Server.Client();
        (await other.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("eve@example.com", "Eve"))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/accounts/{account.Id}/models")).StatusCode);
    }

    [Fact]
    public async Task AGoKeyServesOnlyTheServerAgent()
    {
        await using var h = await AgentSessionTest.Harness.StartAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Boris.PostAsJsonAsync("/api/v1/accounts", new CreateHarnessAccountRequest(AccountKind.OpenCodeGoApiKey, "Short", "abc"))).StatusCode);
        var account = await AddAccountAsync(h);
        var state = await SessionTest.EnrollAsync(h.Server.BaseAddress, h.Boris, Directory.CreateTempSubdirectory("bosofa-go-").FullName);
        var codex = await h.Boris.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest(state.MachineId, Harnesses.Codex, "/tmp", "x", null, null, account.Id));
        Assert.False(codex.IsSuccessStatusCode);
    }

    [Fact]
    public async Task AReusedCallIdStillNeedsItsOwnApproval()
    {
        await using var h = await AgentSessionTest.Harness.StartAsync();
        await AddAccountAsync(h);
        const string tool = "mcp__facts__lookup";
        h.Backend.Call("call_0", tool, "{\"q\":\"first\"}");
        h.Backend.Call("call_0", tool, "{\"q\":\"second\"}");
        h.Backend.Answer("Done.");
        var session = await h.StartSessionAsync(model: "glm-5.3");

        var first = await h.RequestAsync(session.Id);
        Assert.Equal("call_0", first.ToolUseId);
        await h.AnswerAsync(session.Id, first.Id, true);

        var second = await AgentSessionTest.Eventually(() => h.RequestAsync(session.Id), request => request.Id != first.Id);
        Assert.Contains("second", second.Input);
        Assert.Equal(["{\"q\":\"first\"}"], h.Backend.ToolCalls); // the old approval did not run it
        await h.AnswerAsync(session.Id, second.Id, false);

        var items = await h.RunEndedAsync(session.Id);
        Assert.Equal("completed", items[^1].Text);
        Assert.Equal(["{\"q\":\"first\"}"], h.Backend.ToolCalls);
    }
}
