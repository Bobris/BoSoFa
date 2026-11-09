using System.Text.Json;
using BoSoFa.Protocol;
using BoSoFa.Server.Domain;

namespace BoSoFa.Server.Agents;

/// <summary>What the transcript says the loop should do next: the conversation for the next model call, the tool calls still
/// without a result, and whether the model is due.</summary>
public sealed record AgentConversationState(IReadOnlyList<ConversationItem> Conversation, IReadOnlyList<ToolCall> OpenCalls, bool NeedsModel,
    bool HasQueued);

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

        void Steer(UserMessage message)
        {
            if (open.Count > 0) steering.Add(message);
            else conversation.Add(message);
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
                case SessionItemKind.Assistant:
                    conversation.Add(Stored<AssistantMessage>(item) ?? new AssistantMessage(item.Text));
                    break;
                case SessionItemKind.Thinking:
                    if (Stored<ReasoningItem>(item) is { } reasoning) conversation.Add(reasoning);
                    break;
                case SessionItemKind.ToolUse:
                    var call = Stored<ToolCall>(item) ?? new ToolCall(item.ToolUseId ?? "", item.ToolName ?? "", item.Data ?? "{}");
                    conversation.Add(call);
                    open.Add(call);
                    break;
                case SessionItemKind.ToolResult:
                    conversation.Add(new ToolResult(item.ToolUseId ?? "", item.Text, item.IsError));
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
                    queued.Clear();
                    break;
            }
        var needsModel = runOpen && open.Count == 0 && conversation.LastOrDefault() is UserMessage or ToolResult;
        return new(conversation, open, needsModel, queued.Count > 0);
    }

    public static string Serialize(ConversationItem item) => JsonSerializer.Serialize(item, ServerJsonContext.Default.ConversationItem);

    static T? Stored<T>(SessionItem item) where T : ConversationItem => item.Replay == null
        ? null
        : JsonSerializer.Deserialize(item.Replay, ServerJsonContext.Default.ConversationItem) as T;

    static UserMessage UserOf(SessionItem item, out bool queued)
    {
        var replay = item.Replay == null ? null : JsonSerializer.Deserialize(item.Replay, ProtocolJsonContext.Default.AgentUserReplay);
        queued = replay?.Queued == true;
        return new(replay?.Text ?? item.Text);
    }
}
