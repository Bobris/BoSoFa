using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Agents;

/// <summary>
/// One call of the OpenAI Responses API (docs/server-agent.md §5.1), as CodeAlta makes it: the whole conversation every time
/// with <c>store: false</c> and no <c>previous_response_id</c>, encrypted reasoning asked for and replayed to the same
/// origin only, function tools, streamed. Output items are replayed without their <c>id</c> and <c>status</c>: with nothing
/// stored, the API cannot resolve an id.
/// </summary>
public sealed class OpenAIResponsesExecutor(HttpClient http, Uri endpoint, Action<HttpRequestMessage, TurnRequest> authorize,
    string provider = "OpenAI", bool includeEncryptedReasoning = true, bool hostedSearch = false) : IModelTurnExecutor
{
    public const string ApiName = "openai-responses";
    public static readonly Uri OpenAIEndpoint = new("https://api.openai.com/v1/responses");
    public static readonly Uri OpenCodeGoEndpoint = new("https://opencode.ai/inference/go/openai/v1/responses");

    /// <summary>The OpenAI API with an API key (<c>Authorization: Bearer</c>).</summary>
    public static OpenAIResponsesExecutor WithApiKey(HttpClient http, string apiKey, Uri? endpoint = null) =>
        new(http, endpoint ?? OpenAIEndpoint, (message, _) => message.Headers.Authorization = new("Bearer", apiKey), hostedSearch: true);

    /// <summary>OpenCode Go: its key, and the stable session id it asks for in <c>x-opencode-session</c>.</summary>
    public static OpenAIResponsesExecutor ForOpenCodeGo(HttpClient http, string apiKey, Uri? endpoint = null, bool includeEncryptedReasoning = true) =>
        new(http, endpoint ?? OpenCodeGoEndpoint, (message, request) =>
        {
            message.Headers.Authorization = new("Bearer", apiKey);
            message.Headers.TryAddWithoutValidation("x-opencode-session", request.SessionId);
        }, "OpenCode Go", includeEncryptedReasoning);

    public string Api => ApiName;
    public string Endpoint { get; } = endpoint.ToString();

    /// <summary>This model does not take the hosted search; it simply goes without.</summary>
    bool _searchRefused;

    public async Task<TurnResponse> ExecuteAsync(TurnRequest request, Func<TurnDelta, ValueTask> onDelta, CancellationToken cancellation)
    {
        try { return await CallAsync(request, onDelta, cancellation); }
        catch (ModelProviderException problem) when (hostedSearch && !_searchRefused && problem is { Status: 400 } && problem.Message.Contains("web_search", StringComparison.OrdinalIgnoreCase))
        {
            _searchRefused = true;
            return await CallAsync(request, onDelta, cancellation);
        }
    }

    async Task<TurnResponse> CallAsync(TurnRequest request, Func<TurnDelta, ValueTask> onDelta, CancellationToken cancellation)
    {
        var origin = new ModelOrigin(Api, Endpoint, request.Model);
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(Body(request, origin).ToJsonString(), Encoding.UTF8, "application/json")
        };
        message.Headers.Accept.ParseAdd("text/event-stream");
        authorize(message, request);
        using var response = await ProviderHttp.SendAsync(http, message, provider, cancellation);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        return await ReadAsync(stream, origin, onDelta, cancellation);
    }

    /// <summary>The request body; public for tests.</summary>
    public JsonObject Body(TurnRequest request, ModelOrigin origin)
    {
        var input = new JsonArray();
        // This session's instructions come first in the input rather than in "instructions", so the instructions shared
        // by many sessions stay one cached prefix.
        if (!string.IsNullOrWhiteSpace(request.SessionInstructions))
            input.Add((JsonNode)Message("developer", "input_text", request.SessionInstructions));
        foreach (var item in request.Conversation)
            if (InputOf(request, item, origin) is { } entry) input.Add((JsonNode)entry);
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["instructions"] = request.Instructions,
            ["input"] = input,
            ["stream"] = true,
            ["store"] = false
        };
        var tools = new JsonArray(request.Tools.Select(tool => (JsonNode)new JsonObject
        {
            ["type"] = "function", ["name"] = tool.Name, ["description"] = tool.Description,
            ["parameters"] = tool.InputSchema.DeepClone(), ["strict"] = false
        }).ToArray());
        if (hostedSearch && !_searchRefused) tools.Add((JsonNode)new JsonObject { ["type"] = "web_search" });
        if (tools.Count > 0) body["tools"] = tools;
        if (request.NoToolUse && tools.Count > 0) body["tool_choice"] = "none";
        if (request.ReasoningEffort != null) body["reasoning"] = new JsonObject { ["effort"] = request.ReasoningEffort, ["summary"] = "auto" };
        if (includeEncryptedReasoning) body["include"] = new JsonArray("reasoning.encrypted_content");
        if (request.MaxOutputTokens is { } limit) body["max_output_tokens"] = limit;
        return body;
    }

    static JsonObject Message(string role, string type, string text) => new()
    {
        ["type"] = "message", ["role"] = role, ["content"] = new JsonArray(new JsonObject { ["type"] = type, ["text"] = text })
    };

    /// <summary>An item as input: the provider's own form for the same origin, else rebuilt from what every API shares.
    /// Reasoning from another origin is left out, since only its own model can read it.</summary>
    static JsonObject? InputOf(TurnRequest request, ConversationItem item, ModelOrigin origin) => item switch
    {
        UserMessage { Images.Length: > 0 } user => new JsonObject
        {
            ["type"] = "message", ["role"] = "user",
            ["content"] = new JsonArray([..user.Images.Select(image => (JsonNode)ImagePart(request, image)), new JsonObject { ["type"] = "input_text", ["text"] = user.Text }])
        },
        UserMessage user => Message("user", "input_text", user.Text),
        AssistantMessage assistant => Replay(assistant.Origin, assistant.ProviderData, origin) ??
                                      Message("assistant", "output_text", assistant.Text),
        ReasoningItem reasoning => Replay(reasoning.Origin, reasoning.ProviderData, origin),
        ToolCall call => Replay(call.Origin, call.ProviderData, origin) ?? new JsonObject
        {
            ["type"] = "function_call", ["call_id"] = call.Id, ["name"] = call.Name, ["arguments"] = call.Arguments
        },
        ToolResult { Images.Length: > 0 } result => new JsonObject
        {
            ["type"] = "function_call_output", ["call_id"] = result.CallId,
            ["output"] = new JsonArray([new JsonObject { ["type"] = "input_text", ["text"] = result.Text }, ..result.Images.Select(image => (JsonNode)ImagePart(request, image))])
        },
        ToolResult result => new JsonObject { ["type"] = "function_call_output", ["call_id"] = result.CallId, ["output"] = result.Text },
        _ => null
    };

    static JsonObject ImagePart(TurnRequest request, PromptImage image) => request.DataOf(image) is { } data
        ? new() { ["type"] = "input_image", ["image_url"] = $"data:{image.MediaType};base64,{data}", ["detail"] = "auto" }
        : new() { ["type"] = "input_text", ["text"] = TurnRequest.MissingImage };

    static JsonObject? Replay(ModelOrigin? from, string? data, ModelOrigin to) =>
        from == to ? ProviderHttp.ParseObject(data) : null;

    async Task<TurnResponse> ReadAsync(Stream stream, ModelOrigin origin, Func<TurnDelta, ValueTask> onDelta, CancellationToken cancellation)
    {
        var done = new List<JsonObject>();
        await foreach (var item in SseParser.Create(stream).EnumerateAsync(cancellation))
        {
            if (item.Data is not { Length: > 0 } data || data == "[DONE]") continue;
            JsonObject? eventData;
            try { eventData = JsonNode.Parse(data) as JsonObject; }
            catch (JsonException) { continue; }
            if (eventData == null) continue;
            switch (Str(eventData["type"]) ?? item.EventType)
            {
                case "response.output_text.delta":
                case "response.refusal.delta":
                    await onDelta(new(TurnDeltaKind.Text, Str(eventData["delta"]) ?? ""));
                    break;
                case "response.reasoning_summary_text.delta":
                case "response.reasoning_text.delta":
                    await onDelta(new(TurnDeltaKind.Reasoning, Str(eventData["delta"]) ?? ""));
                    break;
                case "response.function_call_arguments.delta":
                    await onDelta(new(TurnDeltaKind.ToolArguments, Str(eventData["delta"]) ?? ""));
                    break;
                case "response.output_item.done":
                    if (eventData["item"] is JsonObject finished) done.Add(finished);
                    break;
                case "response.completed":
                case "response.incomplete":
                    return Finish(eventData["response"] as JsonObject, done, origin);
                case "response.failed":
                    var failure = eventData["response"]?["error"];
                    throw Failure(Str(failure?["code"]), Str(failure?["message"]) ?? "The response failed.");
                case "error":
                    throw Failure(Str(eventData["code"]) ?? Str(eventData["error"]?["code"]),
                        Str(eventData["message"]) ?? Str(eventData["error"]?["message"]) ?? "The stream reported an error.");
            }
        }
        throw new ModelProviderException(ProviderFailure.Transient, $"{provider}'s stream ended before the answer was complete.");
    }

    ModelProviderException Failure(string? code, string message) =>
        new(ProviderHttp.Classify(null, code, message), $"{provider}: {message}");

    TurnResponse Finish(JsonObject? response, List<JsonObject> done, ModelOrigin origin)
    {
        // The final response lists every output item; the items finished during the stream are the same, in case it does not.
        var items = response?["output"] is JsonArray { Count: > 0 } output ? output.OfType<JsonObject>().ToList() : done;
        var conversation = new List<ConversationItem>();
        var refused = false;
        foreach (var item in items)
        {
            var data = ForReplay(item);
            switch (Str(item["type"]))
            {
                case "message":
                    var text = new StringBuilder();
                    foreach (var part in (item["content"] as JsonArray ?? []).OfType<JsonObject>())
                        if (Str(part["type"]) == "output_text") text.Append(Str(part["text"]));
                        else if (Str(part["type"]) == "refusal") { text.Append(Str(part["refusal"])); refused = true; }
                    conversation.Add(new AssistantMessage(text.ToString(), origin, data));
                    break;
                case "reasoning":
                    var summary = (item["summary"] as JsonArray ?? []).OfType<JsonObject>().Select(part => Str(part["text"]))
                        .Concat((item["content"] as JsonArray ?? []).OfType<JsonObject>().Select(part => Str(part["text"])))
                        .Where(part => !string.IsNullOrEmpty(part));
                    conversation.Add(new ReasoningItem(string.Join("\n\n", summary), origin, data));
                    break;
                case "web_search_call":
                    // Kept like reasoning: the reasoning before it must not go back without it.
                    conversation.Add(new ReasoningItem(Str(item["action"]?["query"]) is { Length: > 0 } query ? $"Searched the web: {query}" : "Searched the web", origin, data));
                    break;
                case "function_call":
                    conversation.Add(new ToolCall(Str(item["call_id"]) ?? "", Str(item["name"]) ?? "", Str(item["arguments"]) ?? "",
                        origin, data));
                    break;
            }
        }
        var usage = response?["usage"];
        var turnUsage = new TurnUsage(Long(usage?["input_tokens"]), Long(usage?["input_tokens_details"]?["cached_tokens"]),
            Long(usage?["input_tokens_details"]?["cache_write_tokens"]), Long(usage?["output_tokens"]),
            Long(usage?["output_tokens_details"]?["reasoning_tokens"]));
        var stop = Str(response?["status"]) == "incomplete"
            ? Str(response?["incomplete_details"]?["reason"]) == "content_filter" ? StopReason.Refused : StopReason.MaxTokens
            : refused ? StopReason.Refused
            : conversation.OfType<ToolCall>().Any() ? StopReason.ToolCalls
            : StopReason.EndTurn;
        return new(conversation, turnUsage, stop);
    }

    /// <summary>The item as it may be sent back: everything but <c>id</c> and <c>status</c>.</summary>
    static string ForReplay(JsonObject item)
    {
        var copy = (JsonObject)item.DeepClone();
        copy.Remove("id");
        copy.Remove("status");
        return copy.ToJsonString();
    }

    static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    static long Long(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : 0;
}
