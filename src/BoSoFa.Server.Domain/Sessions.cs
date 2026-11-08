using System.Text.Json;
using BoSoFa.Protocol;
using BTDB.ODBLayer;
using BTDB.Replication.EventSourcing;

namespace BoSoFa.Server.Domain;

public class Session
{
    [PrimaryKey(1)] public ulong Id { get; set; }
    public ulong MachineId { get; set; }
    public ulong CreatedById { get; set; }
    public string Harness { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string Title { get; set; } = "";
    public string PermissionMode { get; set; } = "";
    public string? Model { get; set; }

    /// <summary>The harness's own conversation ID (Claude Code's session_id), for resuming.</summary>
    public string? HarnessSessionId { get; set; }

    public SessionStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Seq of the newest transcript item; items are numbered from 1.</summary>
    public long LastSeq { get; set; }

    /// <summary>Highest outbox seq of the machine applied to this session; reports up to it are duplicates.</summary>
    public long LastReportSeq { get; set; }

    public double CostUsd { get; set; }
}

public interface ISessionTable : IRelation<Session>
{
    void Insert(Session session);
    void Update(Session session);
    Session? FindByIdOrDefault(ulong id);
}

public class SessionItem
{
    [PrimaryKey(1)] public ulong SessionId { get; set; }
    [PrimaryKey(2)] public long Seq { get; set; }
    public SessionItemKind Kind { get; set; }
    public DateTime At { get; set; }
    public string Text { get; set; } = "";
    public string? ToolUseId { get; set; }
    public string? ToolName { get; set; }
    public string? Data { get; set; }
    public bool IsError { get; set; }
    public string? Ref { get; set; }

    public SessionItemDto ToDto() => new(Seq, Kind, At, Text, ToolUseId, ToolName, Data, IsError, Ref);
}

public interface ISessionItemTable : IRelation<SessionItem>
{
    void Insert(SessionItem item);
    IEnumerable<SessionItem> ListById(ulong sessionId, AdvancedEnumeratorParam<long> param);
}

public class RuntimeRequest
{
    [PrimaryKey(1)] public ulong SessionId { get; set; }
    [PrimaryKey(2)] public ulong Id { get; set; }

    /// <summary>The harness's request ID, unique within the session.</summary>
    [SecondaryKey("Key", IncludePrimaryKeyOrder = 1)] public string Key { get; set; } = "";

    public string ToolName { get; set; } = "";
    public string? ToolUseId { get; set; }
    public string Input { get; set; } = "";
    public string? Description { get; set; }
    public RuntimeRequestStatus Status { get; set; }
    public DateTime RaisedAt { get; set; }
    public ulong AnsweredById { get; set; }
    public DateTime? AnsweredAt { get; set; }
}

public interface IRuntimeRequestTable : IRelation<RuntimeRequest>
{
    void Insert(RuntimeRequest request);
    void Update(RuntimeRequest request);
    RuntimeRequest? FindByIdOrDefault(ulong sessionId, ulong id);
    RuntimeRequest? FindByKeyOrDefault(ulong sessionId, string key);
    IEnumerable<RuntimeRequest> ListById(ulong sessionId);
}

/// <summary>The machine inbox (plan §4.2): commands waiting for the daemon's acknowledgement, ordered by ID.</summary>
public class MachineCommandEntry
{
    [PrimaryKey(1)] public ulong MachineId { get; set; }
    [PrimaryKey(2)] public ulong Id { get; set; }

    /// <summary>The <see cref="MachineCommand"/> as protocol JSON.</summary>
    public string Command { get; set; } = "";

    public DateTime IssuedAt { get; set; }
}

public interface IMachineCommandTable : IRelation<MachineCommandEntry>
{
    void Insert(MachineCommandEntry entry);
    bool RemoveById(ulong machineId, ulong id);
    IEnumerable<MachineCommandEntry> ListById(ulong machineId, AdvancedEnumeratorParam<ulong> param);
}

/// <summary>Process-local reactions after a batch is visible: wake the daemon connection, push to live subscribers.
/// They run on every node that consumes the event, never during replay.</summary>
public interface IDomainSignals
{
    void MachineCommandsIssued(ulong machineId);
    void SessionChanged(ulong sessionId);

    /// <summary>The machine's connection is now <paramref name="connectionId"/> (null: none). A node holding a different
    /// connection of that machine closes it.</summary>
    void MachinePresenceChanged(ulong machineId, string? connectionId);
}

public enum CommandOutcome
{
    Ok,
    NotFound,
    Forbidden,
    Invalid
}

[BTDB.Generate]
public class SessionCommandResult
{
    public CommandOutcome Outcome { get; set; }
    public ulong SessionId { get; set; }
}

/// <summary>A person starts a harness on one of their machines.</summary>
public class SessionCreated : EventWithResult<SessionCommandResult>
{
    public ulong MachineId { get; set; }
    public ulong UserId { get; set; }
    public string Harness { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string PermissionMode { get; set; } = "";
    public string? Model { get; set; }
}

public class SessionMessageSent : EventWithResult<SessionCommandResult>
{
    public ulong SessionId { get; set; }
    public ulong UserId { get; set; }
    public string Text { get; set; } = "";
    public DispatchMode Mode { get; set; }
}

public class SessionInterruptRequested : EventWithResult<SessionCommandResult>
{
    public ulong SessionId { get; set; }
    public ulong UserId { get; set; }
}

public class SessionStopRequested : EventWithResult<SessionCommandResult>
{
    public ulong SessionId { get; set; }
    public ulong UserId { get; set; }
}

public class RuntimeRequestAnswered : EventWithResult<SessionCommandResult>
{
    public ulong SessionId { get; set; }
    public ulong RequestId { get; set; }
    public ulong UserId { get; set; }
    public bool Allow { get; set; }
    public string? Message { get; set; }
}

/// <summary>The daemon applied these commands; they leave the inbox.</summary>
public class MachineCommandsAcked : PureEvent
{
    public ulong MachineId { get; set; }
    public List<ulong> CommandIds { get; set; } = [];
}

/// <summary>A batch from the daemon's outbox, as the protocol JSON of <see cref="OutboxRecord"/>[]. Records the
/// session already has (seq at most <see cref="Session.LastReportSeq"/>) are skipped, so resending is harmless.</summary>
public class DaemonReportsReceived : PureEvent
{
    public ulong MachineId { get; set; }
    public string RecordsJson { get; set; } = "";
}

static class Sessions
{
    public const int MaximumPromptLength = 100_000;

    public static bool CanControl(ulong userId, Machine machine) => userId != 0 && machine.OwnerId == userId;

    public static long AppendItem(Session session, SessionItemKind kind, DateTime at, string text, string? toolUseId = null,
        string? toolName = null, string? data = null, bool isError = false, string? itemRef = null)
    {
        session.LastSeq++;
        Ambient.Relation<ISessionItemTable>().Insert(new()
        {
            SessionId = session.Id, Seq = session.LastSeq, Kind = kind, At = at, Text = Cut(text), ToolUseId = toolUseId,
            ToolName = toolName, Data = data == null ? null : Cut(data), IsError = isError, Ref = itemRef
        });
        return session.LastSeq;
    }

    static string Cut(string text) => text.Length <= ItemReport.InlineLimit
        ? text
        : text[..ItemReport.InlineLimit] + $"\n… ({text.Length - ItemReport.InlineLimit} more characters)";

    public static void IssueCommand(IDomainSignals signals, ulong machineId, MachineCommand command, DateTime when)
    {
        Ambient.Relation<IMachineCommandTable>().Insert(new()
        {
            MachineId = machineId, Id = Ambient.AllocateId(),
            Command = JsonSerializer.Serialize(command, ProtocolJsonContext.Default.MachineCommand), IssuedAt = when
        });
        Ambient.RegisterPostCommit(() => signals.MachineCommandsIssued(machineId));
    }

    public static void Changed(IDomainSignals signals, Session session, DateTime when)
    {
        session.UpdatedAt = when;
        Ambient.Relation<ISessionTable>().Update(session);
        var id = session.Id;
        Ambient.RegisterPostCommit(() => signals.SessionChanged(id));
    }

    /// <summary>The session and its machine when the user may control it.</summary>
    public static (Session? Session, Machine? Machine, CommandOutcome Outcome) Controlled(ulong sessionId, ulong userId)
    {
        var session = Ambient.Relation<ISessionTable>().FindByIdOrDefault(sessionId);
        if (session == null) return (null, null, CommandOutcome.NotFound);
        var machine = Ambient.Relation<IMachineTable>().FindByIdOrDefault(session.MachineId);
        if (machine == null) return (null, null, CommandOutcome.NotFound);
        return CanControl(userId, machine) ? (session, machine, CommandOutcome.Ok) : (null, null, CommandOutcome.Forbidden);
    }

    public static string TitleOf(string prompt)
    {
        var line = prompt.Trim().Split('\n', 2)[0].Trim();
        return line.Length <= 80 ? line : line[..79] + "…";
    }
}

public sealed class SessionCreatedHandler(IDomainSignals signals) : IEventHandler<SessionCreated, SessionCommandResult>
{
    public SessionCommandResult? Validate(SessionCreated ev) =>
        string.IsNullOrWhiteSpace(ev.Prompt) || ev.Prompt.Length > Sessions.MaximumPromptLength ||
        string.IsNullOrWhiteSpace(ev.WorkingDirectory) || ev.Harness is not (Harnesses.Claude or Harnesses.Replay) ||
        !Harnesses.PermissionModes.Contains(ev.PermissionMode)
            ? new() { Outcome = CommandOutcome.Invalid }
            : null;

    public SessionCommandResult Consume(SessionCreated ev)
    {
        var machine = Ambient.Relation<IMachineTable>().FindByIdOrDefault(ev.MachineId);
        if (machine == null) return new() { Outcome = CommandOutcome.NotFound };
        if (!Sessions.CanControl(ev.UserId, machine)) return new() { Outcome = CommandOutcome.Forbidden };
        var session = new Session
        {
            Id = Ambient.AllocateId(), MachineId = machine.Id, CreatedById = ev.UserId, Harness = ev.Harness,
            WorkingDirectory = ev.WorkingDirectory.Trim(), Title = Sessions.TitleOf(ev.Prompt),
            PermissionMode = ev.PermissionMode, Model = string.IsNullOrWhiteSpace(ev.Model) ? null : ev.Model.Trim(),
            Status = SessionStatus.Starting, CreatedAt = ev.When, UpdatedAt = ev.When
        };
        Ambient.Relation<ISessionTable>().Insert(session);
        Sessions.AppendItem(session, SessionItemKind.User, ev.When, ev.Prompt);
        Sessions.IssueCommand(signals, machine.Id, new StartSessionCommand(session.Id, session.Harness,
            session.WorkingDirectory, ev.Prompt, session.PermissionMode, session.Model), ev.When);
        Sessions.Changed(signals, session, ev.When);
        return new() { Outcome = CommandOutcome.Ok, SessionId = session.Id };
    }
}

public sealed class SessionMessageSentHandler(IDomainSignals signals)
    : IEventHandler<SessionMessageSent, SessionCommandResult>
{
    public SessionCommandResult? Validate(SessionMessageSent ev) =>
        string.IsNullOrWhiteSpace(ev.Text) || ev.Text.Length > Sessions.MaximumPromptLength
            ? new() { Outcome = CommandOutcome.Invalid }
            : null;

    public SessionCommandResult Consume(SessionMessageSent ev)
    {
        var (session, machine, outcome) = Sessions.Controlled(ev.SessionId, ev.UserId);
        if (session == null) return new() { Outcome = outcome };
        if (session.Status is SessionStatus.Stopped) return new() { Outcome = CommandOutcome.Invalid };
        Sessions.AppendItem(session, SessionItemKind.User, ev.When, ev.Text);
        Sessions.IssueCommand(signals, machine!.Id, new SendMessageCommand(session.Id, ev.Text, ev.Mode), ev.When);
        Sessions.Changed(signals, session, ev.When);
        return new() { Outcome = CommandOutcome.Ok, SessionId = session.Id };
    }
}

public sealed class SessionInterruptRequestedHandler(IDomainSignals signals)
    : IEventHandler<SessionInterruptRequested, SessionCommandResult>
{
    public SessionCommandResult? Validate(SessionInterruptRequested ev) => null;

    public SessionCommandResult Consume(SessionInterruptRequested ev)
    {
        var (session, machine, outcome) = Sessions.Controlled(ev.SessionId, ev.UserId);
        if (session == null) return new() { Outcome = outcome };
        Sessions.IssueCommand(signals, machine!.Id, new InterruptCommand(session.Id), ev.When);
        return new() { Outcome = CommandOutcome.Ok, SessionId = session.Id };
    }
}

public sealed class SessionStopRequestedHandler(IDomainSignals signals)
    : IEventHandler<SessionStopRequested, SessionCommandResult>
{
    public SessionCommandResult? Validate(SessionStopRequested ev) => null;

    public SessionCommandResult Consume(SessionStopRequested ev)
    {
        var (session, machine, outcome) = Sessions.Controlled(ev.SessionId, ev.UserId);
        if (session == null) return new() { Outcome = outcome };
        Sessions.IssueCommand(signals, machine!.Id, new StopSessionCommand(session.Id), ev.When);
        return new() { Outcome = CommandOutcome.Ok, SessionId = session.Id };
    }
}

public sealed class RuntimeRequestAnsweredHandler(IDomainSignals signals)
    : IEventHandler<RuntimeRequestAnswered, SessionCommandResult>
{
    public SessionCommandResult? Validate(RuntimeRequestAnswered ev) =>
        ev.Message is { Length: > 10_000 } ? new() { Outcome = CommandOutcome.Invalid } : null;

    public SessionCommandResult Consume(RuntimeRequestAnswered ev)
    {
        var (session, machine, outcome) = Sessions.Controlled(ev.SessionId, ev.UserId);
        if (session == null) return new() { Outcome = outcome };
        var requests = Ambient.Relation<IRuntimeRequestTable>();
        var request = requests.FindByIdOrDefault(ev.SessionId, ev.RequestId);
        if (request == null) return new() { Outcome = CommandOutcome.NotFound };
        if (request.Status != RuntimeRequestStatus.Pending) return new() { Outcome = CommandOutcome.Invalid };
        request.Status = ev.Allow ? RuntimeRequestStatus.Allowed : RuntimeRequestStatus.Denied;
        request.AnsweredById = ev.UserId;
        request.AnsweredAt = ev.When;
        requests.Update(request);
        Sessions.IssueCommand(signals, machine!.Id,
            new AnswerApprovalCommand(session.Id, request.Key, ev.Allow, ev.Message), ev.When);
        Sessions.Changed(signals, session, ev.When);
        return new() { Outcome = CommandOutcome.Ok, SessionId = session.Id };
    }
}

public sealed class MachineCommandsAckedHandler : IEventHandler<MachineCommandsAcked>
{
    public void Validate(MachineCommandsAcked ev) { }

    public void Consume(MachineCommandsAcked ev)
    {
        var commands = Ambient.Relation<IMachineCommandTable>();
        foreach (var id in ev.CommandIds) commands.RemoveById(ev.MachineId, id);
    }
}

public sealed class DaemonReportsReceivedHandler(IDomainSignals signals) : IEventHandler<DaemonReportsReceived>
{
    public void Validate(DaemonReportsReceived ev) => Parse(ev);

    static OutboxRecord[] Parse(DaemonReportsReceived ev) =>
        JsonSerializer.Deserialize(ev.RecordsJson, ProtocolJsonContext.Default.OutboxRecordArray) ??
        throw new ArgumentException("No records.");

    public void Consume(DaemonReportsReceived ev)
    {
        var sessions = Ambient.Relation<ISessionTable>();
        var changed = new Dictionary<ulong, Session>();
        foreach (var record in Parse(ev))
        {
            if (!changed.TryGetValue(record.SessionId, out var session))
            {
                session = sessions.FindByIdOrDefault(record.SessionId);
                if (session == null || session.MachineId != ev.MachineId) continue;
                changed[session.Id] = session;
            }
            // In order and exactly once: duplicates from a resend are dropped, and so is anything after a gap,
            // which the daemon sends again because it is not acknowledged.
            if (record.Seq != session.LastReportSeq + 1) continue;
            session.LastReportSeq = record.Seq;
            Apply(session, record.Report, ev.When);
        }
        foreach (var session in changed.Values) Sessions.Changed(signals, session, ev.When);
    }

    static void Apply(Session session, SessionReport report, DateTime when)
    {
        switch (report)
        {
            case ItemReport item:
                Sessions.AppendItem(session, item.Kind, item.At, item.Text, item.ToolUseId, item.ToolName, item.Data,
                    item.IsError, item.Ref);
                break;
            case StatusReport status:
                session.Status = status.Status;
                if (status.HarnessSessionId != null) session.HarnessSessionId = status.HarnessSessionId;
                if (status.Model != null) session.Model = status.Model;
                if (status.Error != null) Sessions.AppendItem(session, SessionItemKind.Notice, when, status.Error, isError: true);
                // Approvals only wait inside a turn; a daemon that restarted cannot answer the old ones any more.
                if (status.Status is SessionStatus.Idle or SessionStatus.Stopped or SessionStatus.Failed)
                    CancelPending(session, when);
                break;
            case RunEndedReport ended:
                session.CostUsd += ended.CostUsd;
                Sessions.AppendItem(session, SessionItemKind.RunEnded, ended.At, ended.Outcome,
                    data: JsonSerializer.Serialize(report, ProtocolJsonContext.Default.SessionReport), isError: ended.IsError);
                break;
            case ApprovalRequestedReport approval:
                var requests = Ambient.Relation<IRuntimeRequestTable>();
                if (requests.FindByKeyOrDefault(session.Id, approval.Key) != null) break;
                requests.Insert(new()
                {
                    SessionId = session.Id, Id = Ambient.AllocateId(), Key = approval.Key, ToolName = approval.ToolName,
                    ToolUseId = approval.ToolUseId, Input = approval.Input, Description = approval.Description,
                    Status = RuntimeRequestStatus.Pending, RaisedAt = approval.At
                });
                // The daemon reports its status separately, possibly in a later batch: a pending approval already
                // means the turn waits, so readers never see the request beside a running session.
                session.Status = SessionStatus.WaitingForApproval;
                break;
            case ApprovalClosedReport closed:
                var all = Ambient.Relation<IRuntimeRequestTable>();
                if (all.FindByKeyOrDefault(session.Id, closed.Key) is { Status: RuntimeRequestStatus.Pending } pending)
                {
                    pending.Status = RuntimeRequestStatus.Cancelled;
                    pending.AnsweredAt = when;
                    all.Update(pending);
                }
                break;
        }
    }

    static void CancelPending(Session session, DateTime when)
    {
        var requests = Ambient.Relation<IRuntimeRequestTable>();
        foreach (var request in requests.ListById(session.Id).Where(r => r.Status == RuntimeRequestStatus.Pending).ToList())
        {
            request.Status = RuntimeRequestStatus.Cancelled;
            request.AnsweredAt = when;
            requests.Update(request);
        }
    }
}
