using System.Text.Json.Nodes;

namespace BoSoFa.Server.Agents;

// The server agent's provider layer (docs/server-agent.md §5): one model call per turn, the same contracts for every API.
// The loop (step 2) keeps the conversation and calls a turn executor again after running the tools.

/// <summary>Where a provider-specific piece came from. Opaque data (OpenAI's encrypted reasoning, Anthropic's thinking
/// signatures) is replayed only to the same API, endpoint and model; anywhere else it is left out.</summary>
public sealed record ModelOrigin(string Api, string Endpoint, string Model);

/// <summary>One entry of the conversation, as the loop stores it.</summary>
public abstract record ConversationItem;

/// <summary>What the person wrote.</summary>
public sealed record UserMessage(string Text) : ConversationItem;

/// <summary>Text the model wrote. <c>ProviderData</c> is the provider's own form of it (JSON), for replay to the same origin.</summary>
public sealed record AssistantMessage(string Text, ModelOrigin? Origin = null, string? ProviderData = null) : ConversationItem;

/// <summary>The model's reasoning: readable text (a summary, where the provider gives only that) and the provider's opaque
/// form, which only the same origin may receive back.</summary>
public sealed record ReasoningItem(string Text, ModelOrigin Origin, string? ProviderData) : ConversationItem;

/// <summary>A tool the model wants run. <c>Arguments</c> is the JSON object as the model wrote it (it may not parse).</summary>
public sealed record ToolCall(string Id, string Name, string Arguments, ModelOrigin? Origin = null, string? ProviderData = null)
    : ConversationItem;

/// <summary>What running a tool gave back to the model.</summary>
public sealed record ToolResult(string CallId, string Text, bool IsError = false) : ConversationItem;

/// <summary>A tool the model may call; <c>InputSchema</c> is a JSON Schema of an object.</summary>
public sealed record ToolSpec(string Name, string Description, JsonObject InputSchema);

/// <param name="Instructions">The system prompt shared by many sessions; it comes first, so providers cache it.</param>
/// <param name="SessionInstructions">This session's own instructions, sent after the shared ones so they do not break
/// the cached prefix.</param>
/// <param name="SessionId">Stable for a conversation: a cache key and, where an endpoint asks for one, its session header.</param>
/// <param name="ReasoningEffort">low, medium, high or null for the model's default.</param>
public sealed record TurnRequest(string Model, string Instructions, IReadOnlyList<ConversationItem> Conversation,
    IReadOnlyList<ToolSpec> Tools, string SessionId, string? SessionInstructions = null, string? ReasoningEffort = null,
    int? MaxOutputTokens = null);

public enum StopReason
{
    /// <summary>The model is done and waits for the person.</summary>
    EndTurn,

    /// <summary>The model asked for tools; run them and call it again.</summary>
    ToolCalls,

    /// <summary>The output limit cut the answer.</summary>
    MaxTokens,

    /// <summary>The provider refused or filtered the answer.</summary>
    Refused
}

/// <param name="InputTokens">All input, cached or not.</param>
public sealed record TurnUsage(long InputTokens, long CachedInputTokens, long CacheWriteTokens, long OutputTokens, long ReasoningTokens)
{
    public static readonly TurnUsage None = new(0, 0, 0, 0, 0);
}

/// <summary>The model's output of one call, in order, and what it cost.</summary>
public sealed record TurnResponse(IReadOnlyList<ConversationItem> Output, TurnUsage Usage, StopReason Stop);

public enum TurnDeltaKind
{
    Text,
    Reasoning,
    ToolArguments,

    /// <summary>The call is being made again after a failure: drop what this turn streamed so far.</summary>
    Restart
}

/// <summary>A piece of the answer while it streams. Only for showing: the final items come in the <see cref="TurnResponse"/>.</summary>
public sealed record TurnDelta(TurnDeltaKind Kind, string Text);

public enum ProviderFailure
{
    /// <summary>The provider or the network failed for now (5xx, a broken stream); asking again may work.</summary>
    Transient,

    /// <summary>Too many requests; wait <see cref="ModelProviderException.RetryAfter"/> and ask again.</summary>
    RateLimited,

    /// <summary>The conversation does not fit the model's context: compact it first.</summary>
    ContextOverflow,

    /// <summary>The key or token is wrong or expired.</summary>
    Authentication,

    /// <summary>The account has no credit or quota left; asking again will not help.</summary>
    QuotaExceeded,

    /// <summary>The request is wrong (unknown model, bad schema); asking again will not help.</summary>
    Invalid
}

/// <summary>A model call that failed, classified so the loop knows whether to retry, compact, or tell the person.</summary>
public sealed class ModelProviderException(ProviderFailure failure, string message, TimeSpan? retryAfter = null, int? status = null)
    : Exception(message)
{
    public ProviderFailure Failure { get; } = failure;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public int? Status { get; } = status;

    public bool IsRetryable => Failure is ProviderFailure.Transient or ProviderFailure.RateLimited;
}

/// <summary>One model call of one API (docs/server-agent.md §5).</summary>
public interface IModelTurnExecutor
{
    /// <summary>The API and endpoint this executor calls; together with the model, the origin of its opaque data.</summary>
    string Api { get; }

    string Endpoint { get; }

    /// <summary>Streams the answer through <paramref name="onDelta"/> and returns it whole.</summary>
    /// <exception cref="ModelProviderException">The call failed.</exception>
    Task<TurnResponse> ExecuteAsync(TurnRequest request, Func<TurnDelta, ValueTask> onDelta, CancellationToken cancellation);
}
