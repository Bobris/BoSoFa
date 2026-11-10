using System.Text.Json;
using BoSoFa.Protocol;
using BoSoFa.Server.Domain;

namespace BoSoFa.Server.Agents;

/// <summary>What the transcript says the loop should do next: the conversation for the next model call, the tool calls still
/// without a result, and whether the model is due.</summary>
/// <param name="JustCompacted">Nothing has happened since the last summary, so another one would not help.</param>
public sealed record AgentConversationState(IReadOnlyList<ConversationItem> Conversation, IReadOnlyList<ToolCall> OpenCalls, bool NeedsModel,
    bool HasQueued, bool JustCompacted = false);

/// <summary>
/// Turns stored items into the conversation (docs/server-agent.md §4.2): the transcript is the loop's only state. People's messages
/// are stored when they arrive, which can be between a tool call and its result, so they are placed where the model must see them:
/// a steering message after the current step's tool results, a queued one after the run that was going on when it came.
/// </summary>
public static class AgentConversation
{
    public static AgentConversationState Rebuild(IEnumerable<SessionItem> items)
    {
        var conversation = new List<ConversationItem>();
        var open = new List<ToolCall>();
        var steering = new List<UserMessage>();
        var queued = new List<UserMessage>();
        var runOpen = false;
        UserMessage? lastUser = null;
        var justCompacted = false;

        void Steer(UserMessage message)
        {
            if (open.Count > 0) steering.Add(message);
            else conversation.Add(message);
            lastUser = message;
        }

        foreach (var item in items)
            switch (item.Kind)
            {
                case SessionItemKind.User:
                    var user = UserOf(item, out var isQueued);
                    if (runOpen && isQueued) queued.Add(user);
                    else Steer(user);
                    runOpen = true;
                    break;
                case SessionItemKind.Compaction:
                    // Everything so far becomes the summary, and the person's latest words stay as they were.
                    conversation.Clear();
                    conversation.Add(Stored<UserMessage>(item) ?? new UserMessage(item.Text));
                    if (lastUser != null) conversation.Add(lastUser);
                    justCompacted = true;
                    break;
                case SessionItemKind.Assistant:
                    justCompacted = false;
                    conversation.Add(Stored<AssistantMessage>(item) ?? new AssistantMessage(item.Text));
                    break;
                case SessionItemKind.Thinking:
                    if (Stored<ReasoningItem>(item) is { } reasoning) conversation.Add(reasoning);
                    break;
                case SessionItemKind.ToolUse:
                    justCompacted = false;
                    var call = Stored<ToolCall>(item) ?? new ToolCall(item.ToolUseId ?? "", item.ToolName ?? "", item.Data ?? "{}");
                    conversation.Add(call);
                    open.Add(call);
                    break;
                case SessionItemKind.ToolResult:
                    conversation.Add(new ToolResult(item.ToolUseId ?? "", item.Text, item.IsError, ImagesOf(item)));
                    open.RemoveAll(candidate => candidate.Id == item.ToolUseId);
                    if (open.Count == 0)
                    {
                        conversation.AddRange(steering);
                        steering.Clear();
                    }
                    break;
                case SessionItemKind.RunEnded:
                    runOpen = queued.Count > 0;
                    conversation.AddRange(queued);
                    if (queued.Count > 0) lastUser = queued[^1];
                    queued.Clear();
                    break;
            }
        var needsModel = runOpen && open.Count == 0 && conversation.LastOrDefault() is UserMessage or ToolResult;
        return new(conversation, open, needsModel, queued.Count > 0, justCompacted);
    }

    public static string Serialize(ConversationItem item) => JsonSerializer.Serialize(item, ServerJsonContext.Default.ConversationItem);

    static T? Stored<T>(SessionItem item) where T : ConversationItem => item.Replay == null
        ? null
        : JsonSerializer.Deserialize(item.Replay, ServerJsonContext.Default.ConversationItem) as T;

    static UserMessage UserOf(SessionItem item, out bool queued)
    {
        var replay = item.Replay == null ? null : JsonSerializer.Deserialize(item.Replay, ProtocolJsonContext.Default.AgentUserReplay);
        queued = replay?.Queued == true;
        return new(replay?.Text ?? item.Text, ImagesOf(item));
    }

    /// <summary>A message's or a tool result's images are in its data, where the page shows them too.</summary>
    static PromptImage[]? ImagesOf(SessionItem item)
    {
        if (item.Data is not { Length: > 0 } data) return null;
        try { return JsonSerializer.Deserialize(data, ProtocolJsonContext.Default.PromptImageArray) is { Length: > 0 } images ? images : null; }
        catch (JsonException) { return null; }
    }
}
