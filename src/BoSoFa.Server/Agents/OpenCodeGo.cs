using System.Text.Json;
using System.Text.Json.Nodes;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Agents;

/// <summary>
/// OpenCode Go (docs/server-agent.md §5.3): many models behind one key. OpenCode translates every text model to either API, and each is
/// called on the one it speaks natively where we have a client for that. The model list does not say which that is, so it comes from
/// the table in OpenCode's docs.
/// </summary>
public static class OpenCodeGo
{
    public const string DefaultBaseUrl = "https://opencode.ai/inference/go";

    static string Base(string? configured) => (string.IsNullOrEmpty(configured) ? DefaultBaseUrl : configured).TrimEnd('/');

    /// <summary>Claude, Qwen and MiniMax M3 speak Messages. MiniMax M3.1 Flash does not, so the id is matched whole.</summary>
    public static bool UsesMessages(string model) => model.StartsWith("claude-", StringComparison.Ordinal) ||
                                                     model.StartsWith("qwen", StringComparison.Ordinal) || model == "minimax-m3";

    /// <summary>These speak Responses themselves, so asking for their encrypted reasoning makes sense; the rest are translated.</summary>
    public static bool IsNativeResponses(string model) => model.StartsWith("gpt-", StringComparison.Ordinal) || model.StartsWith("grok-", StringComparison.Ordinal) ||
                                                          model.StartsWith("muse-spark-", StringComparison.Ordinal) || model.StartsWith("crouton-", StringComparison.Ordinal);

    /// <summary>OpenAI's models are trained on <c>apply_patch</c>.</summary>
    public static bool OffersPatch(string model) => model.StartsWith("gpt-", StringComparison.Ordinal);

    public static IModelTurnExecutor ExecutorFor(HttpClient http, string apiKey, string model, string? baseUrl) => UsesMessages(model)
        ? AnthropicMessagesExecutor.ForOpenCodeGo(http, apiKey, new Uri(Base(baseUrl) + "/anthropic/v1/messages"))
        : OpenAIResponsesExecutor.ForOpenCodeGo(http, apiKey, new Uri(Base(baseUrl) + "/openai/v1/responses"), IsNativeResponses(model));

    /// <summary>The models that can run an agent: chat models with tools. The list is public, so no key goes with the request.</summary>
    /// <exception cref="HttpRequestException">OpenCode could not be asked.</exception>
    /// <exception cref="JsonException">It answered something else.</exception>
    public static async Task<AgentModelDto[]> ListModelsAsync(HttpClient http, string? baseUrl, CancellationToken cancellation)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, Base(baseUrl) + "/v1/models");
        message.Headers.TryAddWithoutValidation("User-Agent", ProviderHttp.UserAgent);
        using var response = await http.SendAsync(message, cancellation);
        response.EnsureSuccessStatusCode();
        var models = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation))?["data"] as JsonArray ?? [];
        return models.OfType<JsonObject>()
            .Where(model => Str(model["type"]) == "chat" && Flag(model["capabilities"]?["tools"]?["supported"]) && Str(model["id"]) is { Length: > 0 })
            .Select(model => new AgentModelDto(Str(model["id"])!, Str(model["name"]) ?? Str(model["id"])!, Long(model["limit"]?["context"]), Long(model["limit"]?["output"])))
            .OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    static bool Flag(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
    static long? Long(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;
}
