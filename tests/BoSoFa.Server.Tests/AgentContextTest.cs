using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BoSoFa.Protocol;
using BoSoFa.Server.Agents;
using BoSoFa.Server.Domain;

namespace BoSoFa.Server.Tests;

/// <summary>Costs, compaction and web access of the server agent (docs/server-agent.md §6.3 and §8).</summary>
public class AgentContextTest
{
    const string Notify = "notify_user", Message = "{\"message\":\"Working.\"}";

    static async Task<double> SessionCostAsync(AgentSessionTest.Harness h, ulong id) => (await h.Boris.GetFromJsonAsync<SessionDto>($"/api/v1/sessions/{id}"))!.CostUsd;

    static double RunCost(SessionItemDto item) => (double)JsonNode.Parse(item.Data!)!["costUsd"]!;

    [Fact]
    public async Task EveryRunAndTheSessionShowWhatTheyCost()
    {
        await using var h = await AgentSessionTest.Harness.StartAsync();
        h.Backend.Call("c1", Notify, Message);
        h.Backend.Answer("One.");
        var session = await h.StartSessionAsync(profile: null, model: "gpt-5.5");
        var items = await h.RunEndedAsync(session.Id);
        var expected = 2 * (10 * 5e-6 + 5 * 30e-6); // two calls at this model's prices
        Assert.Equal(expected, RunCost(items[^1]), 9);
        Assert.Equal(expected, await SessionCostAsync(h, session.Id), 9);

        h.Backend.Answer("Two.");
        await h.SendAsync(session.Id, "Again.");
        items = await h.RunEndedAsync(session.Id, 2);
        Assert.Equal(10 * 5e-6 + 5 * 30e-6, RunCost(items[^1]), 9); // only this run
        Assert.Equal(expected * 1.5, await SessionCostAsync(h, session.Id), 9);
    }

    [Fact]
    public async Task AModelTheSnapshotDoesNotKnowStillRunsWithoutACost()
    {
        await using var h = await AgentSessionTest.Harness.StartAsync();
        h.Backend.Answer("Fine.");
        var session = await h.StartSessionAsync(profile: null, model: "a-model-from-the-future");
        var items = await h.RunEndedAsync(session.Id);
        Assert.Equal("completed", items[^1].Text);
        Assert.Equal(0, RunCost(items[^1]));
    }

    [Fact]
    public async Task ANearlyFullConversationIsSummarizedAndGoesOnFromTheSummary()
    {
        await using var h = await AgentSessionTest.Harness.StartAsync();
        h.Backend.Call("c1", Notify, Message, inputTokens: 900_000); // over 90% of this model's 922,000
        h.Backend.Answer("Here is the summary.");
        h.Backend.Answer("Finished.");
        var session = await h.StartSessionAsync(prompt: "Do the big thing.", profile: null, model: "gpt-5.5");
        var items = await h.RunEndedAsync(session.Id);
        Assert.Equal([SessionItemKind.User, SessionItemKind.ToolUse, SessionItemKind.Notice, SessionItemKind.ToolResult, SessionItemKind.Compaction, SessionItemKind.Assistant, SessionItemKind.RunEnded],
            items.Select(item => item.Kind));
        Assert.Equal("Here is the summary.", items[4].Text);
        Assert.Equal("Finished.", items[5].Text);

        var requests = h.Backend.ModelRequests.ToArray();
        Assert.Equal(3, requests.Length);
        Assert.Equal("none", (string)requests[1]["tool_choice"]!);
        Assert.Contains("summary", (string)AgentSessionTest.Input(requests[1])[^1]!["content"]![0]!["text"]!);
        Assert.Equal(AgentSessionTest.Tools(requests[0]), AgentSessionTest.Tools(requests[1])); // the same prefix, so the cache still serves it

        var after = requests[2];
        Assert.Null(after["tool_choice"]);
        Assert.Empty(AgentSessionTest.OfType(after, "function_call"));
        var texts = AgentSessionTest.OfType(after, "message").Select(message => (string)message["content"]![0]!["text"]!).ToArray();
        Assert.Contains(texts, text => text.Contains("Here is the summary."));
        Assert.Equal("Do the big thing.", texts[^1]); // the person's latest words stay as they were
    }

    [Fact]
    public async Task AnOverflowIsSummarizedOnceAndThenReported()
    {
        await using var h = await AgentSessionTest.Harness.StartAsync();
        h.Backend.Fail(400, "context_length_exceeded", "The input is too long.");
        h.Backend.Answer("Short summary.");
        h.Backend.Answer("Done after all.");
        var session = await h.StartSessionAsync(profile: null, model: "a-model-from-the-future"); // an overflow needs no known limit
        var items = await h.RunEndedAsync(session.Id);
        Assert.Contains(items, item => item.Kind == SessionItemKind.Compaction);
        Assert.Equal("Done after all.", items.Last(item => item.Kind == SessionItemKind.Assistant).Text);

        h.Backend.Fail(400, "context_length_exceeded", "The input is too long.");
        h.Backend.Answer("Another summary.");
        h.Backend.Fail(400, "context_length_exceeded", "Still too long.");
        await h.SendAsync(session.Id, "More.");
        items = await h.RunEndedAsync(session.Id, 2);
        Assert.Equal("failed", items[^1].Text);
        Assert.Contains(items, item => item.Kind == SessionItemKind.Notice && item.Text.Contains("Still too long"));
    }

    [Fact]
    public void AQueuedMessageSurvivesASummary()
    {
        static SessionItem Item(long seq, SessionItemKind kind, string text, string? replay = null) => new() { Seq = seq, Kind = kind, Text = text, Replay = replay };
        var state = AgentConversation.Rebuild(
        [
            Item(1, SessionItemKind.User, "First."),
            Item(2, SessionItemKind.Assistant, "Working."),
            Item(3, SessionItemKind.User, "Later.", System.Text.Json.JsonSerializer.Serialize(new AgentUserReplay(null, true), ProtocolJsonContext.Default.AgentUserReplay)),
            Item(4, SessionItemKind.Compaction, "Summary.")
        ]);
        Assert.Equal(["Summary.", "First."], state.Conversation.OfType<UserMessage>().Select(message => message.Text));
        Assert.True(state.HasQueued);
        Assert.True(state.JustCompacted);
        Assert.True(state.NeedsModel);
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)] // cloud metadata
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("2002:a00:1::", false)]
    [InlineData("64:ff9b::a00:1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("172.32.0.1", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void OnlyPublicAddressesCount(string address, bool isPublic) => Assert.Equal(isPublic, WebAddresses.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public void HtmlBecomesReadableText()
    {
        var text = WebFetcher.ToMarkdown("<html><head><title>T</title><style>a{}</style></head><body><!-- x --><h2>Title</h2><p>One <b>bold</b> &amp; <a href=\"https://x.test/a\">link</a></p><ul><li>Item</li></ul><script>evil()</script><pre>  code  </pre></body></html>");
        Assert.Contains("## Title", text);
        Assert.Contains("One **bold** & [link](https://x.test/a)", text);
        Assert.Contains("- Item", text);
        Assert.Contains("```", text);
        Assert.DoesNotContain("evil", text);
        Assert.DoesNotContain("<", text);
        Assert.Contains("[a](/b)", WebFetcher.ToMarkdown("<p><a href=/b>a</a></p>")); // minified, without quotes
        Assert.Contains("[a](/c)", WebFetcher.ToMarkdown("<a class=x href='/c' id=y>a</a>"));
    }

    [Fact]
    public async Task TheDefaultFetcherRefusesItsOwnNetwork()
    {
        await using var backend = await FakeAgentBackend.StartAsync();
        using var fetcher = new WebFetcher(allowPrivateNetworks: false);
        foreach (var (url, expected) in new[]
                 {
                     (backend.PageUrl, "usual web ports"),
                     ("http://127.0.0.1/", "not on the public internet"),
                     ("http://localhost/", "not on the public internet"),
                     ("http://169.254.169.254/latest/meta-data/", "not on the public internet"),
                     ("http://[::1]/", "not on the public internet"),
                     ("file:///etc/passwd", "http and https"),
                     ("http://user:secret@example.com/", "user name")
                 })
        {
            var result = await fetcher.FetchAsync(url, 0, default);
            Assert.True(result.IsError, url);
            Assert.Contains(expected, result.Text);
        }
    }

    [Fact]
    public void TheAuditLogKeepsNoQueryString() => Assert.Equal("https://x.test/a", WebFetcher.ForAudit("https://x.test/a?token=secret"));

    [Fact]
    public async Task WebFetchReadsAPageWithoutAskingAndIsAudited()
    {
        await using var h = await AgentSessionTest.Harness.StartAsync(allowPrivateFetch: true);
        h.Backend.Call("c1", AgentToolbox.WebFetchTool, new JsonObject { ["url"] = h.Backend.RedirectUrl + "?secret=1" }.ToJsonString());
        h.Backend.Answer("Read it.");
        var session = await h.StartSessionAsync(mode: "plan", profile: null, model: "gpt-5.5"); // reading is allowed even when planning
        var result = (await h.RunEndedAsync(session.Id)).First(item => item.Kind == SessionItemKind.ToolResult);
        Assert.False(result.IsError, result.Text);
        Assert.Contains("Title: x", result.Text);
        Assert.Contains("# Big news", result.Text);
        Assert.Contains("Hello & [more](/more)", result.Text);
        Assert.DoesNotContain("evil", result.Text);
        var audit = await AgentSessionTest.Eventually(() => h.Boris.GetStringAsync("/api/v1/audit"), text => text.Contains("web.fetched"));
        Assert.Contains("/redirect", audit);
        Assert.DoesNotContain("secret=1", audit);
    }

    [Fact]
    public async Task OpenAIGetsTheHostedSearchAndItsItemsGoBack()
    {
        await using var h = await AgentSessionTest.Harness.StartAsync();
        h.Backend.SearchedAndAnswered("capital of France", "Paris.");
        h.Backend.Answer("France.");
        var session = await h.StartSessionAsync(profile: null, model: "gpt-5.5");
        var items = await h.RunEndedAsync(session.Id);
        Assert.Contains(items, item => item.Kind == SessionItemKind.Thinking && item.Text == "Searched the web: capital of France");
        Assert.Contains(h.Backend.ModelRequests.First()["tools"]!.AsArray(), tool => (string?)tool!["type"] == "web_search");

        await h.SendAsync(session.Id, "Which country?");
        await h.RunEndedAsync(session.Id, 2);
        var replayed = Assert.Single(AgentSessionTest.OfType(h.Backend.ModelRequests.Last(), "web_search_call"));
        Assert.Null(replayed["id"]);
        Assert.Equal("capital of France", (string)replayed["action"]!["query"]!);
    }

    [Fact]
    public async Task AModelWithoutTheHostedSearchGoesWithout()
    {
        await using var h = await AgentSessionTest.Harness.StartAsync();
        h.Backend.Fail(400, "invalid_request_error", "Tool 'web_search' is not supported with this model.");
        h.Backend.Answer("Fine.");
        var session = await h.StartSessionAsync(profile: null, model: "gpt-5.5");
        Assert.Equal("completed", (await h.RunEndedAsync(session.Id))[^1].Text);
        Assert.DoesNotContain(h.Backend.ModelRequests.Last()["tools"]!.AsArray(), tool => (string?)tool!["type"] == "web_search");
    }

    [Fact]
    public async Task AnthropicTakesUpAPausedSearchAndKeepsItsBlocks()
    {
        await using var h = await AgentSessionTest.Harness.StartAsync();
        var account = await (await h.Boris.PostAsJsonAsync("/api/v1/accounts", new CreateHarnessAccountRequest(AccountKind.AnthropicApiKey, "Claude", "sk-ant-api03-testtesttesttesttesttesttest")))
            .Content.ReadFromJsonAsync<HarnessAccountDto>();
        h.AccountId = account!.Id;
        h.Backend.AnthropicSearchPaused("capital of France");
        h.Backend.AnthropicCitedAnswer("Paris.");
        h.Backend.AnthropicAnswer("France.");
        var session = await h.StartSessionAsync(profile: null, model: "claude-opus-5-5");
        var items = await h.RunEndedAsync(session.Id);
        Assert.Equal("Paris.", items.Last(item => item.Kind == SessionItemKind.Assistant).Text);
        Assert.Contains(items, item => item.Text == "Searched the web: capital of France");

        var requests = h.Backend.ModelRequests.ToArray();
        Assert.Equal("web_search_20260209", (string)requests[0]["tools"]![0]!["type"]!);
        var continued = requests[1]["messages"]!.AsArray();
        Assert.Equal("assistant", (string)continued[^1]!["role"]!); // the same turn goes on
        Assert.Null(continued[^1]!["content"]!.AsArray().Last()!["cache_control"]);

        await h.SendAsync(session.Id, "Which country?");
        await h.RunEndedAsync(session.Id, 2);
        var assistant = h.Backend.ModelRequests.Last()["messages"]!.AsArray()[1]!["content"]!.AsArray();
        Assert.Equal(["server_tool_use", "web_search_tool_result", "server_tool_use", "code_execution_tool_result", "text"], assistant.Select(block => (string?)block!["type"]));
        Assert.Equal("opaque", (string)assistant[1]!["content"]![0]!["encrypted_content"]!);
        Assert.Equal(("print(1)", "direct"), ((string)assistant[2]!["input"]!["code"]!, (string)assistant[2]!["caller"]!["type"]!)); // the server-side code comes back whole
        Assert.Equal("1", (string)assistant[3]!["content"]!["stdout"]!);
        Assert.Equal("https://example.com", (string)assistant[4]!["citations"]![0]!["url"]!);
    }

    static async Task UseAnthropicAsync(AgentSessionTest.Harness h)
    {
        var account = await (await h.Boris.PostAsJsonAsync("/api/v1/accounts", new CreateHarnessAccountRequest(AccountKind.AnthropicApiKey, "Claude", "sk-ant-api03-testtesttesttesttesttesttest")))
            .Content.ReadFromJsonAsync<HarnessAccountDto>();
        h.AccountId = account!.Id;
    }

    [Fact]
    public async Task TheModelListIsWhatTheProviderOffersAndTheSnapshotKnows()
    {
        await using var h = await AgentSessionTest.Harness.StartAsync();
        var openAi = await h.Boris.GetFromJsonAsync<AgentModelDto[]>($"/api/v1/accounts/{h.AccountId}/models");
        Assert.Equal(["gpt-5.5"], openAi!.Select(model => model.Id));
        Assert.Equal("GPT-5.5", openAi![0].Name);
        Assert.Equal((5, 30), (openAi[0].InputPrice, openAi[0].OutputPrice));
        Assert.Contains("high", openAi[0].ReasoningEfforts);
        await UseAnthropicAsync(h);
        Assert.Equal(["claude-opus-5-5"], (await h.Boris.GetFromJsonAsync<AgentModelDto[]>($"/api/v1/accounts/{h.AccountId}/models"))!.Select(model => model.Id));

        h.Backend.ModelListFails = true; // the snapshot will do
        var fallback = await h.Boris.GetFromJsonAsync<AgentModelDto[]>($"/api/v1/accounts/{h.AccountId}/models");
        Assert.Contains(fallback!, model => model.Id == "claude-haiku-5-5");
    }

    [Fact]
    public async Task TheSessionReportsHowFullItsContextIsAndWhenItsCacheLapses()
    {
        await using var h = await AgentSessionTest.Harness.StartAsync();
        await UseAnthropicAsync(h);
        h.Backend.AnthropicAnswer("Little cached.");
        var session = await h.StartSessionAsync(profile: null, model: "claude-opus-5-5");
        await h.RunEndedAsync(session.Id);
        var after = await h.Boris.GetFromJsonAsync<SessionDto>($"/api/v1/sessions/{session.Id}");
        Assert.Equal((19, 1_000_000), (after!.ContextTokens!.Value, after.ContextLimit!.Value)); // 10 + 4 cached + 5 out
        Assert.Null(after.CacheExpiresAt); // too little to matter

        h.Backend.AnthropicCacheRead = 50_000;
        h.Backend.AnthropicAnswer("Much cached.");
        await h.SendAsync(session.Id, "Again.");
        await h.RunEndedAsync(session.Id, 2);
        after = await h.Boris.GetFromJsonAsync<SessionDto>($"/api/v1/sessions/{session.Id}");
        var left = after!.CacheExpiresAt!.Value - DateTime.UtcNow;
        Assert.InRange(left.TotalMinutes, 3, 5.1);
    }

    [Fact]
    public async Task AnOpenAISessionCountsNoCacheDown()
    {
        await using var h = await AgentSessionTest.Harness.StartAsync();
        h.Backend.Call("c1", Notify, Message, inputTokens: 500_000);
        h.Backend.Answer("Done.");
        var session = await h.StartSessionAsync(profile: null, model: "gpt-5.5");
        await h.RunEndedAsync(session.Id);
        var after = await h.Boris.GetFromJsonAsync<SessionDto>($"/api/v1/sessions/{session.Id}");
        Assert.Null(after!.CacheExpiresAt);
        Assert.Equal(922_000, after.ContextLimit);
    }

    [Fact]
    public async Task ThinkingCarriesARefSoItsStreamEnds()
    {
        await using var h = await AgentSessionTest.Harness.StartAsync();
        await UseAnthropicAsync(h);
        h.Backend.AnthropicThinkAndCall("Hmm.", "sig", "toolu_1", Notify, Message);
        h.Backend.AnthropicAnswer("Done.");
        var items = await h.RunEndedAsync((await h.StartSessionAsync(profile: null, model: "claude-opus-5-5")).Id);
        Assert.StartsWith("thinking-", items.Single(item => item.Kind == SessionItemKind.Thinking).Ref);
    }

    [Fact]
    public async Task OpenCodeGoGetsNoHostedSearchButKeepsWebFetch()
    {
        await using var h = await AgentSessionTest.Harness.StartAsync();
        var account = await (await h.Boris.PostAsJsonAsync("/api/v1/accounts", new CreateHarnessAccountRequest(AccountKind.OpenCodeGoApiKey, "Go", "go-testtesttesttesttesttesttest")))
            .Content.ReadFromJsonAsync<HarnessAccountDto>();
        h.AccountId = account!.Id;
        h.Backend.Answer("Hi.");
        await h.RunEndedAsync((await h.StartSessionAsync(profile: null, model: "glm-5.3")).Id);
        var tools = AgentSessionTest.Tools(h.Backend.ModelRequests.Single());
        Assert.DoesNotContain("web_search", tools);
        Assert.Contains(AgentToolbox.WebFetchTool, tools);
    }
}
