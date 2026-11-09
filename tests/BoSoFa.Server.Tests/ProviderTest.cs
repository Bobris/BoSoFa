using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using BoSoFa.Protocol;
using BoSoFa.Server.Agents;

namespace BoSoFa.Server.Tests;

/// <summary>Answers model calls with recorded streams and keeps what was asked (docs/server-agent.md §10).</summary>
sealed class FakeProvider : HttpMessageHandler
{
    readonly Queue<Func<HttpResponseMessage>> _answers = new();
    public List<(HttpRequestMessage Request, JsonObject Body)> Calls { get; } = [];

    public static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "providers", name));

    public FakeProvider Stream(string fixture)
    {
        _answers.Enqueue(() => new(HttpStatusCode.OK) { Content = new StringContent(Fixture(fixture), Encoding.UTF8, "text/event-stream") });
        return this;
    }

    public FakeProvider Error(HttpStatusCode status, string body, Action<HttpResponseHeaders>? headers = null)
    {
        _answers.Enqueue(() =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            headers?.Invoke(response.Headers);
            return response;
        });
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
        Calls.Add((request, body));
        return _answers.Dequeue()();
    }
}

/// <summary>Timers fire at once; the waits asked for are kept.</summary>
sealed class InstantTime : TimeProvider
{
    public List<TimeSpan> Waits { get; } = [];

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime != Timeout.InfiniteTimeSpan) Waits.Add(dueTime);
        var timer = System.CreateTimer(callback, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (dueTime != Timeout.InfiniteTimeSpan) callback(state);
        return timer;
    }
}

public class ProviderTest
{
    static readonly ToolSpec Add = new("add", "Adds two integers.", JsonNode.Parse("""
        {"type":"object","properties":{"a":{"type":"integer"},"b":{"type":"integer"}},"required":["a","b"]}
        """)!.AsObject());

    const string Question = "3 packs of 7 pens and 5 packs of 11 pencils: use the add tool to add the counts.";

    static TurnRequest Request(string model, IReadOnlyList<ConversationItem> conversation, string? effort = null, string? session = null) =>
        new(model, "You are a test assistant.", conversation, [Add], "session-1", session, effort);

    static async Task<(TurnResponse Response, List<TurnDelta> Deltas)> RunAsync(IModelTurnExecutor executor, TurnRequest request)
    {
        var deltas = new List<TurnDelta>();
        var response = await executor.ExecuteAsync(request, delta =>
        {
            deltas.Add(delta);
            return ValueTask.CompletedTask;
        }, CancellationToken.None);
        return (response, deltas);
    }

    [Fact]
    public async Task OpenAIResponsesRunsAToolCallRoundTripFromRecordedStreams()
    {
        var fake = new FakeProvider().Stream("openai/reasoning-tool-call.sse").Stream("openai/final-answer.sse");
        var executor = OpenAIResponsesExecutor.WithApiKey(new HttpClient(fake), "sk-test");
        List<ConversationItem> conversation = [new UserMessage(Question)];

        var (first, deltas) = await RunAsync(executor, Request("gpt-5.4-mini", conversation, "medium", "Answer in English."));
        Assert.Equal(StopReason.ToolCalls, first.Stop);
        var reasoning = Assert.IsType<ReasoningItem>(first.Output[0]);
        Assert.Contains("pens", reasoning.Text, StringComparison.OrdinalIgnoreCase);
        var replayed = JsonNode.Parse(reasoning.ProviderData!)!.AsObject();
        Assert.True(replayed.ContainsKey("encrypted_content"));
        Assert.False(replayed.ContainsKey("id"));
        var call = Assert.IsType<ToolCall>(first.Output[1]);
        Assert.Equal(("add", """{"a":21,"b":55}"""), (call.Name, call.Arguments));
        Assert.Equal(new TurnUsage(107, 0, 0, 92, 69), first.Usage);
        Assert.Contains(deltas, delta => delta.Kind == TurnDeltaKind.Reasoning);
        Assert.Equal("""{"a":21,"b":55}""", string.Concat(deltas.Where(d => d.Kind == TurnDeltaKind.ToolArguments).Select(d => d.Text)));

        var (request, body) = fake.Calls[0];
        Assert.Equal("Bearer sk-test", request.Headers.Authorization?.ToString());
        Assert.StartsWith(Brand.Name + "/", request.Headers.UserAgent.ToString());
        Assert.Equal((false, true, "You are a test assistant."), ((bool)body["store"]!, (bool)body["stream"]!, (string)body["instructions"]!));
        Assert.Equal("reasoning.encrypted_content", (string)body["include"]![0]!);
        Assert.Equal("medium", (string)body["reasoning"]!["effort"]!);
        Assert.Equal("developer", (string)body["input"]![0]!["role"]!); // the session's instructions, after the shared ones
        Assert.Equal("add", (string)body["tools"]![0]!["name"]!);

        conversation.AddRange(first.Output);
        conversation.Add(new ToolResult(call.Id, "76"));
        var (second, _) = await RunAsync(executor, Request("gpt-5.4-mini", conversation, "medium"));
        Assert.Equal(StopReason.EndTurn, second.Stop);
        Assert.Contains("76", Assert.IsType<AssistantMessage>(Assert.Single(second.Output)).Text);
        var input = fake.Calls[1].Body["input"]!.AsArray();
        Assert.Equal(["message", "reasoning", "function_call", "function_call_output"], input.Select(item => (string?)item!["type"]));
        Assert.All(input.Skip(1), item => Assert.False(item!.AsObject().ContainsKey("id")));
        Assert.Equal(call.Id, (string)input[3]!["call_id"]!);
    }

    [Fact]
    public void OpenAIReplaysReasoningOnlyToItsOwnModel()
    {
        var executor = OpenAIResponsesExecutor.WithApiKey(new HttpClient(new FakeProvider()), "sk-test");
        var origin = new ModelOrigin(OpenAIResponsesExecutor.ApiName, executor.Endpoint, "gpt-5.4-mini");
        List<ConversationItem> conversation =
        [
            new UserMessage(Question),
            new ReasoningItem("thinking", origin, """{"type":"reasoning","encrypted_content":"x","summary":[]}"""),
            new ToolCall("call_1", "add", """{"a":21,"b":55}""", origin, """{"type":"function_call","call_id":"call_1","name":"add","arguments":"{}"}"""),
            new ToolResult("call_1", "76")
        ];
        var sameModel = executor.Body(Request("gpt-5.4-mini", conversation), origin)["input"]!.AsArray();
        Assert.Equal(["message", "reasoning", "function_call", "function_call_output"], sameModel.Select(item => (string?)item!["type"]));

        var other = origin with { Model = "gpt-5.5" };
        var otherModel = executor.Body(Request("gpt-5.5", conversation), other)["input"]!.AsArray();
        Assert.Equal(["message", "function_call", "function_call_output"], otherModel.Select(item => (string?)item!["type"]));
        Assert.Equal("""{"a":21,"b":55}""", (string)otherModel[1]!["arguments"]!); // rebuilt from the call itself
    }

    [Fact]
    public async Task OpenCodeGoGetsItsSessionHeaderOnBothApis()
    {
        var fake = new FakeProvider().Stream("openai/final-answer.sse").Stream("anthropic/final-answer.sse");
        await RunAsync(OpenAIResponsesExecutor.ForOpenCodeGo(new HttpClient(fake), "go-key"), Request("gpt-6-luna", [new UserMessage("hi")]));
        await RunAsync(AnthropicMessagesExecutor.ForOpenCodeGo(new HttpClient(fake), "go-key"), Request("qwen3.8-max", [new UserMessage("hi")]));
        Assert.Equal(OpenAIResponsesExecutor.OpenCodeGoEndpoint, fake.Calls[0].Request.RequestUri);
        Assert.Equal(AnthropicMessagesExecutor.OpenCodeGoEndpoint, fake.Calls[1].Request.RequestUri);
        Assert.All(fake.Calls, call => Assert.Equal("session-1", call.Request.Headers.GetValues("x-opencode-session").Single()));
        Assert.Equal("go-key", fake.Calls[1].Request.Headers.GetValues("x-api-key").Single());
    }

    [Fact]
    public async Task AnthropicMessagesRunsAToolCallRoundTrip()
    {
        var fake = new FakeProvider().Stream("anthropic/thinking-tool-use.sse").Stream("anthropic/final-answer.sse");
        var executor = AnthropicMessagesExecutor.WithApiKey(new HttpClient(fake), "ant-key");
        List<ConversationItem> conversation = [new UserMessage(Question)];

        var (first, deltas) = await RunAsync(executor, Request("claude-test", conversation, "high", "Answer in English."));
        Assert.Equal(StopReason.ToolCalls, first.Stop);
        var thinking = Assert.IsType<ReasoningItem>(first.Output[0]);
        Assert.Equal("Pens: 3 x 7 = 21. Pencils: 5 x 11 = 55.", thinking.Text);
        Assert.Equal("EqQBCgIYAhIM1gbcDa9GJwZA2b3h", (string)JsonNode.Parse(thinking.ProviderData!)!["signature"]!);
        Assert.Equal("Adding the two counts.", Assert.IsType<AssistantMessage>(first.Output[1]).Text);
        var call = Assert.IsType<ToolCall>(first.Output[2]);
        Assert.Equal(("toolu_01Add", "add"), (call.Id, call.Name));
        Assert.Equal(21, (int)JsonNode.Parse(call.Arguments)!["a"]!);
        Assert.Equal(new TurnUsage(1540, 1200, 300, 64, 0), first.Usage); // input counts the cached tokens too
        Assert.Contains(deltas, delta => delta.Kind == TurnDeltaKind.Reasoning);

        var (request, body) = fake.Calls[0];
        Assert.Equal("ant-key", request.Headers.GetValues("x-api-key").Single());
        Assert.Equal(AnthropicMessagesExecutor.ApiVersion, request.Headers.GetValues("anthropic-version").Single());
        Assert.Equal("ephemeral", (string)body["system"]![0]!["cache_control"]!["type"]!);
        Assert.Equal("Answer in English.", (string)body["system"]![1]!["text"]!);
        Assert.Equal("ephemeral", (string)body["tools"]![0]!["cache_control"]!["type"]!);
        Assert.Equal(("adaptive", "high"), ((string)body["thinking"]!["type"]!, (string)body["output_config"]!["effort"]!));
        Assert.Equal(AnthropicMessagesExecutor.DefaultMaxOutputTokens, (int)body["max_tokens"]!);

        conversation.AddRange(first.Output);
        conversation.Add(new ToolResult(call.Id, "76"));
        var (second, _) = await RunAsync(executor, Request("claude-test", conversation, "high"));
        Assert.Equal((StopReason.EndTurn, "21 pens and 55 pencils: 76 in total."),
            (second.Stop, Assert.IsType<AssistantMessage>(Assert.Single(second.Output)).Text));
        var messages = fake.Calls[1].Body["messages"]!.AsArray();
        Assert.Equal(["user", "assistant", "user"], messages.Select(message => (string?)message!["role"]));
        Assert.Equal(["thinking", "text", "tool_use"], messages[1]!["content"]!.AsArray().Select(block => (string?)block!["type"]));
        var result = messages[2]!["content"]![0]!;
        Assert.Equal(("tool_result", "toolu_01Add", "ephemeral"),
            ((string)result["type"]!, (string)result["tool_use_id"]!, (string)result["cache_control"]!["type"]!));
    }

    [Fact]
    public void AnthropicMergesParallelResultsAndKeepsThinkingToItsModel()
    {
        var origin = new ModelOrigin(AnthropicMessagesExecutor.ApiName, AnthropicMessagesExecutor.AnthropicEndpoint.ToString(), "claude-a");
        List<ConversationItem> conversation =
        [
            new UserMessage("Two sums"),
            new ReasoningItem("plan", origin, """{"type":"thinking","thinking":"plan","signature":"sig"}"""),
            new ToolCall("t1", "add", """{"a":1,"b":2}"""),
            new ToolCall("t2", "add", "not json"),
            new ToolResult("t1", "3"),
            new ToolResult("t2", "Arguments are not JSON.", true),
            new UserMessage("And now?")
        ];
        var messages = AnthropicMessagesExecutor.Body(Request("claude-b", conversation), origin with { Model = "claude-b" })["messages"]!.AsArray();
        Assert.Equal(["user", "assistant", "user"], messages.Select(message => (string?)message!["role"]));
        Assert.Equal(["tool_use", "tool_use"], messages[1]!["content"]!.AsArray().Select(block => (string?)block!["type"])); // no foreign thinking
        Assert.Empty(messages[1]!["content"]![1]!["input"]!.AsObject());
        Assert.Equal(["tool_result", "tool_result", "text"], messages[2]!["content"]!.AsArray().Select(block => (string?)block!["type"]));
        Assert.True((bool)messages[2]!["content"]![1]!["is_error"]!);

        var schema = AnthropicMessagesExecutor.Schema(JsonNode.Parse("""
            {"anyOf":[{"type":"object","properties":{"path":{"type":"string"}}},{"type":"object","properties":{"url":{"type":"string"}}}]}
            """)!.AsObject());
        Assert.Equal(("object", false), ((string)schema["type"]!, schema.ContainsKey("anyOf")));
        Assert.Equal(["path", "url"], schema["properties"]!.AsObject().Select(property => property.Key));
    }

    [Fact]
    public async Task FailuresAreClassifiedForTheLoop()
    {
        async Task<ModelProviderException> Fails(FakeProvider fake) => await Assert.ThrowsAsync<ModelProviderException>(() =>
            RunAsync(OpenAIResponsesExecutor.WithApiKey(new HttpClient(fake), "sk-test"), Request("gpt-5.4-mini", [new UserMessage("hi")])));

        var unauthorized = await Fails(new FakeProvider().Error(HttpStatusCode.Unauthorized,
            """{"error":{"message":"Incorrect API key provided.","type":"invalid_request_error","code":"invalid_api_key"}}"""));
        Assert.Equal((ProviderFailure.Authentication, 401), (unauthorized.Failure, unauthorized.Status));
        Assert.Contains("Incorrect API key", unauthorized.Message);
        Assert.Equal(ProviderFailure.Invalid, (await Fails(new FakeProvider().Error(HttpStatusCode.NotFound,
            """{"error":{"message":"The model `x` does not exist.","code":"model_not_found"}}"""))).Failure);
        var limited = await Fails(new FakeProvider().Error(HttpStatusCode.TooManyRequests, """{"error":{"message":"Slow down."}}""",
            headers => headers.RetryAfter = new(TimeSpan.FromSeconds(7))));
        Assert.Equal((ProviderFailure.RateLimited, TimeSpan.FromSeconds(7)), (limited.Failure, limited.RetryAfter));
        Assert.Equal(ProviderFailure.QuotaExceeded, (await Fails(new FakeProvider().Error(HttpStatusCode.TooManyRequests,
            """{"error":{"message":"You exceeded your current quota.","code":"insufficient_quota"}}"""))).Failure);
        Assert.Equal(ProviderFailure.ContextOverflow, (await Fails(new FakeProvider().Stream("openai/failed.sse"))).Failure);
        Assert.Equal(ProviderFailure.Transient, (await Fails(new FakeProvider().Error(HttpStatusCode.BadGateway, "<html>bad gateway</html>"))).Failure);

        var overloaded = await Assert.ThrowsAsync<ModelProviderException>(() => RunAsync(
            AnthropicMessagesExecutor.WithApiKey(new HttpClient(new FakeProvider().Stream("anthropic/overloaded.sse")), "k"),
            Request("claude-test", [new UserMessage("hi")])));
        Assert.Equal(ProviderFailure.Transient, overloaded.Failure);
    }

    [Fact]
    public async Task PassingFailuresAreRetriedAndTheirDraftDropped()
    {
        var fake = new FakeProvider().Stream("anthropic/overloaded.sse")
            .Error(HttpStatusCode.TooManyRequests, """{"error":{"message":"Slow down."}}""", headers => headers.RetryAfter = new(TimeSpan.FromSeconds(3)))
            .Stream("anthropic/final-answer.sse");
        var time = new InstantTime();
        var executor = new RetryingTurnExecutor(AnthropicMessagesExecutor.WithApiKey(new HttpClient(fake), "k"), time);
        var (response, deltas) = await RunAsync(executor, Request("claude-test", [new UserMessage("hi")]));
        Assert.Equal("21 pens and 55 pencils: 76 in total.", Assert.IsType<AssistantMessage>(Assert.Single(response.Output)).Text);
        Assert.Equal(3, fake.Calls.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)], time.Waits);
        // "Partial" streamed before the failure, then the restart, then the answer.
        Assert.Equal([TurnDeltaKind.Text, TurnDeltaKind.Restart, TurnDeltaKind.Text, TurnDeltaKind.Text], deltas.Select(delta => delta.Kind));

        var refused = new FakeProvider().Error(HttpStatusCode.Unauthorized, """{"error":{"message":"Bad key.","code":"invalid_api_key"}}""");
        await Assert.ThrowsAsync<ModelProviderException>(() => RunAsync(
            new RetryingTurnExecutor(OpenAIResponsesExecutor.WithApiKey(new HttpClient(refused), "k"), time), Request("m", [new UserMessage("hi")])));
        Assert.Single(refused.Calls); // a wrong key is not asked again
    }

    /// <summary>Runs only with <c>BOSOFA_TEST_OPENAI_KEY_FILE</c> naming a file that holds an OpenAI API key; the key is never
    /// printed. <c>BOSOFA_TEST_OPENAI_MODEL</c> picks the model (gpt-5.4-nano by default).</summary>
    internal sealed class LiveOpenAIFactAttribute : FactAttribute
    {
        public LiveOpenAIFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("BOSOFA_TEST_OPENAI_KEY_FILE") is not { Length: > 0 } file || !File.Exists(file))
                Skip = "Set BOSOFA_TEST_OPENAI_KEY_FILE to a file with an OpenAI API key to call the real API.";
        }
    }

    internal sealed class LiveAnthropicFactAttribute : FactAttribute
    {
        public LiveAnthropicFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("BOSOFA_TEST_ANTHROPIC_KEY_FILE") is not { Length: > 0 } file || !File.Exists(file))
                Skip = "Set BOSOFA_TEST_ANTHROPIC_KEY_FILE to a file with an Anthropic API key to call the real API.";
        }
    }

    /// <summary>The real Messages API: the signed thinking of the first step must be accepted back with the tool's result.</summary>
    [LiveAnthropicFact]
    public async Task LiveAnthropicKeepsThinkingAcrossAToolCall()
    {
        var key = File.ReadAllText(Environment.GetEnvironmentVariable("BOSOFA_TEST_ANTHROPIC_KEY_FILE")!).Trim();
        var model = Environment.GetEnvironmentVariable("BOSOFA_TEST_ANTHROPIC_MODEL") is { Length: > 0 } chosen ? chosen : "claude-opus-5-5";
        using var http = new HttpClient();
        var executor = new RetryingTurnExecutor(AnthropicMessagesExecutor.WithApiKey(http, key), TimeProvider.System);
        List<ConversationItem> conversation = [new UserMessage("Use the add tool to add 1234 and 4321, then answer with the sum only.")];
        var calls = 0;
        for (var step = 0; step < 6; step++)
        {
            var response = await executor.ExecuteAsync(Request(model, conversation), _ => ValueTask.CompletedTask, default);
            conversation.AddRange(response.Output);
            if (response.Stop != StopReason.ToolCalls)
            {
                Assert.True(calls > 0, "The model never called the tool.");
                Assert.Contains("5555", conversation.OfType<AssistantMessage>().Last().Text);
                return;
            }
            foreach (var call in response.Output.OfType<ToolCall>())
            {
                calls++;
                var arguments = JsonNode.Parse(call.Arguments)!;
                conversation.Add(new ToolResult(call.Id, ((int)arguments["a"]! + (int)arguments["b"]!).ToString()));
            }
        }
        Assert.Fail("The model did not finish.");
    }

    internal sealed class LiveOpenCodeGoFactAttribute : FactAttribute
    {
        public LiveOpenCodeGoFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("BOSOFA_TEST_OPENCODE_KEY_FILE") is not { Length: > 0 } file || !File.Exists(file))
                Skip = "Set BOSOFA_TEST_OPENCODE_KEY_FILE to a file with an OpenCode Go API key to call the real service.";
        }
    }

    [LiveOpenCodeGoFact]
    public async Task LiveOpenCodeGoRunsAToolCall()
    {
        var key = File.ReadAllText(Environment.GetEnvironmentVariable("BOSOFA_TEST_OPENCODE_KEY_FILE")!).Trim();
        var model = Environment.GetEnvironmentVariable("BOSOFA_TEST_OPENCODE_MODEL") is { Length: > 0 } chosen ? chosen : "glm-5.3";
        using var http = new HttpClient();
        var executor = new RetryingTurnExecutor(OpenCodeGo.ExecutorFor(http, key, model, null), TimeProvider.System);
        List<ConversationItem> conversation = [new UserMessage("Use the add tool to add 1234 and 4321, then answer with the sum only.")];
        for (var step = 0; step < 6; step++)
        {
            var response = await executor.ExecuteAsync(Request(model, conversation), _ => ValueTask.CompletedTask, default);
            conversation.AddRange(response.Output);
            if (response.Stop != StopReason.ToolCalls)
            {
                Assert.Contains(conversation, item => item is ToolCall);
                Assert.Contains("5555", conversation.OfType<AssistantMessage>().Last().Text);
                return;
            }
            foreach (var call in response.Output.OfType<ToolCall>())
            {
                var arguments = JsonNode.Parse(call.Arguments)!;
                conversation.Add(new ToolResult(call.Id, ((int)arguments["a"]! + (int)arguments["b"]!).ToString()));
            }
        }
        Assert.Fail("The model did not finish.");
    }

    [Theory]
    [InlineData("glm-5.3", false, false)]
    [InlineData("kimi-k3", false, false)]
    [InlineData("minimax-m3.1-flash", false, false)]
    [InlineData("minimax-m3", true, false)]
    [InlineData("qwen3.8-max", true, false)]
    [InlineData("claude-haiku-5-5", true, false)]
    [InlineData("gpt-6-luna", false, true)]
    [InlineData("grok-4.7", false, true)]
    public void OpenCodeGoCallsEachModelOnItsOwnApi(string model, bool messages, bool nativeResponses)
    {
        Assert.Equal(messages, OpenCodeGo.UsesMessages(model));
        Assert.Equal(nativeResponses, OpenCodeGo.IsNativeResponses(model));
    }

    [Theory]
    [InlineData("claude-opus-5-5", true)]
    [InlineData("claude-sonnet-4-6", true)]
    [InlineData("claude-opus-5", true)]
    [InlineData("claude-haiku-4-5", false)]
    [InlineData("claude-opus-4-5-20251101", false)]
    [InlineData("claude-sonnet-4-20250514", false)]
    public void AdaptiveThinkingIsForTheModelsThatHaveIt(string model, bool adaptive)
    {
        var origin = new ModelOrigin(AnthropicMessagesExecutor.ApiName, "e", model);
        var plain = AnthropicMessagesExecutor.Body(Request(model, [new UserMessage("hi")]), origin);
        Assert.Equal(adaptive, plain["thinking"] != null);
        if (adaptive) Assert.Equal(("adaptive", "summarized"), ((string)plain["thinking"]!["type"]!, (string)plain["thinking"]!["display"]!));
        Assert.Null(plain["output_config"]); // the model's own default, until a session can choose
        Assert.Equal(adaptive, AnthropicMessagesExecutor.Body(Request(model, [new UserMessage("hi")], "high"), origin)["output_config"] != null);
    }

    [Fact]
    public void ToolArgumentsStreamEagerlyOnlyWhereAsked()
    {
        var origin = new ModelOrigin(AnthropicMessagesExecutor.ApiName, "e", "claude-opus-5-5");
        var tools = Request("claude-opus-5-5", [new UserMessage("hi")]);
        Assert.Null(AnthropicMessagesExecutor.Body(tools, origin)["tools"]![0]!["eager_input_streaming"]);
        Assert.True((bool)AnthropicMessagesExecutor.Body(tools, origin, eagerToolInput: true)["tools"]![0]!["eager_input_streaming"]!);
    }

    [LiveOpenAIFact]
    public async Task LiveOpenAIRunsAToolCallRoundTrip()
    {
        var key = File.ReadAllText(Environment.GetEnvironmentVariable("BOSOFA_TEST_OPENAI_KEY_FILE")!).Trim();
        var model = Environment.GetEnvironmentVariable("BOSOFA_TEST_OPENAI_MODEL") is { Length: > 0 } chosen ? chosen : "gpt-5.4-nano";
        using var http = new HttpClient();
        var executor = new RetryingTurnExecutor(OpenAIResponsesExecutor.WithApiKey(http, key), TimeProvider.System);
        List<ConversationItem> conversation = [new UserMessage("Use the add tool to add 1234 and 4321, then answer with the sum only.")];

        // A small loop as the agent will run it: answer every call (a model may make several at once) until it is done.
        var calls = 0;
        TurnResponse response;
        List<TurnDelta> deltas;
        while (true)
        {
            (response, deltas) = await RunAsync(executor, Request(model, conversation, "low"));
            Assert.True(response.Usage.InputTokens > 0 && response.Usage.OutputTokens > 0);
            conversation.AddRange(response.Output);
            if (response.Stop != StopReason.ToolCalls) break;
            foreach (var call in response.Output.OfType<ToolCall>())
            {
                var arguments = JsonNode.Parse(call.Arguments)!;
                var sum = (long)arguments["a"]! + (long)arguments["b"]!;
                conversation.Add(new ToolResult(call.Id, sum.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                calls++;
            }
            Assert.True(calls < 6, "The model kept calling the tool.");
        }
        Assert.True(calls > 0, "The model answered without the tool.");
        Assert.Equal(StopReason.EndTurn, response.Stop);
        Assert.Contains("5555", string.Concat(response.Output.OfType<AssistantMessage>().Select(message => message.Text)));
        Assert.Contains(deltas, delta => delta.Kind == TurnDeltaKind.Text);
    }
}
