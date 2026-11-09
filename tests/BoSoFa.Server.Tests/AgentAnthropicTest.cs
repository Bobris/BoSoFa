using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Tests;

/// <summary>Whole agent sessions on an Anthropic API key, against a fake Messages API (docs/server-agent.md §5.4).</summary>
public class AgentAnthropicTest
{
    const string Key = "sk-ant-api03-testtesttesttesttesttesttest", Model = "claude-opus-5-5", McpTool = "mcp__facts__lookup";

    static async Task UseAnthropicAsync(AgentSessionTest.Harness h)
    {
        var account = await (await h.Boris.PostAsJsonAsync("/api/v1/accounts", new CreateHarnessAccountRequest(AccountKind.AnthropicApiKey, "My Claude API", Key)))
            .Content.ReadFromJsonAsync<HarnessAccountDto>();
        Assert.True(account!.ServesAgent);
        h.AccountId = account.Id;
    }

    static async Task<AgentSessionTest.Harness> StartAsync()
    {
        var h = await AgentSessionTest.Harness.StartAsync();
        await UseAnthropicAsync(h);
        return h;
    }

    static JsonArray Messages(JsonObject request) => request["messages"]!.AsArray();

    [Fact]
    public async Task ASessionRunsAToolAndSendsItsThinkingBack()
    {
        await using var h = await StartAsync();
        h.Backend.AnthropicThinkAndCall("I should look it up.", "sig-1", "toolu_1", McpTool, "{\"q\":\"life\"}");
        h.Backend.AnthropicAnswer("It is 42.");
        var session = await h.StartSessionAsync(model: Model);

        var request = await h.RequestAsync(session.Id);
        Assert.Equal(McpTool, request.ToolName);
        await h.AnswerAsync(session.Id, request.Id, true);

        var items = await h.RunEndedAsync(session.Id);
        Assert.Equal([SessionItemKind.User, SessionItemKind.Thinking, SessionItemKind.ToolUse, SessionItemKind.ToolResult, SessionItemKind.Assistant, SessionItemKind.RunEnded],
            items.Select(item => item.Kind));
        Assert.Equal("I should look it up.", items[1].Text);
        Assert.Equal("{\"q\":\"life\"}", items[2].Data);
        Assert.Equal("It is 42.", items[4].Text);
        Assert.Contains("\"inputTokens\":28", items[5].Data); // 14 each, cached tokens included
        Assert.Equal(["{\"q\":\"life\"}"], h.Backend.ToolCalls);
        Assert.All(h.Backend.ModelAuthorizations, key => Assert.Equal(Key, key));

        var first = h.Backend.ModelRequests.First();
        Assert.Equal(Model, (string)first["model"]!);
        Assert.Equal(AnthropicDefaults.Streaming, (int)first["max_tokens"]!);
        Assert.Equal(("adaptive", "summarized"), ((string)first["thinking"]!["type"]!, (string)first["thinking"]!["display"]!));
        var tools = first["tools"]!.AsArray();
        Assert.All(tools, tool => Assert.True((bool)tool!["eager_input_streaming"]!));
        Assert.Equal("ephemeral", (string)tools[^1]!["cache_control"]!["type"]!);
        Assert.Equal("ephemeral", (string)first["system"]![0]!["cache_control"]!["type"]!);

        var second = Messages(h.Backend.ModelRequests.ElementAt(1));
        Assert.Equal(["user", "assistant", "user"], second.Select(message => (string?)message!["role"]));
        var assistant = second[1]!["content"]!.AsArray();
        Assert.Equal(["thinking", "tool_use"], assistant.Select(block => (string?)block!["type"]));
        Assert.Equal(("I should look it up.", "sig-1"), ((string)assistant[0]!["thinking"]!, (string)assistant[0]!["signature"]!));
        Assert.Equal("life", (string)assistant[1]!["input"]!["q"]!);
        var result = second[2]!["content"]![0]!;
        Assert.Equal(("toolu_1", "The answer is 42.", "ephemeral"), ((string)result["tool_use_id"]!, (string)result["content"]!, (string)result["cache_control"]!["type"]!));
    }

    [Fact]
    public async Task ARefusedSignatureIsAskedAgainWithoutThinking()
    {
        await using var h = await StartAsync();
        h.Backend.AnthropicThinkAndCall("Thinking.", "sig-1", "toolu_1", AgentToolNames.Notify, "{\"message\":\"Working.\"}");
        h.Backend.AnthropicFail(400, "invalid_request_error", "messages.1.content.0: Invalid `signature` in `thinking` block. The block is bound to a different conversation.");
        h.Backend.AnthropicThinkAndCall("More.", "sig-2", "toolu_2", AgentToolNames.Notify, "{\"message\":\"Still.\"}");
        h.Backend.AnthropicAnswer("Done.");
        var session = await h.StartSessionAsync(profile: null, model: Model);

        var items = await h.RunEndedAsync(session.Id);
        Assert.Equal("completed", items[^1].Text);
        var requests = h.Backend.ModelRequests.ToArray();
        Assert.Equal(4, requests.Length);
        Assert.Contains("thinking", Types(requests[1]));
        Assert.DoesNotContain("thinking", Types(requests[2]));
        Assert.DoesNotContain("thinking", Types(requests[3])); // it stays off for this loop

        static IEnumerable<string?> Types(JsonObject request) => Messages(request).SelectMany(message => message!["content"]!.AsArray()).Select(block => (string?)block!["type"]);
    }

    [Fact]
    public async Task ARefusalIsShownWithItsReason()
    {
        await using var h = await StartAsync();
        h.Backend.AnthropicRefuse();
        var session = await h.StartSessionAsync(profile: null, model: Model);
        var items = await h.RunEndedAsync(session.Id);
        Assert.Equal("failed", items[^1].Text);
        Assert.Contains(items, item => item.Kind == SessionItemKind.Notice && item.Text == "The provider refused to answer: It looks like an attack.");
    }

    [Fact]
    public async Task AToolCallCutOffByTheOutputLimitDoesNotRun()
    {
        await using var h = await StartAsync();
        h.Backend.AnthropicCutOffCall("toolu_1", McpTool, "{\"q\":\"li");
        var session = await h.StartSessionAsync(mode: "bypassPermissions", model: Model);
        var items = await h.RunEndedAsync(session.Id);
        Assert.DoesNotContain(items, item => item.Kind == SessionItemKind.ToolUse);
        Assert.Empty(h.Backend.ToolCalls);
        Assert.Contains(items, item => item.Kind == SessionItemKind.Notice && item.Text.Contains("output limit"));
    }

    [Fact]
    public async Task AWrongKeyIsReportedAndEndsTheRun()
    {
        await using var h = await StartAsync();
        h.Backend.AnthropicFail(401, "authentication_error", "invalid x-api-key");
        var session = await h.StartSessionAsync(profile: null, model: Model);
        var items = await h.RunEndedAsync(session.Id);
        Assert.Equal("failed", items[^1].Text);
        Assert.Contains(items, item => item.Kind == SessionItemKind.Notice && item.Text.Contains("invalid x-api-key"));
    }

    static class AnthropicDefaults
    {
        public const int Streaming = 64000;
    }

    static class AgentToolNames
    {
        public const string Notify = "notify_user";
    }
}
