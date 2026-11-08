using System.Text.Json.Serialization;

namespace BoSoFa.Protocol;

/// <summary>Messages on the daemon's WebSocket (<c>/api/daemon/v1/connect</c>), one JSON text frame each, with the
/// type in <c>t</c>. The daemon always dials out; the long-poll fallback carries the same messages.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(WelcomeMessage), "welcome")]
[JsonDerivedType(typeof(PingMessage), "ping")]
[JsonDerivedType(typeof(PongMessage), "pong")]
[JsonDerivedType(typeof(CommandMessage), "command")]
[JsonDerivedType(typeof(CommandAckMessage), "commandAck")]
[JsonDerivedType(typeof(OutboxMessage), "outbox")]
[JsonDerivedType(typeof(OutboxAckMessage), "outboxAck")]
[JsonDerivedType(typeof(DeltaMessage), "delta")]
public abstract record DaemonMessage;

/// <summary>First message of the daemon after connecting.</summary>
public sealed record HelloMessage(string DaemonVersion, int ProtocolVersion, string Os, string Arch, string HostName)
    : DaemonMessage;

/// <summary>The server's answer to <see cref="HelloMessage"/>. Every unacknowledged command follows it.</summary>
public sealed record WelcomeMessage(ulong MachineId, string ServerVersion) : DaemonMessage;

/// <summary>Keepalive from either side; the other answers with <see cref="PongMessage"/>.</summary>
public sealed record PingMessage(long Sequence) : DaemonMessage;

public sealed record PongMessage(long Sequence) : DaemonMessage;

/// <summary>Server → daemon: a durable command from the machine's inbox. It is sent again after every reconnect until
/// the daemon acknowledges it, so handling must be idempotent per <c>Id</c>.</summary>
public sealed record CommandMessage(ulong Id, MachineCommand Command) : DaemonMessage;

/// <summary>Daemon → server: these commands were applied; the server drops them from the inbox.</summary>
public sealed record CommandAckMessage(ulong[] Ids) : DaemonMessage;

/// <summary>Daemon → server: durable reports from the daemon's outbox, in seq order per session.</summary>
public sealed record OutboxMessage(OutboxRecord[] Records) : DaemonMessage;

/// <summary>Server → daemon: every record of the session up to <c>Seq</c> is stored; the daemon may forget them.</summary>
public sealed record OutboxAckMessage(OutboxAck[] Acks) : DaemonMessage;

/// <summary>Daemon → server, ephemeral: streamed text of an assistant message that is not complete yet. Dropped while
/// disconnected; the final item arrives through the outbox anyway.</summary>
public sealed record DeltaMessage(ulong SessionId, string Ref, string Text) : DaemonMessage;

public sealed record OutboxRecord(ulong SessionId, long Seq, SessionReport Report);

public sealed record OutboxAck(ulong SessionId, long Seq);

/// <summary>What the server asks a machine to do, kind in <c>k</c>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "k")]
[JsonDerivedType(typeof(StartSessionCommand), "startSession")]
[JsonDerivedType(typeof(SendMessageCommand), "sendMessage")]
[JsonDerivedType(typeof(InterruptCommand), "interrupt")]
[JsonDerivedType(typeof(AnswerApprovalCommand), "answerApproval")]
[JsonDerivedType(typeof(StopSessionCommand), "stopSession")]
public abstract record MachineCommand(ulong SessionId);

/// <summary>Starts a session. <c>AccountId</c> names the harness account whose secret the daemon leases just in time
/// (<c>POST /api/daemon/v1/accounts/{id}/lease</c>); null runs on the machine's own sign-in.</summary>
public sealed record StartSessionCommand(ulong SessionId, string Harness, string WorkingDirectory, string Prompt,
    string PermissionMode, string? Model, ulong? AccountId = null, bool UseWorktree = false) : MachineCommand(SessionId);

public sealed record SendMessageCommand(ulong SessionId, string Text, DispatchMode Mode = DispatchMode.Queue)
    : MachineCommand(SessionId);

public sealed record InterruptCommand(ulong SessionId) : MachineCommand(SessionId);

public sealed record AnswerApprovalCommand(ulong SessionId, string Key, bool Allow, string? Message)
    : MachineCommand(SessionId);

public sealed record StopSessionCommand(ulong SessionId) : MachineCommand(SessionId);

/// <summary>A durable fact a daemon reports about one of its sessions, kind in <c>k</c>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "k")]
[JsonDerivedType(typeof(ItemReport), "item")]
[JsonDerivedType(typeof(StatusReport), "status")]
[JsonDerivedType(typeof(RunEndedReport), "runEnded")]
[JsonDerivedType(typeof(ApprovalRequestedReport), "approval")]
[JsonDerivedType(typeof(ApprovalClosedReport), "approvalClosed")]
[JsonDerivedType(typeof(WorkspaceReport), "workspace")]
public abstract record SessionReport;

/// <summary>A transcript item. Text above <see cref="InlineLimit"/> is cut; blobs carry the rest later (M4).</summary>
public sealed record ItemReport(SessionItemKind Kind, DateTime At, string Text, string? ToolUseId = null,
    string? ToolName = null, string? Data = null, bool IsError = false, string? Ref = null) : SessionReport
{
    public const int InlineLimit = 16 * 1024;
}

public sealed record StatusReport(SessionStatus Status, string? HarnessSessionId = null, string? Model = null,
    string? Error = null) : SessionReport;

/// <summary>A turn ended. Cost and tokens are for this turn only.</summary>
public sealed record RunEndedReport(DateTime At, string Outcome, bool IsError, double CostUsd, long InputTokens,
    long OutputTokens, long DurationMs) : SessionReport;

/// <summary>The harness waits for permission to run a tool. <c>Key</c> is the harness's own request ID.</summary>
public sealed record ApprovalRequestedReport(string Key, DateTime At, string ToolName, string? ToolUseId, string Input,
    string? Description) : SessionReport;

public sealed record ApprovalClosedReport(string Key, string Reason) : SessionReport;

/// <summary>The session runs in this worktree, on this branch.</summary>
public sealed record WorkspaceReport(string Path, string Branch) : SessionReport;
