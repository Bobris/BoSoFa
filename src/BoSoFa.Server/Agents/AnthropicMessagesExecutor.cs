using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BoSoFa.Server.Agents;

/// <summary>
/// One call of the Anthropic Messages API (docs/server-agent.md §5.4), streamed. Consecutive items of one role become one
/// message, so parallel tool results share a user message. Signed thinking blocks go back only to the same origin, and the loop's
/// history is append-only, which is what those signatures check. Three cache breakpoints: the shared system prompt, the last tool,
/// and the end of the conversation so far.
/// </summary>
/// <param name="eagerToolInput">Stream tool arguments as they are written. The API then no longer checks them, so the loop's own
/// handling of bad arguments applies; endpoints that only imitate the API may refuse the field.</param>
public sealed partial class AnthropicMessagesExecutor(HttpClient http, Uri endpoint, Action<HttpRequestMessage, TurnRequest> authorize,
    string provider = "Anthropic", bool eagerToolInput = false, int defaultMaxOutputTokens = AnthropicMessagesExecutor.DefaultMaxOutputTokens) : IModelTurnExecutor
{
    public const string ApiName = "anthropic-messages";

    /// <summary>The version every Anthropic-compatible endpoint knows; the Claude API takes it too.</summary>
    public const string ApiVersion = "2023-06-01";

    /// <summary>The Messages API needs an output limit. Streaming has no timeout to fear, so the model gets room; this fits every current one.</summary>
    public const int DefaultMaxOutputTokens = 64000;

    /// <summary>For other models behind the same API, whose limits are lower and not known yet.</summary>
    public const int CompatibleMaxOutputTokens = 16384;

    public static readonly Uri AnthropicEndpoint = new("https://api.anthropic.com/v1/messages");
    public static readonly Uri OpenCodeGoEndpoint = new("https://opencode.ai/inference/go/anthropic/v1/messages");

    public static AnthropicMessagesExecutor WithApiKey(HttpClient http, string apiKey, Uri? endpoint = null) =>
        new(http, endpoint ?? AnthropicEndpoint, (message, _) => message.Headers.TryAddWithoutValidation("x-api-key", apiKey), eagerToolInput: true);

    /// <summary>OpenCode Go's Messages route: its key, and the stable session id it asks for in <c>x-opencode-session</c>.</summary>
    public static AnthropicMessagesExecutor ForOpenCodeGo(HttpClient http, string apiKey, Uri? endpoint = null) =>
        new(http, endpoint ?? OpenCodeGoEndpoint, (message, request) =>
        {
            message.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            message.Headers.TryAddWithoutValidation("x-opencode-session", request.SessionId);
        }, "OpenCode Go", defaultMaxOutputTokens: CompatibleMaxOutputTokens);

    public string Api => ApiName;
    public string Endpoint { get; } = endpoint.ToString();

    /// <summary>A signature was refused once in this conversation, so its thinking is no longer sent back.</summary>
    bool _withoutThinking;

    public async Task<TurnResponse> ExecuteAsync(TurnRequest request, Func<TurnDelta, ValueTask> onDelta, CancellationToken cancellation)
    {
        try { return await CallAsync(request, onDelta, cancellation); }
        catch (ModelProviderException problem) when (!_withoutThinking && problem is { Status: 400 } && problem.Message.Contains("signature", StringComparison.OrdinalIgnoreCase) &&
                                                     request.Conversation.OfType<ReasoningItem>().Any())
        {
            // A changed system prompt or tool list after a restart invalidates them. The conversation still holds without its thinking.
            _withoutThinking = true;
            return await CallAsync(request, onDelta, cancellation);
        }
    }

    async Task<TurnResponse> CallAsync(TurnRequest request, Func<TurnDelta, ValueTask> onDelta, CancellationToken cancellation)
    {
        var origin = new ModelOrigin(Api, Endpoint, request.Model);
        var body = Body(request, origin, !_withoutThinking, eagerToolInput, defaultMaxOutputTokens);
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        message.Headers.Accept.ParseAdd("text/event-stream");
        message.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        authorize(message, request);
        using var response = await ProviderHttp.SendAsync(http, message, provider, cancellation);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        return await ReadAsync(stream, origin, onDelta, cancellation);
    }

    static JsonObject Ephemeral() => new() { ["type"] = "ephemeral" };

    /// <summary>The request body; public for tests.</summary>
    public static JsonObject Body(TurnRequest request, ModelOrigin origin, bool replayThinking = true, bool eagerToolInput = false,
        int defaultMaxOutputTokens = DefaultMaxOutputTokens)
    {
        var system = new JsonArray((JsonNode)new JsonObject
        {
            ["type"] = "text", ["text"] = request.Instructions, ["cache_control"] = Ephemeral()
        });
        if (!string.IsNullOrWhiteSpace(request.SessionInstructions))
            system.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = request.SessionInstructions });
        var messages = Messages(request.Conversation, origin, replayThinking);
        if (messages.LastOrDefault()?["content"] is JsonArray { Count: > 0 } last && last[^1] is JsonObject lastBlock)
            lastBlock["cache_control"] = Ephemeral();
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["max_tokens"] = request.MaxOutputTokens ?? defaultMaxOutputTokens,
            ["system"] = system,
            ["messages"] = messages,
            ["stream"] = true
        };
        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray(request.Tools.Select(tool =>
            {
                var definition = new JsonObject { ["name"] = tool.Name, ["description"] = tool.Description, ["input_schema"] = Schema(tool.InputSchema) };
                if (eagerToolInput) definition["eager_input_streaming"] = true;
                return (JsonNode)definition;
            }).ToArray());
            ((JsonObject)tools[^1]!)["cache_control"] = Ephemeral();
            body["tools"] = tools;
        }
        var generation = GenerationOf(request.Model);
        if (generation == ClaudeGeneration.Adaptive || (generation == ClaudeGeneration.NotClaude && request.ReasoningEffort != null))
        {
            // Without "summarized", current models send empty thinking and a person sees only a long pause.
            body["thinking"] = new JsonObject { ["type"] = "adaptive", ["display"] = "summarized" };
            if (request.ReasoningEffort != null) body["output_config"] = new JsonObject { ["effort"] = request.ReasoningEffort };
        }
        return body;
    }

    enum ClaudeGeneration
    {
        NotClaude,

        /// <summary>Before the 4.6 models: no adaptive thinking and no effort.</summary>
        Older,
        Adaptive
    }

    static ClaudeGeneration GenerationOf(string model)
    {
        if (ClaudeModel().Match(model) is not { Success: true } match) return ClaudeGeneration.NotClaude;
        var major = int.Parse(match.Groups[1].ValueSpan);
        var minor = match.Groups[2].Success ? int.Parse(match.Groups[2].ValueSpan) : 0;
        return major > 4 || (major == 4 && minor >= 6) ? ClaudeGeneration.Adaptive : ClaudeGeneration.Older;
    }

    [GeneratedRegex(@"^claude-[a-z]+-(\d+)(?:-(\d{1,2}))?(?:-|$)")]
    private static partial Regex ClaudeModel();

    /// <summary>The API refuses a root-level <c>anyOf</c>/<c>oneOf</c>; their object branches are merged into one object whose
    /// properties are all optional, as CodeAlta does.</summary>
    public static JsonObject Schema(JsonObject schema)
    {
        var copy = (JsonObject)schema.DeepClone();
        foreach (var key in new[] { "anyOf", "oneOf" })
        {
            if (copy[key] is not JsonArray choices) continue;
            copy.Remove(key);
            var properties = copy["properties"] as JsonObject ?? new JsonObject();
            foreach (var choice in choices.OfType<JsonObject>())
                if (choice["properties"] is JsonObject choiceProperties)
                    foreach (var (name, value) in choiceProperties)
                        if (!properties.ContainsKey(name)) properties[name] = value?.DeepClone();
            copy["type"] = "object";
            copy["properties"] = properties;
        }
        return copy;
    }

    static JsonArray Messages(IReadOnlyList<ConversationItem> conversation, ModelOrigin origin, bool replayThinking)
    {
        var messages = new JsonArray();
        JsonArray? content = null;
        string? role = null;
        void Add(string blockRole, JsonObject block)
        {
            if (blockRole != role)
            {
                content = new JsonArray();
                messages.Add((JsonNode)new JsonObject { ["role"] = blockRole, ["content"] = content });
                role = blockRole;
            }
            content!.Add((JsonNode)block);
        }
        foreach (var item in conversation)
            switch (item)
            {
                case UserMessage user:
                    Add("user", new JsonObject { ["type"] = "text", ["text"] = user.Text });
                    break;
                case ToolResult result:
                    Add("user", new JsonObject
                    {
                        ["type"] = "tool_result", ["tool_use_id"] = result.CallId, ["content"] = result.Text, ["is_error"] = result.IsError
                    });
                    break;
                case AssistantMessage { Text.Length: > 0 } assistant:
                    Add("assistant", new JsonObject { ["type"] = "text", ["text"] = assistant.Text });
                    break;
                case ReasoningItem reasoning when replayThinking && reasoning.Origin == origin && ProviderHttp.ParseObject(reasoning.ProviderData) is { } block:
                    Add("assistant", block);
                    break;
                case ToolCall call:
                    Add("assistant", new JsonObject
                    {
                        ["type"] = "tool_use", ["id"] = call.Id, ["name"] = call.Name, ["input"] = Input(call.Arguments)
                    });
                    break;
            }
        return messages;
    }

    /// <summary>The model's arguments as an object; arguments that do not parse are sent as an empty object, since the
    /// API would refuse the whole conversation otherwise.</summary>
    static JsonObject Input(string arguments)
    {
        try { return JsonNode.Parse(arguments) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    sealed class Block
    {
        public string Type = "";
        public string? Id, Name, Data;
        public readonly StringBuilder Text = new(), Signature = new(), Json = new();
    }

    async Task<TurnResponse> ReadAsync(Stream stream, ModelOrigin origin, Func<TurnDelta, ValueTask> onDelta, CancellationToken cancellation)
    {
        var blocks = new SortedDictionary<int, Block>();
        long input = 0, cacheRead = 0, cacheWrite = 0, output = 0;
        string? stopReason = null, stopDetail = null;
        await foreach (var item in SseParser.Create(stream).EnumerateAsync(cancellation))
        {
            if (item.Data is not { Length: > 0 } data) continue;
            JsonObject? eventData;
            try { eventData = JsonNode.Parse(data) as JsonObject; }
            catch (JsonException) { continue; }
            if (eventData == null) continue;
            switch (Str(eventData["type"]) ?? item.EventType)
            {
                case "message_start":
                    var usage = eventData["message"]?["usage"];
                    (input, cacheRead, cacheWrite, output) = (Long(usage?["input_tokens"]), Long(usage?["cache_read_input_tokens"]),
                        Long(usage?["cache_creation_input_tokens"]), Long(usage?["output_tokens"]));
                    break;
                case "content_block_start":
                    var start = eventData["content_block"];
                    var block = new Block
                    {
                        Type = Str(start?["type"]) ?? "", Id = Str(start?["id"]), Name = Str(start?["name"]), Data = Str(start?["data"])
                    };
                    block.Text.Append(Str(start?["text"]) ?? Str(start?["thinking"]));
                    blocks[(int)Long(eventData["index"])] = block;
                    break;
                case "content_block_delta":
                    if (!blocks.TryGetValue((int)Long(eventData["index"]), out var target)) break;
                    var delta = eventData["delta"];
                    switch (Str(delta?["type"]))
                    {
                        case "text_delta":
                            target.Text.Append(Str(delta?["text"]));
                            await onDelta(new(TurnDeltaKind.Text, Str(delta?["text"]) ?? ""));
                            break;
                        case "thinking_delta":
                            target.Text.Append(Str(delta?["thinking"]));
                            await onDelta(new(TurnDeltaKind.Reasoning, Str(delta?["thinking"]) ?? ""));
                            break;
                        case "signature_delta":
                            target.Signature.Append(Str(delta?["signature"]));
                            break;
                        case "input_json_delta":
                            target.Json.Append(Str(delta?["partial_json"]));
                            await onDelta(new(TurnDeltaKind.ToolArguments, Str(delta?["partial_json"]) ?? ""));
                            break;
                    }
                    break;
                case "message_delta":
                    stopReason = Str(eventData["delta"]?["stop_reason"]) ?? stopReason;
                    stopDetail = Str(eventData["delta"]?["stop_details"]?["explanation"]) ?? Str(eventData["delta"]?["stop_details"]?["category"]) ?? stopDetail;
                    if (eventData["usage"]?["output_tokens"] is { } outputTokens) output = Long(outputTokens);
                    break;
                case "message_stop":
                    return Finish(blocks.Values, origin, new(input + cacheRead + cacheWrite, cacheRead, cacheWrite, output, 0), stopReason, stopDetail);
                case "error":
                    var error = eventData["error"];
                    var message = Str(error?["message"]) ?? "The stream reported an error.";
                    throw new ModelProviderException(ProviderHttp.Classify(null, Str(error?["type"]), message), $"{provider}: {message}");
            }
        }
        throw new ModelProviderException(ProviderFailure.Transient, $"{provider}'s stream ended before the answer was complete.");
    }

    static TurnResponse Finish(IEnumerable<Block> blocks, ModelOrigin origin, TurnUsage usage, string? stopReason, string? stopDetail)
    {
        var conversation = new List<ConversationItem>();
        foreach (var block in blocks)
            switch (block.Type)
            {
                case "text":
                    conversation.Add(new AssistantMessage(block.Text.ToString(), origin));
                    break;
                case "thinking":
                    conversation.Add(new ReasoningItem(block.Text.ToString(), origin, new JsonObject
                    {
                        ["type"] = "thinking", ["thinking"] = block.Text.ToString(), ["signature"] = block.Signature.ToString()
                    }.ToJsonString()));
                    break;
                case "redacted_thinking":
                    conversation.Add(new ReasoningItem("", origin,
                        new JsonObject { ["type"] = "redacted_thinking", ["data"] = block.Data }.ToJsonString()));
                    break;
                case "tool_use":
                    conversation.Add(new ToolCall(block.Id ?? "", block.Name ?? "", block.Json.Length > 0 ? block.Json.ToString() : "{}",
                        origin));
                    break;
            }
        var stop = stopReason switch
        {
            "tool_use" => StopReason.ToolCalls,
            "max_tokens" or "model_context_window_exceeded" => StopReason.MaxTokens,
            "refusal" => StopReason.Refused,
            _ => conversation.OfType<ToolCall>().Any() ? StopReason.ToolCalls : StopReason.EndTurn
        };
        return new(conversation, usage, stop, stop == StopReason.Refused ? stopDetail : null);
    }

    static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    static long Long(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : 0;
}
