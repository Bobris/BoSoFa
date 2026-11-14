using System.Text.Json;
using System.Text.Json.Nodes;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Agents;

/// <summary>
/// OpenCode Zen (pay as you go) and OpenCode Go (the subscription), docs/server-agent.md §5.3: many models behind one key. OpenCode
/// translates every text model to either API, and each is called on the one it speaks natively where we have a client for that. The
/// model list does not say which that is, so it comes from the tables in OpenCode's docs, which differ between the two. Both ask for
/// the session in <c>x-opencode-session</c>, and neither serves its free models outside OpenCode's own app.
/// </summary>
public sealed class OpenCodeGateway
{
    /// <summary>Pay as you go, billed to the balance of the key's workspace.</summary>
    public static readonly OpenCodeGateway Zen = new("OpenCode Zen", "https://opencode.ai/inference",
        // The Zen docs put Qwen3.8 Max on Chat Completions, unlike Go; it still works on Messages, translated.
        model => model.StartsWith("claude-", StringComparison.Ordinal) || model.StartsWith("qwen", StringComparison.Ordinal) && model != "qwen3.8-max",
        model => model.StartsWith("gpt-", StringComparison.Ordinal) || model.StartsWith("grok-", StringComparison.Ordinal) ||
                 model.StartsWith("muse-", StringComparison.Ordinal));

    /// <summary>The subscription: a key of a single-member workspace, so always a person's own account.</summary>
    public static readonly OpenCodeGateway Go = new("OpenCode Go", "https://opencode.ai/inference/go",
        // Claude, Qwen and MiniMax M3 speak Messages. MiniMax M3.1 Flash does not, so the id is matched whole.
        model => model.StartsWith("claude-", StringComparison.Ordinal) || model.StartsWith("qwen", StringComparison.Ordinal) || model == "minimax-m3",
        model => model.StartsWith("gpt-", StringComparison.Ordinal) || model.StartsWith("grok-", StringComparison.Ordinal) ||
                 model.StartsWith("muse-spark-", StringComparison.Ordinal) || model.StartsWith("crouton-", StringComparison.Ordinal));

    readonly Func<string, bool> _usesMessages;
    readonly Func<string, bool> _isNativeResponses;

    OpenCodeGateway(string name, string defaultBaseUrl, Func<string, bool> usesMessages, Func<string, bool> isNativeResponses)
    {
        Name = name;
        DefaultBaseUrl = defaultBaseUrl;
        _usesMessages = usesMessages;
        _isNativeResponses = isNativeResponses;
    }

    /// <summary>How errors and the provider of a model name it.</summary>
    public string Name { get; }

    /// <summary>Everything else hangs below it: <c>/openai/v1/responses</c>, <c>/anthropic/v1/messages</c> and <c>/v1/models</c>.</summary>
    public string DefaultBaseUrl { get; }

    /// <summary>The gateway an account kind calls, or null for one that is not OpenCode's.</summary>
    public static OpenCodeGateway? Of(AccountKind kind) => kind switch
    {
        AccountKind.OpenCodeZenApiKey => Zen,
        AccountKind.OpenCodeGoApiKey => Go,
        _ => null
    };

    /// <summary>The base URL a server's configuration names for this gateway, empty for OpenCode's own.</summary>
    public string? ConfiguredBaseUrl(AgentOptions options) => this == Zen ? options.OpenCodeZenBaseUrl : options.OpenCodeGoBaseUrl;

    string Base(string? configured) => (string.IsNullOrEmpty(configured) ? DefaultBaseUrl : configured).TrimEnd('/');

    public bool UsesMessages(string model) => _usesMessages(model);

    /// <summary>These speak Responses themselves, so asking for their encrypted reasoning makes sense; the rest are translated.</summary>
    public bool IsNativeResponses(string model) => _isNativeResponses(model);

    /// <summary>OpenAI's models are trained on <c>apply_patch</c>.</summary>
    public static bool OffersPatch(string model) => model.StartsWith("gpt-", StringComparison.Ordinal);

    /// <summary>OpenCode keeps its free models for its own app and answers 403 for them anywhere else.</summary>
    public static bool IsFree(string model) => model.EndsWith("-free", StringComparison.Ordinal) || model is "big-pickle" or "grok-code";

    public IModelTurnExecutor ExecutorFor(HttpClient http, string apiKey, string model, string? baseUrl) => UsesMessages(model)
        ? AnthropicMessagesExecutor.ForOpenCode(http, apiKey, new Uri(Base(baseUrl) + "/anthropic/v1/messages"), Name)
        : OpenAIResponsesExecutor.ForOpenCode(http, apiKey, new Uri(Base(baseUrl) + "/openai/v1/responses"), Name, IsNativeResponses(model));

    /// <summary>The models that can run an agent: paid chat models with tools. The list is public, so no key goes with the request.</summary>
    /// <exception cref="HttpRequestException">OpenCode could not be asked.</exception>
    /// <exception cref="JsonException">It answered something else.</exception>
    public async Task<AgentModelDto[]> ListModelsAsync(HttpClient http, string? baseUrl, CancellationToken cancellation)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, Base(baseUrl) + "/v1/models");
        message.Headers.TryAddWithoutValidation("User-Agent", ProviderHttp.UserAgent);
        using var response = await http.SendAsync(message, cancellation);
        response.EnsureSuccessStatusCode();
        var models = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation))?["data"] as JsonArray ?? [];
        return models.OfType<JsonObject>()
            .Where(model => Str(model["type"]) == "chat" && Flag(model["capabilities"]?["tools"]?["supported"]) && Str(model["id"]) is { Length: > 0 } id && !IsFree(id))
            .Select(model => new AgentModelDto(Str(model["id"])!, Str(model["name"]) ?? Str(model["id"])!, Long(model["limit"]?["context"]), Long(model["limit"]?["output"]),
                (model["capabilities"]?["reasoning"]?["effort"] as JsonArray ?? []).Select(Str).OfType<string>().ToArray()))
            .OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    static bool Flag(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
    static long? Long(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;
}
