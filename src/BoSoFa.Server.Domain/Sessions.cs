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

    /// <summary>The harness account the session runs with; 0 means the machine's own sign-in.</summary>
    public ulong AccountId { get; set; }

    /// <summary>The session's own branch when it runs in a worktree (then <see cref="WorkingDirectory"/> is that worktree).</summary>
    public string? Branch { get; set; }

    /// <summary>The profile whose MCP servers and skills the session gets; null for none.</summary>
    public string? ProfileName { get; set; }
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

    /// <summary>The agent loop's own form of this item, kept whole for the next model call: JSON, or <c>blob:</c> and a
    /// hash above the inline limit. It holds provider-opaque data, so it never leaves the server.</summary>
    public string? Replay { get; set; }

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

    /// <summary>What the person added to an agent session's request; the loop passes it to the model.</summary>
    public string? Answer { get; set; }
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

    /// <summary>An agent session changed; the node running it looks at what to do next.</summary>
    void AgentSessionChanged(ulong sessionId);

    /// <summary>The node running an agent session changed, or none runs it any more.</summary>
    void AgentRunnerChanged(ulong sessionId);
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
    public ulong AccountId { get; set; }
    public bool UseWorktree { get; set; }
    public string? ProfileName { get; set; }

    /// <summary>The prompt's images as JSON (<see cref="PromptImage"/>[]); empty without any.</summary>
    public string ImagesJson { get; set; } = "";

    /// <summary>For an agent session: the node handling this request, which runs the session.</summary>
    public string RunnerNodeId { get; set; } = "";

    public string RunnerPeerUrl { get; set; } = "";
}

public class SessionMessageSent : EventWithResult<SessionCommandResult>
{
    public ulong SessionId { get; set; }
    public ulong UserId { get; set; }
    public string Text { get; set; } = "";
    public DispatchMode Mode { get; set; }

    /// <summary>The message's images as JSON (<see cref="PromptImage"/>[]); empty without any.</summary>
    public string ImagesJson { get; set; } = "";

    /// <summary>For an agent session that nothing runs right now: the node handling this request takes it.</summary>
    public string RunnerNodeId { get; set; } = "";

    public string RunnerPeerUrl { get; set; } = "";
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

public static class SessionAccess
{
    /// <summary>Only its creator sees a session: its transcript, requests and live updates (decided 2026-11-09).</summary>
    public static bool CanSee(ulong userId, Session session) => userId != 0 && session.CreatedById == userId;
}

static class Sessions
{
    public const int MaximumPromptLength = 100_000;

    /// <summary>The item's data when it has images, so the transcript can show them.</summary>
    public static string? ImagesData(string? imagesJson) => string.IsNullOrEmpty(imagesJson) ? null : imagesJson;

    public static PromptImage[]? Images(string? imagesJson) => string.IsNullOrEmpty(imagesJson)
        ? null
        : JsonSerializer.Deserialize(imagesJson, ProtocolJsonContext.Default.PromptImageArray);


    public static long AppendItem(Session session, SessionItemKind kind, DateTime at, string text, string? toolUseId = null,
        string? toolName = null, string? data = null, bool isError = false, string? itemRef = null, string? replay = null)
    {
        session.LastSeq++;
        Ambient.Relation<ISessionItemTable>().Insert(new()
        {
            SessionId = session.Id, Seq = session.LastSeq, Kind = kind, At = at, Text = Cut(text), ToolUseId = toolUseId,
            ToolName = toolName, Data = data == null ? null : Cut(data), IsError = isError, Ref = itemRef, Replay = replay
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
        var isAgent = session.Harness == Harnesses.Agent;
        Ambient.RegisterPostCommit(() =>
        {
            signals.SessionChanged(id);
            if (isAgent) signals.AgentSessionChanged(id);
        });
    }

    public static void CancelPending(Session session, DateTime when)
    {
        var requests = Ambient.Relation<IRuntimeRequestTable>();
        foreach (var request in requests.ListById(session.Id).Where(r => r.Status == RuntimeRequestStatus.Pending).ToList())
        {
            request.Status = RuntimeRequestStatus.Cancelled;
            request.AnsweredAt = when;
            requests.Update(request);
        }
    }

    /// <summary>The session and its machine when the user may control it: their own session, on a machine they may still use.
    /// Someone else's session is not found, so its existence does not show.</summary>
    public static (Session? Session, Machine? Machine, CommandOutcome Outcome) Controlled(ulong sessionId, ulong userId)
    {
        var session = Ambient.Relation<ISessionTable>().FindByIdOrDefault(sessionId);
        if (session == null || !SessionAccess.CanSee(userId, session)) return (null, null, CommandOutcome.NotFound);
        if (session.Harness == Harnesses.Agent && session.MachineId == 0) return (session, null, CommandOutcome.Ok);
        var machine = Ambient.Relation<IMachineTable>().FindByIdOrDefault(session.MachineId);
        if (machine == null) return (null, null, CommandOutcome.NotFound);
        return MachineAccess.CanControl(userId, machine) ? (session, machine, CommandOutcome.Ok) : (null, null, CommandOutcome.Forbidden);
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
        !Harnesses.All.Contains(ev.Harness) || !Harnesses.PermissionModes.Contains(ev.PermissionMode) ||
        (ev.Harness == Harnesses.Agent
            ? string.IsNullOrWhiteSpace(ev.Model) || ev.AccountId == 0 || ev.RunnerNodeId.Length == 0 || ev.UseWorktree ||
              (ev.MachineId != 0 && string.IsNullOrWhiteSpace(ev.WorkingDirectory))
            : string.IsNullOrWhiteSpace(ev.WorkingDirectory))
            ? new() { Outcome = CommandOutcome.Invalid }
            : null;

    /// <summary>A session the server's own loop runs on the creator's provider account: its tools run on a machine they control, or
    /// without one the profile must need no process on a machine.</summary>
    SessionCommandResult ConsumeAgent(SessionCreated ev)
    {
        var account = Ambient.Relation<IHarnessAccountTable>().FindByIdOrDefault(ev.AccountId);
        if (account == null) return new() { Outcome = CommandOutcome.NotFound };
        if (account.OwnerId != ev.UserId) return new() { Outcome = CommandOutcome.Forbidden };
        if (!AccountSecrets.ServesAgent(account.Kind)) return new() { Outcome = CommandOutcome.Invalid };
        if (ev.MachineId != 0)
        {
            if (Ambient.Relation<IMachineTable>().FindByIdOrDefault(ev.MachineId) is not { } machine) return new() { Outcome = CommandOutcome.NotFound };
            if (!MachineAccess.CanControl(ev.UserId, machine)) return new() { Outcome = CommandOutcome.Forbidden };
        }
        if (ev.ProfileName != null)
        {
            if (Ambient.Relation<IProfileTable>().FindByIdOrDefault(ev.ProfileName) is not { } profile) return new() { Outcome = CommandOutcome.NotFound };
            var servers = Ambient.Relation<IMcpServerTable>();
            if (ev.MachineId == 0 && JsonSerializer.Deserialize(profile.McpServers, ProtocolJsonContext.Default.StringArray)!
                .Any(name => servers.FindByIdOrDefault(name) is not { Transport: McpTransport.Http }))
                return new() { Outcome = CommandOutcome.Invalid };
        }
        var session = new Session
        {
            Id = Ambient.AllocateId(), MachineId = ev.MachineId, WorkingDirectory = ev.MachineId == 0 ? "" : ev.WorkingDirectory.Trim(),
            CreatedById = ev.UserId, Harness = ev.Harness, Title = Sessions.TitleOf(ev.Prompt),
            PermissionMode = ev.PermissionMode, Model = ev.Model!.Trim(), Status = SessionStatus.Running, CreatedAt = ev.When,
            UpdatedAt = ev.When, AccountId = ev.AccountId, ProfileName = ev.ProfileName
        };
        Ambient.Relation<ISessionTable>().Insert(session);
        Sessions.AppendItem(session, SessionItemKind.User, ev.When, ev.Prompt, replay: AgentRunners.UserReplay(ev.Prompt, false));
        AgentRunners.Claim(signals, session, ev.RunnerNodeId, ev.RunnerPeerUrl, ev.When);
        Sessions.Changed(signals, session, ev.When);
        Audit.Record(ev.When, ev.UserId, "session.created", $"session {session.Id}", $"{ev.Harness} with {session.Model}");
        return new() { Outcome = CommandOutcome.Ok, SessionId = session.Id };
    }

    public SessionCommandResult Consume(SessionCreated ev)
    {
        if (ev.Harness == Harnesses.Agent) return ConsumeAgent(ev);
        var machine = Ambient.Relation<IMachineTable>().FindByIdOrDefault(ev.MachineId);
        if (machine == null) return new() { Outcome = CommandOutcome.NotFound };
        if (!MachineAccess.CanControl(ev.UserId, machine)) return new() { Outcome = CommandOutcome.Forbidden };
        if (ev.AccountId != 0)
        {
            // Only the creator's own account, and one for this harness (the replay plays Claude Code).
            var account = Ambient.Relation<IHarnessAccountTable>().FindByIdOrDefault(ev.AccountId);
            if (account == null) return new() { Outcome = CommandOutcome.NotFound };
            var harness = ev.Harness == Harnesses.Replay ? Harnesses.Claude : ev.Harness;
            if (account.OwnerId != ev.UserId) return new() { Outcome = CommandOutcome.Forbidden };
            if (AccountSecrets.HarnessOf(account.Kind) != harness) return new() { Outcome = CommandOutcome.Invalid };
        }
        if (ev.ProfileName != null && Ambient.Relation<IProfileTable>().FindByIdOrDefault(ev.ProfileName) == null)
            return new() { Outcome = CommandOutcome.NotFound };
        var session = new Session
        {
            Id = Ambient.AllocateId(), MachineId = machine.Id, CreatedById = ev.UserId, Harness = ev.Harness,
            WorkingDirectory = ev.WorkingDirectory.Trim(), Title = Sessions.TitleOf(ev.Prompt),
            PermissionMode = ev.PermissionMode, Model = string.IsNullOrWhiteSpace(ev.Model) ? null : ev.Model.Trim(),
            Status = SessionStatus.Starting, CreatedAt = ev.When, UpdatedAt = ev.When, AccountId = ev.AccountId,
            ProfileName = ev.ProfileName
        };
        Ambient.Relation<ISessionTable>().Insert(session);
        Sessions.AppendItem(session, SessionItemKind.User, ev.When, ev.Prompt, data: Sessions.ImagesData(ev.ImagesJson));
        Sessions.IssueCommand(signals, machine.Id, new StartSessionCommand(session.Id, session.Harness,
            session.WorkingDirectory, ev.Prompt, session.PermissionMode, session.Model,
            session.AccountId == 0 ? null : session.AccountId, ev.UseWorktree, ev.ProfileName != null, Sessions.Images(ev.ImagesJson)), ev.When);
        Sessions.Changed(signals, session, ev.When);
        Audit.Record(ev.When, ev.UserId, "session.created", $"session {session.Id}", $"{ev.Harness} in {ev.WorkingDirectory} on machine {ev.MachineId}");
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
        if (session.Harness == Harnesses.Agent)
        {
            if (ev.RunnerNodeId.Length == 0) return new() { Outcome = CommandOutcome.Invalid };
            Sessions.AppendItem(session, SessionItemKind.User, ev.When, ev.Text, replay: AgentRunners.UserReplay(ev.Text, ev.Mode == DispatchMode.Queue));
            AgentRunners.Claim(signals, session, ev.RunnerNodeId, ev.RunnerPeerUrl, ev.When);
            Sessions.Changed(signals, session, ev.When);
            return new() { Outcome = CommandOutcome.Ok, SessionId = session.Id };
        }
        Sessions.AppendItem(session, SessionItemKind.User, ev.When, ev.Text, data: Sessions.ImagesData(ev.ImagesJson));
        Sessions.IssueCommand(signals, machine!.Id, new SendMessageCommand(session.Id, ev.Text, ev.Mode, Sessions.Images(ev.ImagesJson)), ev.When);
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
        if (session.Harness == Harnesses.Agent)
        {
            // Kept in the row, so a node taking over still honors it.
            var runners = Ambient.Relation<IAgentRunnerTable>();
            if (runners.FindByIdOrDefault(session.Id) is { InterruptRequested: false } runner)
            {
                runner.InterruptRequested = true;
                runners.Update(runner);
                Sessions.Changed(signals, session, ev.When);
            }
            return new() { Outcome = CommandOutcome.Ok, SessionId = session.Id };
        }
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
        if (session.Harness == Harnesses.Agent)
        {
            if (Ambient.Relation<IAgentRunnerTable>().RemoveById(session.Id)) AgentRunners.RunnerChanged(signals, session.Id);
            Sessions.CancelPending(session, ev.When);
            session.Status = SessionStatus.Stopped;
            Sessions.Changed(signals, session, ev.When);
            Audit.Record(ev.When, ev.UserId, "session.stopped", $"session {session.Id}");
            return new() { Outcome = CommandOutcome.Ok, SessionId = session.Id };
        }
        Sessions.IssueCommand(signals, machine!.Id, new StopSessionCommand(session.Id), ev.When);
        Audit.Record(ev.When, ev.UserId, "session.stopped", $"session {session.Id}");
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
        if (session.Harness == Harnesses.Agent)
        {
            request.Answer = ev.Message;
            requests.Update(request);
            if (!requests.ListById(session.Id).Any(other => other.Status == RuntimeRequestStatus.Pending)) session.Status = SessionStatus.Running;
        }
        else
            Sessions.IssueCommand(signals, machine!.Id,
                new AnswerApprovalCommand(session.Id, request.Key, ev.Allow, ev.Message), ev.When);
        Sessions.Changed(signals, session, ev.When);
        Audit.Record(ev.When, ev.UserId, ev.Allow ? "approval.allowed" : "approval.denied", $"session {session.Id}", request.ToolName);
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
            // The daemon was told these seqs were stored and they are not: this server was restored from a backup, and
            // waiting for them would freeze the session. (A session with no reports yet is a new one with a reused ID.)
            if (record.Seq > session.LastReportSeq + 1 && record.Acked > session.LastReportSeq)
            {
                if (session.LastReportSeq > 0)
                    Sessions.AppendItem(session, SessionItemKind.Notice, ev.When,
                        "Part of this session was lost when the server was restored from a backup.");
                session.LastReportSeq = record.Seq - 1;
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
                    Sessions.CancelPending(session, when);
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
            case AccountUsageReport usage:
                AccountUsages.Record(session.AccountId, usage);
                break;
            case WorkspaceReport workspace:
                session.WorkingDirectory = workspace.Path;
                session.Branch = workspace.Branch;
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
}
