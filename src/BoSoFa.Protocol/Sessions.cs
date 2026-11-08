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
    Diff
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
    string? ProfileName);

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
    string? ProfileName = null);

/// <summary>How a message reaches a session whose turn is still running.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DispatchMode>))]
public enum DispatchMode
{
    /// <summary>Waits until the running turn ends, then starts the next one.</summary>
    Queue,

    /// <summary>Redirects the running turn now (for harnesses without native steering: interrupt, then send).</summary>
    Steer
}

public sealed record SendMessageRequest(string Text, DispatchMode? Mode = null);

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

    public static readonly string[] All = [Claude, Codex, OpenCode, Replay];

    public static readonly string[] PermissionModes = ["default", "acceptEdits", "plan", "bypassPermissions"];
}
