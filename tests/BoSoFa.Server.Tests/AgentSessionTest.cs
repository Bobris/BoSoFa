using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Tests;

/// <summary>The server agent on one real server, against a fake model provider and a fake HTTP MCP server (docs/server-agent.md).</summary>
public class AgentSessionTest
{
    const string Key = "sk-testtesttesttesttesttesttest";
    const string McpTool = "mcp__facts__lookup";
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    internal static async Task<T> Eventually<T>(Func<Task<T>> probe, Func<T, bool> done)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            var value = await probe();
            if (done(value)) return value;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Still {System.Text.Json.JsonSerializer.Serialize(value, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))}");
            await Task.Delay(30);
        }
    }

    internal sealed class Harness : IAsyncDisposable
    {
        public TestServer Server = null!;
        public FakeAgentBackend Backend = null!;
        public HttpClient Boris = null!;
        public ulong AccountId;

        /// <param name="liveKey">An OpenAI key: the real API answers instead of the script.</param>
        public static async Task<Harness> StartAsync(string? liveKey = null)
        {
            var harness = new Harness { Backend = await FakeAgentBackend.StartAsync() };
            harness.Server = await TestServer.StartAsync(extraArgs: liveKey != null ? [] : ["--BoSoFa:Agent:OpenAIEndpoint", harness.Backend.ResponsesUrl, "--BoSoFa:Agent:AnthropicEndpoint", harness.Backend.MessagesUrl, "--BoSoFa:Agent:OpenCodeGoBaseUrl", harness.Backend.GoBaseUrl]);
            harness.Boris = harness.Server.Client();
            (await harness.Boris.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("boris@example.com", "Boris"))).EnsureSuccessStatusCode();
            await harness.Boris.PostAsJsonAsync("/api/v1/mcp-servers", new PublishMcpServer("facts", McpTransport.Http, null, null, harness.Backend.McpUrl,
                new() { ["X-Secret"] = "mcp-secret" }));
            (await harness.Boris.PostAsJsonAsync("/api/v1/profiles", new ProfileDto("research", null, null, ["facts"], []))).EnsureSuccessStatusCode();
            var account = await (await harness.Boris.PostAsJsonAsync("/api/v1/accounts", new CreateHarnessAccountRequest(AccountKind.OpenAIApiKey, "Work OpenAI", liveKey ?? Key)))
                .Content.ReadFromJsonAsync<HarnessAccountDto>();
            Assert.True(account!.ServesAgent);
            harness.AccountId = account.Id;
            return harness;
        }

        public async Task<SessionDto> StartSessionAsync(string prompt = "What is the answer?", string? mode = null, string? profile = "research", string model = "test-model")
        {
            var response = await Boris.PostAsJsonAsync("/api/v1/sessions",
                new CreateSessionRequest(0, Harnesses.Agent, "", prompt, mode, model, AccountId, ProfileName: profile));
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<SessionDto>())!;
        }

        /// <summary>The transcript as the loop's tests see it: without the item that records the system prompt.</summary>
        public async Task<SessionItemDto[]> ItemsAsync(ulong id) => (await AllItemsAsync(id)).Where(item => item.Kind != SessionItemKind.Instructions).ToArray();
        public Task<SessionItemDto[]> AllItemsAsync(ulong id) => Boris.GetFromJsonAsync<SessionItemDto[]>($"/api/v1/sessions/{id}/items")!;
        public Task<SessionItemDto[]> RunEndedAsync(ulong id, int count = 1) => Eventually(() => ItemsAsync(id), items => items.Count(item => item.Kind == SessionItemKind.RunEnded) >= count);
        public Task<RuntimeRequestDto> RequestAsync(ulong id) =>
            Eventually(async () => (await Boris.GetFromJsonAsync<RuntimeRequestDto[]>($"/api/v1/sessions/{id}/requests"))!.FirstOrDefault()!, request => request != null);
        public async Task AnswerAsync(ulong id, ulong request, bool allow, string? message = null) =>
            (await Boris.PostAsJsonAsync($"/api/v1/sessions/{id}/requests/{request}/answer", new AnswerRuntimeRequest(allow, message))).EnsureSuccessStatusCode();
        public async Task SendAsync(ulong id, string text, DispatchMode mode = DispatchMode.Queue) =>
            (await Boris.PostAsJsonAsync($"/api/v1/sessions/{id}/messages", new SendMessageRequest(text, mode))).EnsureSuccessStatusCode();
        public Task<SessionDto> IdleAsync(ulong id) =>
            Eventually(() => Boris.GetFromJsonAsync<SessionDto>($"/api/v1/sessions/{id}")!, session => session!.Status == SessionStatus.Idle)!;

        public async ValueTask DisposeAsync()
        {
            Boris.Dispose();
            await Server.DisposeAsync();
            await Backend.DisposeAsync();
        }
    }

    internal static JsonArray Input(JsonObject request) => request["input"]!.AsArray();
    internal static IEnumerable<JsonObject> OfType(JsonObject request, string type) => Input(request).OfType<JsonObject>().Where(item => item["type"]?.GetValue<string>() == type);
    internal static string[] Tools(JsonObject request) => request["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>()).ToArray();

    [Fact]
    public async Task ASessionWithNoMachineAnswersThroughAnHttpMcpServer()
    {
        await using var h = await Harness.StartAsync();
        h.Backend.Call("call_1", McpTool, "{\"q\":\"life\"}");
        h.Backend.Answer("It is 42.");
        var session = await h.StartSessionAsync();
        Assert.Equal((ulong)0, session.MachineId);
        Assert.True(session.CanControl);

        var request = await h.RequestAsync(session.Id);
        Assert.Equal(McpTool, request.ToolName);
        Assert.Equal(SessionStatus.WaitingForApproval, (await h.Boris.GetFromJsonAsync<SessionDto>($"/api/v1/sessions/{session.Id}"))!.Status);
        Assert.Empty(h.Backend.ToolCalls);
        await h.AnswerAsync(session.Id, request.Id, true);

        var items = await h.RunEndedAsync(session.Id);
        Assert.Equal([SessionItemKind.User, SessionItemKind.ToolUse, SessionItemKind.ToolResult, SessionItemKind.Assistant, SessionItemKind.RunEnded],
            items.Select(item => item.Kind));
        Assert.Equal("The answer is 42.", items[2].Text);
        Assert.Equal("It is 42.", items[3].Text);
        Assert.Equal("completed", items[4].Text);
        Assert.Contains("\"inputTokens\":20", items[4].Data);
        Assert.Equal(SessionStatus.Idle, (await h.IdleAsync(session.Id)).Status);

        Assert.Equal(["{\"q\":\"life\"}"], h.Backend.ToolCalls);
        Assert.All(h.Backend.McpAuthorizations, secret => Assert.Equal("mcp-secret", secret));
        Assert.All(h.Backend.ModelAuthorizations, authorization => Assert.Equal("Bearer " + Key, authorization));
        var second = h.Backend.ModelRequests.ElementAt(1);
        Assert.Contains(McpTool, Tools(second));
        Assert.Equal("The answer is 42.", OfType(second, "function_call_output").Single()["output"]!.GetValue<string>());

        // The next message goes on from the stored conversation.
        h.Backend.Answer("Nothing else.");
        await h.SendAsync(session.Id, "Anything else?");
        await h.RunEndedAsync(session.Id, 2);
        var third = h.Backend.ModelRequests.ElementAt(2);
        Assert.Equal(["function_call", "function_call_output", "message"], OfType(third, "function_call").Concat(OfType(third, "function_call_output")).Concat(OfType(third, "message").Take(1))
            .Select(item => item["type"]!.GetValue<string>()));
        Assert.Equal(3, OfType(third, "message").Count(message => (string?)message["role"] != "developer")); // two questions and one answer

        // Opaque provider data stays on the server.
        Assert.DoesNotContain("replay", await h.Boris.GetStringAsync($"/api/v1/sessions/{session.Id}/items"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheModeDecidesWhatNeedsAPersonAndWhichToolsExist()
    {
        await using var h = await Harness.StartAsync();
        h.Backend.Call("call_1", McpTool, "{}");
        h.Backend.Answer("Done.");
        var bypass = await h.StartSessionAsync(mode: "bypassPermissions");
        await h.RunEndedAsync(bypass.Id);
        Assert.Single(h.Backend.ToolCalls);

        h.Backend.Call("call_2", McpTool, "{}"); // hidden in plan mode: the model is told, and nothing runs
        h.Backend.Answer("Understood.");
        var plan = await h.StartSessionAsync(mode: "plan");
        var items = await h.RunEndedAsync(plan.Id);
        Assert.Single(h.Backend.ToolCalls);
        Assert.Contains(items, item => item is { Kind: SessionItemKind.ToolResult, IsError: true } && item.Text.Contains("no tool named"));
        Assert.DoesNotContain(McpTool, Tools(h.Backend.ModelRequests.ElementAt(2)));
    }

    [Fact]
    public async Task ADenialAndBrokenArgumentsAreAnswersTheModelReads()
    {
        await using var h = await Harness.StartAsync();
        h.Backend.Call("call_1", McpTool, "{}");
        h.Backend.Call("call_2", McpTool, "{not json");
        h.Backend.Call("call_3", "made_up", "{}");
        h.Backend.Answer("I give up.");
        var session = await h.StartSessionAsync();
        await h.AnswerAsync(session.Id, (await h.RequestAsync(session.Id)).Id, false, "Too risky");
        var items = await h.RunEndedAsync(session.Id);
        var results = items.Where(item => item.Kind == SessionItemKind.ToolResult).ToArray();
        Assert.Equal(3, results.Length);
        Assert.All(results, result => Assert.True(result.IsError));
        Assert.Equal("The person did not allow this. They added: Too risky", results[0].Text);
        Assert.Contains("not a valid JSON object", results[1].Text);
        Assert.Contains("no tool named made_up", results[2].Text);
        Assert.Empty(h.Backend.ToolCalls);
    }

    [Fact]
    public async Task TheAgentAsksAndNotifiesThePerson()
    {
        await using var h = await Harness.StartAsync();
        h.Backend.Call("call_1", "notify_user", "{\"message\":\"Half way\"}");
        h.Backend.Call("call_2", "ask_user", "{\"question\":\"Ship it?\"}");
        h.Backend.Answer("Shipping.");
        var session = await h.StartSessionAsync(profile: null);
        var question = await h.RequestAsync(session.Id);
        Assert.Equal("Ship it?", question.Description);
        await h.AnswerAsync(session.Id, question.Id, true, "after lunch");
        var items = await h.RunEndedAsync(session.Id);
        Assert.Contains(items, item => item is { Kind: SessionItemKind.Notice, Text: "Half way" });
        Assert.Contains(items, item => item is { Kind: SessionItemKind.ToolResult, Text: "The person answered yes. They added: after lunch" });
    }

    [Fact]
    public async Task AnInterruptEndsTheCallAndTheSessionGoesOn()
    {
        await using var h = await Harness.StartAsync();
        h.Backend.Hang("Thinking out loud");
        var session = await h.StartSessionAsync();
        await h.Backend.WaitForModelCallAsync();
        (await h.Boris.PostAsync($"/api/v1/sessions/{session.Id}/interrupt", null)).EnsureSuccessStatusCode();
        var items = await h.RunEndedAsync(session.Id);
        Assert.Equal("interrupted", items[^1].Text);
        await h.IdleAsync(session.Id);

        h.Backend.Answer("Back.");
        await h.SendAsync(session.Id, "Continue");
        Assert.Equal("completed", (await h.RunEndedAsync(session.Id, 2))[^1].Text);
    }

    [Fact]
    public async Task AnInterruptDuringAToolReportsItToTheModel()
    {
        await using var h = await Harness.StartAsync();
        h.Backend.HoldTool = new();
        h.Backend.Call("call_1", McpTool, "{}");
        var session = await h.StartSessionAsync(mode: "bypassPermissions");
        await h.Backend.WaitForToolAsync();
        (await h.Boris.PostAsync($"/api/v1/sessions/{session.Id}/interrupt", null)).EnsureSuccessStatusCode();
        var items = await h.RunEndedAsync(session.Id);
        Assert.Contains(items, item => item is { Kind: SessionItemKind.ToolResult, IsError: true } && item.Text.Contains("interrupted"));
        Assert.Equal("interrupted", items[^1].Text);
    }

    [Fact]
    public async Task SteeringReachesTheNextStepAndAQueuedMessageTheNextRun()
    {
        await using var h = await Harness.StartAsync();
        h.Backend.HoldTool = new();
        h.Backend.Call("call_1", McpTool, "{}");
        h.Backend.Answer("Steered answer.");
        h.Backend.Answer("Queued answer.");
        var session = await h.StartSessionAsync(mode: "bypassPermissions");
        await h.Backend.WaitForToolAsync();

        // Both arrive while the tool runs, so both are stored between the call and its result.
        await h.SendAsync(session.Id, "Focus on this", DispatchMode.Steer);
        await h.SendAsync(session.Id, "And then this", DispatchMode.Queue);
        h.Backend.HoldTool.SetResult();

        await h.RunEndedAsync(session.Id, 2);
        var afterTool = Input(h.Backend.ModelRequests.ElementAt(1));
        var nextRun = h.Backend.ModelRequests.ElementAt(2);
        Assert.Equal("function_call_output", afterTool[^2]!["type"]!.GetValue<string>());
        Assert.Equal("Focus on this", afterTool[^1]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.DoesNotContain("And then this", afterTool.ToJsonString());
        Assert.Equal("And then this", Input(nextRun)[^1]!["content"]![0]!["text"]!.GetValue<string>());
        await h.IdleAsync(session.Id);
    }

    [Fact]
    public async Task AProviderFailureEndsTheRunAndTellsThePerson()
    {
        await using var h = await Harness.StartAsync();
        h.Backend.Fail(401, "invalid_api_key", "Incorrect API key.");
        var session = await h.StartSessionAsync();
        var items = await h.RunEndedAsync(session.Id);
        Assert.Contains(items, item => item is { Kind: SessionItemKind.Notice, IsError: true } && item.Text.Contains("Incorrect API key"));
        Assert.True(items[^1].IsError);
        await h.IdleAsync(session.Id);
    }

    [Fact]
    public async Task StoppingEndsARunningSession()
    {
        await using var h = await Harness.StartAsync();
        h.Backend.Hang("Going on");
        var session = await h.StartSessionAsync();
        await h.Backend.WaitForModelCallAsync();
        (await h.Boris.PostAsync($"/api/v1/sessions/{session.Id}/stop", null)).EnsureSuccessStatusCode();
        await Eventually(() => h.Boris.GetFromJsonAsync<SessionDto>($"/api/v1/sessions/{session.Id}")!, s => s!.Status == SessionStatus.Stopped);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Boris.PostAsJsonAsync($"/api/v1/sessions/{session.Id}/messages", new SendMessageRequest("More"))).StatusCode);
    }

    [Fact]
    public async Task ReasoningLargerThanAnItemGoesBackWhole()
    {
        await using var h = await Harness.StartAsync();
        var encrypted = new string('e', 40_000);
        h.Backend.Reasoned(encrypted, "call_1", "notify_user", "{\"message\":\"Hi\"}");
        h.Backend.Answer("Done.");
        var session = await h.StartSessionAsync(profile: null);
        await h.RunEndedAsync(session.Id);
        var reasoning = OfType(h.Backend.ModelRequests.ElementAt(1), "reasoning").Single();
        Assert.Equal(encrypted, reasoning["encrypted_content"]!.GetValue<string>());
        Assert.Null(reasoning["id"]);
    }

    [Fact]
    public async Task ARestartAsksTheModelAgainReportsARunningToolAsUnknownAndKeepsAPendingApproval()
    {
        await using var h = await Harness.StartAsync();

        // A model call that was streaming is made again.
        h.Backend.Hang("Half an answer");
        h.Backend.Call("call_1", McpTool, "{\"n\":1}");
        h.Backend.Call("call_2", McpTool, "{\"n\":2}");
        h.Backend.HoldTool = new();
        var session = await h.StartSessionAsync(mode: "bypassPermissions");
        await h.Backend.WaitForModelCallAsync();
        await h.Server.StopAsync();
        await h.Server.StartAppAsync();
        await h.Backend.WaitForToolAsync();

        // That second answer asked for a tool, which is now running: its effect is unknown after this restart, and it is not run again.
        await h.Server.StopAsync();
        await h.Server.StartAppAsync();
        var unknown = await Eventually(() => h.ItemsAsync(session.Id), items => items.Any(item => item.Kind == SessionItemKind.ToolResult));
        Assert.Contains("whether it took effect is unknown", unknown.First(item => item.Kind == SessionItemKind.ToolResult).Text);

        // The next call runs normally, and is answered.
        await h.Backend.WaitForToolAsync();
        h.Backend.HoldTool.SetResult();
        h.Backend.HoldTool = null;
        h.Backend.Answer("Finished.");
        var items = await h.RunEndedAsync(session.Id);
        Assert.Equal("Finished.", items.Last(item => item.Kind == SessionItemKind.Assistant).Text);
        Assert.Equal(["{\"n\":1}", "{\"n\":2}"], h.Backend.ToolCalls);

        // A request waiting for a person survives, and the answer lets the tool run.
        h.Backend.Call("call_3", McpTool, "{}");
        h.Backend.Answer("Finished again.");
        var waiting = await h.StartSessionAsync();
        var request = await h.RequestAsync(waiting.Id);
        await h.Server.StopAsync();
        await h.Server.StartAppAsync();
        Assert.Equal(request.Id, (await h.RequestAsync(waiting.Id)).Id);
        await h.AnswerAsync(waiting.Id, request.Id, true);
        var done = await h.RunEndedAsync(waiting.Id);
        Assert.Contains(done, item => item is { Kind: SessionItemKind.ToolResult, Text: "The answer is 42." });
    }

    /// <summary>The whole loop against the real API, with the key file named by BOSOFA_TEST_OPENAI_KEY_FILE: the tool's result reaches the
    /// answer, and a second message goes on from the stored conversation, reasoning included.</summary>
    [ProviderTest.LiveOpenAIFact]
    public async Task LiveOpenAIAnswersThroughAnMcpServer()
    {
        var key = File.ReadAllText(Environment.GetEnvironmentVariable("BOSOFA_TEST_OPENAI_KEY_FILE")!).Trim();
        var model = Environment.GetEnvironmentVariable("BOSOFA_TEST_OPENAI_MODEL") is { Length: > 0 } chosen ? chosen : "gpt-5.4-nano";
        await using var h = await Harness.StartAsync(key);
        var session = await h.StartSessionAsync("Call the lookup tool once with q set to \"meaning\", then answer with the number it gives and nothing else.",
            "bypassPermissions", model: model);
        var items = await EventuallyLong(() => h.ItemsAsync(session.Id), all => all.Any(item => item.Kind == SessionItemKind.RunEnded));
        Assert.False(items[^1].IsError, items[^1].Text + " " + string.Join(" | ", items.Where(item => item.Kind == SessionItemKind.Notice).Select(item => item.Text)));
        Assert.NotEmpty(h.Backend.ToolCalls);
        Assert.Contains("42", items.Last(item => item.Kind == SessionItemKind.Assistant).Text);

        await h.SendAsync(session.Id, "Add one to that number and answer with the result only.");
        items = await EventuallyLong(() => h.ItemsAsync(session.Id), all => all.Count(item => item.Kind == SessionItemKind.RunEnded) == 2);
        Assert.False(items[^1].IsError);
        Assert.Contains("43", items.Last(item => item.Kind == SessionItemKind.Assistant).Text);
    }

    static async Task<T> EventuallyLong<T>(Func<Task<T>> probe, Func<T, bool> done)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (true)
        {
            var value = await probe();
            if (done(value) || DateTime.UtcNow > deadline) return value;
            await Task.Delay(200);
        }
    }

    [Fact]
    public async Task OnlyAProperAgentSessionStarts()
    {
        await using var h = await Harness.StartAsync();
        (await h.Boris.PostAsJsonAsync("/api/v1/mcp-servers", new PublishMcpServer("local", McpTransport.Stdio, "tool", null, null, null))).EnsureSuccessStatusCode();
        (await h.Boris.PostAsJsonAsync("/api/v1/profiles", new ProfileDto("onmachine", null, null, ["local"], []))).EnsureSuccessStatusCode();
        async Task<HttpResponseMessage> Create(ulong machine, string? model, ulong? account, string? profile = null) =>
            await h.Boris.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest(machine, Harnesses.Agent, "", "Hi", null, model, account, ProfileName: profile));
        Assert.Equal(HttpStatusCode.BadRequest, (await Create(0, null, h.AccountId)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Create(0, "m", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Create(7, "m", h.AccountId)).StatusCode);
        var stdio = await Create(0, "m", h.AccountId, "onmachine");
        Assert.Equal(HttpStatusCode.BadRequest, stdio.StatusCode);
        Assert.Contains("run on a machine", await stdio.Content.ReadAsStringAsync());

        // Someone else's account is not usable, and the session shows only to its creator.
        using var anna = h.Server.Client();
        (await anna.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("anna@example.com", "Anna"))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await anna.PostAsJsonAsync("/api/v1/sessions",
            new CreateSessionRequest(0, Harnesses.Agent, "", "Hi", null, "m", h.AccountId))).StatusCode);
        h.Backend.Answer("Hello.");
        var session = await h.StartSessionAsync(profile: null);
        Assert.Equal(HttpStatusCode.NotFound, (await anna.GetAsync($"/api/v1/sessions/{session.Id}")).StatusCode);
    }
}
