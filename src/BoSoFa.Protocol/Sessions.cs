using System.Text.Json.Serialization;

namespace BoSoFa.Protocol;

[JsonConverter(typeof(JsonStringEnumConverter<SessionStatus>))]
public enum SessionStatus
{
    /// <summary>Created on the server; the machine has not started the harness yet.</summary>
    Starting,

    /// <summary>A turn is running.</summary>
    Running,

    /// <summary>A turn waits for a person to answer a runtime request (a tool approval).</summary>
    WaitingForApproval,

    /// <summary>Between turns: the harness waits for the next message.</summary>
    Idle,

    Stopped,
    Failed
}

[JsonConverter(typeof(JsonStringEnumConverter<SessionItemKind>))]
public enum SessionItemKind
{
    User,
    Assistant,
    Thinking,
    ToolUse,
    ToolResult,
    RunEnded,
    Notice,

    /// <summary>What a run changed: <c>Text</c> is the patch, <c>Data</c> the per-file line counts as JSON.</summary>
    Diff,

    /// <summary>What a server agent session's model was told, fixed when the session starts.</summary>
    Instructions,

    /// <summary>A summary that stands in for everything before it, so a long conversation fits the model.</summary>
    Compaction,

    /// <summary>The conversation starts over here (/clear): the harness no longer sees anything before it.</summary>
    Cleared,

    /// <summary>A question asked on the side (/btw): answered from the conversation so far, which it never becomes part of.</summary>
    SideQuestion,

    /// <summary>The answer to a <see cref="SideQuestion"/>, whose sequence number is its <c>Ref</c>.</summary>
    SideAnswer
}

[JsonConverter(typeof(JsonStringEnumConverter<RuntimeRequestStatus>))]
public enum RuntimeRequestStatus
{
    Pending,
    Allowed,
    Denied,

    /// <summary>The harness stopped waiting (interrupt, turn ended, process exit).</summary>
    Cancelled
}

/// <summary>Summary of a session. <c>LastSeq</c> is the seq of its newest transcript item.</summary>
public sealed record SessionDto(
    ulong Id,
    ulong MachineId,
    string MachineName,
    string Harness,
    string WorkingDirectory,
    string Title,
    SessionStatus Status,
    ulong CreatedById,
    string CreatedByName,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    long LastSeq,
    string? Model,
    string PermissionMode,
    double CostUsd,
    bool CanControl,
    ulong? AccountId,
    string? AccountLabel,
    string? Branch,
    string? ProfileName,
    string? ReasoningEffort = null,
    string? ToolGroup = null,
    long? ContextTokens = null,
    long? ContextLimit = null,
    DateTime? CacheExpiresAt = null,
    AccountUsageDto? AccountUsage = null);

/// <summary>One persisted transcript entry. <c>Data</c> holds JSON details (tool input, run usage) and <c>Ref</c> the
/// harness's message ID, which live deltas carry too.</summary>
public sealed record SessionItemDto(
    long Seq,
    SessionItemKind Kind,
    DateTime At,
    string Text,
    string? ToolUseId,
    string? ToolName,
    string? Data,
    bool IsError,
    string? Ref);

/// <summary>A question from the harness to a person, for now a tool permission prompt. <c>Input</c> is JSON.</summary>
public sealed record RuntimeRequestDto(
    ulong Id,
    ulong SessionId,
    string ToolName,
    string? ToolUseId,
    string Input,
    string? Description,
    RuntimeRequestStatus Status,
    DateTime RaisedAt,
    string? AnsweredByName,
    DateTime? AnsweredAt);

public sealed record CreateSessionRequest(
    ulong MachineId,
    string Harness,
    string WorkingDirectory,
    string Prompt,
    string? PermissionMode,
    string? Model,
    ulong? AccountId = null,
    bool UseWorktree = false,
    string? ProfileName = null,
    string[]? Images = null,
    string? SystemPrompt = null,
    string? ReasoningEffort = null,
    string? ToolGroup = null,
    ulong? DraftId = null);

/// <summary>A session being written: its prompt and the client's own settings JSON for how to start it.</summary>
public sealed record SessionDraftDto(ulong Id, string Title, string Prompt, string Settings, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record SaveSessionDraftRequest(string Prompt, string Settings);

/// <summary>How a message reaches a session whose turn is still running.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DispatchMode>))]
public enum DispatchMode
{
    /// <summary>Waits until the running turn ends, then starts the next one.</summary>
    Queue,

    /// <summary>Redirects the running turn now (for harnesses without native steering: interrupt, then send).</summary>
    Steer
}

/// <param name="Images">SHA-256 hashes of images already uploaded as blobs.</param>
/// <summary>A question on the side (/btw): the harness answers it from the conversation so far without adding either to it.</summary>
public sealed record SideQuestionRequest(string Text);

public sealed record SendMessageRequest(string Text, DispatchMode? Mode = null, string[]? Images = null);

/// <summary>An image of a prompt, in the blob store. The server read <c>MediaType</c> from the content.</summary>
public sealed record PromptImage(string Sha256, string MediaType, long Size);

public static class PromptImages
{
    public const int MaximumCount = 10;

    /// <summary>What the Claude API accepts for one image; the others take at least this.</summary>
    public const long MaximumBytes = 5 * 1024 * 1024;

    /// <summary>How many leading bytes <see cref="MediaTypeOf"/> needs.</summary>
    public const int HeaderLength = 12;

    public static bool IsSha256(string value) => value.Length == 64 && value.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    /// <summary>Null when the content is not one of the formats every harness can read.</summary>
    public static string? MediaTypeOf(ReadOnlySpan<byte> header) =>
        header.StartsWith<byte>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) ? "image/png"
        : header.StartsWith<byte>([0xFF, 0xD8, 0xFF]) ? "image/jpeg"
        : header.StartsWith("GIF87a"u8) || header.StartsWith("GIF89a"u8) ? "image/gif"
        : header.Length >= HeaderLength && header.StartsWith("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8) ? "image/webp"
        : null;

    public static string ExtensionOf(string mediaType) => mediaType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        _ => ""
    };
}

public sealed record AnswerRuntimeRequest(bool Allow, string? Message);

public static class Harnesses
{
    public const string Claude = "claude";

    /// <summary>OpenAI Codex through <c>codex app-server</c> (M3 step 3).</summary>
    public const string Codex = "codex";

    /// <summary>OpenCode, through ACP.</summary>
    public const string OpenCode = "opencode";

    /// <summary>Plays a recorded Claude Code transcript; only on daemons started with <c>--replay-fixture</c>.</summary>
    public const string Replay = "replay";

    /// <summary>The server's own loop (docs/server-agent.md); it runs on a server node and needs no machine.</summary>
    public const string Agent = "agent";

    public static readonly string[] All = [Claude, Codex, OpenCode, Replay, Agent];

    public static readonly string[] PermissionModes = ["default", "acceptEdits", "plan", "bypassPermissions"];

    /// <summary>Every level some provider knows; a model that lacks one answers with an error the person sees.</summary>
    public static readonly string[] ReasoningEfforts = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>A harness on a machine names its own levels (Codex has "minimal", OpenCode "default"…), so only their form is checked.</summary>
    public static bool IsEffortName(string effort) => effort.Length is > 0 and <= 24 && effort.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_');

    /// <summary>How a profile's system prompt combines with the built-in one.</summary>
    public static readonly string[] SystemPromptModes = ["append", "replace"];
}

/// <summary>Which of the machine's tools a server agent session gets (docs/server-agent.md §3).</summary>
public static class AgentToolGroups
{
    public const string None = "none", Read = "read", Full = "full";
    public static readonly string[] All = [None, Read, Full];

    /// <summary>The narrower of the two; a null means no restriction.</summary>
    public static string Narrower(string? first, string? second)
    {
        var (a, b) = (Array.IndexOf(All, first ?? Full), Array.IndexOf(All, second ?? Full));
        return All[Math.Min(a, b)];
    }
}

/// <summary>A transcript item the server's agent loop records. <c>Replay</c> is the loop's own form of it (see
/// <c>SessionItem.Replay</c>); the other fields are what people see.</summary>
public sealed record AgentItem(SessionItemKind Kind, string Text, string? ToolUseId = null, string? ToolName = null,
    string? Data = null, bool IsError = false, string? Ref = null, string? Replay = null);

/// <summary>What the agent loop keeps of a person's message: the whole text when the shown one is cut, and whether it
/// waits for the running turn to end instead of steering it.</summary>
public sealed record AgentUserReplay(string? Text, bool Queued);
