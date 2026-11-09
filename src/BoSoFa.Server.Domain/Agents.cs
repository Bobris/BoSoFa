using System.Text.Json;
using BoSoFa.Protocol;
using BTDB.ODBLayer;
using BTDB.Replication.EventSourcing;

namespace BoSoFa.Server.Domain;

/// <summary>The node running an agent session's loop (docs/server-agent.md §4.2). A row exists only while the session has
/// work: a run in progress, or a run waiting for a person.</summary>
public class AgentRunner
{
    [PrimaryKey(1)] public ulong SessionId { get; set; }
    public string NodeId { get; set; } = "";

    /// <summary>Where other nodes reach the running node for its live deltas; empty in single-node mode.</summary>
    public string PeerUrl { get; set; } = "";

    public DateTime ClaimedAt { get; set; }

    /// <summary>Raised by every write of the loop. A write that names an older version is a repeat or comes from a node
    /// that lost the session, and is refused.</summary>
    public long Version { get; set; }

    public bool InterruptRequested { get; set; }
    public DateTime RunStartedAt { get; set; }
    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long CacheWriteTokens { get; set; }
    public long OutputTokens { get; set; }
    public long ReasoningTokens { get; set; }

    /// <summary>What this run has cost so far, at list prices.</summary>
    public double CostUsd { get; set; }
}

public interface IAgentRunnerTable : IRelation<AgentRunner>
{
    void Insert(AgentRunner runner);
    void Update(AgentRunner runner);
    AgentRunner? FindByIdOrDefault(ulong sessionId);
    bool RemoveById(ulong sessionId);
}

/// <summary>The loop recorded what a step produced: a model response, a tool's result, or notices.</summary>
public class AgentItemsRecorded : EventWithResult<SessionCommandResult>
{
    public ulong SessionId { get; set; }
    public string NodeId { get; set; } = "";
    public long Version { get; set; }

    /// <summary><see cref="AgentItem"/>[] as protocol JSON.</summary>
    public string ItemsJson { get; set; } = "";

    public long InputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long CacheWriteTokens { get; set; }
    public long OutputTokens { get; set; }
    public long ReasoningTokens { get; set; }

    /// <summary>Worked out by the loop, which knows the model's prices; handlers must not read a price list.</summary>
    public double CostUsd { get; set; }

    /// <summary>Set by a model call: how full the context is now.</summary>
    public long ContextTokens { get; set; }

    public long ContextLimit { get; set; }

    /// <summary>How long what this model call cached will last; zero when little is cached.</summary>
    public int CacheSeconds { get; set; }
}

/// <summary>A tool call waits for a person. <see cref="Key"/> comes from the call's place in the transcript, so a resumed loop finds the same
/// request, and a provider that reuses its call ids cannot answer a new call with an old approval.</summary>
public class AgentApprovalRaised : EventWithResult<SessionCommandResult>
{
    public ulong SessionId { get; set; }
    public string NodeId { get; set; } = "";
    public long Version { get; set; }
    public string Key { get; set; } = "";

    /// <summary>The provider's id of the call, for showing.</summary>
    public string? ToolUseId { get; set; }

    public string ToolName { get; set; } = "";
    public string Input { get; set; } = "";
    public string? Description { get; set; }
}

/// <summary>The loop has nothing more to do for this run.</summary>
public class AgentRunFinished : EventWithResult<SessionCommandResult>
{
    public ulong SessionId { get; set; }
    public string NodeId { get; set; } = "";
    public long Version { get; set; }

    /// <summary>The newest item the loop had seen: a person's message after it may keep the run going.</summary>
    public long SeenSeq { get; set; }

    public string Outcome { get; set; } = "";
    public bool IsError { get; set; }
    public string? Error { get; set; }
    public bool Interrupted { get; set; }

    /// <summary>Messages the loop already saw are waiting for this run to end, so the next run follows at once.</summary>
    public bool HasQueued { get; set; }

    /// <summary>Items to store first (<see cref="AgentItem"/>[]): results for calls that will never run.</summary>
    public string ItemsJson { get; set; } = "";
}

/// <summary>A live node takes over the sessions of a node that left the cluster.</summary>
public class AgentRunClaimed : EventWithResult<SessionCommandResult>
{
    public ulong SessionId { get; set; }
    public string NodeId { get; set; } = "";
    public string PeerUrl { get; set; } = "";
    public string PreviousNodeId { get; set; } = "";
}

static class AgentRunners
{
    /// <summary>Claims the session for the node that handled this message, unless a node already runs it.</summary>
    public static void Claim(IDomainSignals signals, Session session, string nodeId, string peerUrl, DateTime when)
    {
        var runners = Ambient.Relation<IAgentRunnerTable>();
        if (runners.FindByIdOrDefault(session.Id) != null) return;
        runners.Insert(new() { SessionId = session.Id, NodeId = nodeId, PeerUrl = peerUrl, ClaimedAt = when, RunStartedAt = when });
        session.Status = SessionStatus.Running;
        RunnerChanged(signals, session.Id);
    }

    public static void RunnerChanged(IDomainSignals signals, ulong sessionId) =>
        Ambient.RegisterPostCommit(() => signals.AgentRunnerChanged(sessionId));

    /// <summary>The session and its runner when this write comes from the node that holds it and is the next one.</summary>
    public static (Session? Session, AgentRunner? Runner) Held(ulong sessionId, string nodeId, long version)
    {
        if (Ambient.Relation<IAgentRunnerTable>().FindByIdOrDefault(sessionId) is not { } runner ||
            runner.NodeId != nodeId || runner.Version != version)
            return (null, null);
        return Ambient.Relation<ISessionTable>().FindByIdOrDefault(sessionId) is { } session ? (session, runner) : (null, null);
    }

    public static AgentItem[] Items(string json) => string.IsNullOrEmpty(json)
        ? []
        : JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.AgentItemArray) ?? [];

    public static void Append(Session session, AgentItem[] items, DateTime when)
    {
        foreach (var item in items)
            Sessions.AppendItem(session, item.Kind, when, item.Text, item.ToolUseId, item.ToolName, item.Data, item.IsError, item.Ref,
                item.Replay);
    }

    /// <summary>What the loop needs of a person's message beyond the shown text; null when that is nothing.</summary>
    public static string? UserReplay(string text, bool queued)
    {
        var full = text.Length > ItemReport.InlineLimit ? text : null;
        return full == null && !queued
            ? null
            : JsonSerializer.Serialize(new AgentUserReplay(full, queued), ProtocolJsonContext.Default.AgentUserReplay);
    }

    public static bool IsQueued(SessionItem item) => item.Replay is { } replay &&
        JsonSerializer.Deserialize(replay, ProtocolJsonContext.Default.AgentUserReplay)?.Queued == true;
}

public sealed class AgentItemsRecordedHandler(IDomainSignals signals) : IEventHandler<AgentItemsRecorded, SessionCommandResult>
{
    public SessionCommandResult? Validate(AgentItemsRecorded ev)
    {
        AgentRunners.Items(ev.ItemsJson);
        return null;
    }

    public SessionCommandResult Consume(AgentItemsRecorded ev)
    {
        var (session, runner) = AgentRunners.Held(ev.SessionId, ev.NodeId, ev.Version);
        if (session == null) return new() { Outcome = CommandOutcome.Invalid };
        AgentRunners.Append(session, AgentRunners.Items(ev.ItemsJson), ev.When);
        runner!.Version++;
        runner.InputTokens += ev.InputTokens;
        runner.CachedInputTokens += ev.CachedInputTokens;
        runner.CacheWriteTokens += ev.CacheWriteTokens;
        runner.OutputTokens += ev.OutputTokens;
        runner.ReasoningTokens += ev.ReasoningTokens;
        runner.CostUsd += ev.CostUsd;
        session.CostUsd += ev.CostUsd; // so a running session shows what it has cost so far
        if (ev.ContextTokens > 0)
        {
            session.ContextTokens = ev.ContextTokens;
            session.ContextLimit = ev.ContextLimit;
            session.CacheExpiresAt = ev.CacheSeconds > 0 ? ev.When.AddSeconds(ev.CacheSeconds) : null;
        }
        Ambient.Relation<IAgentRunnerTable>().Update(runner);
        Sessions.Changed(signals, session, ev.When);
        return new() { Outcome = CommandOutcome.Ok, SessionId = session.Id };
    }
}

public sealed class AgentApprovalRaisedHandler(IDomainSignals signals) : IEventHandler<AgentApprovalRaised, SessionCommandResult>
{
    public SessionCommandResult? Validate(AgentApprovalRaised ev) =>
        string.IsNullOrEmpty(ev.Key) ? new() { Outcome = CommandOutcome.Invalid } : null;

    public SessionCommandResult Consume(AgentApprovalRaised ev)
    {
        var (session, runner) = AgentRunners.Held(ev.SessionId, ev.NodeId, ev.Version);
        if (session == null) return new() { Outcome = CommandOutcome.Invalid };
        var requests = Ambient.Relation<IRuntimeRequestTable>();
        if (requests.FindByKeyOrDefault(session.Id, ev.Key) == null)
            requests.Insert(new()
            {
                SessionId = session.Id, Id = Ambient.AllocateId(), Key = ev.Key, ToolName = ev.ToolName, ToolUseId = ev.ToolUseId ?? ev.Key,
                Input = ev.Input, Description = ev.Description, Status = RuntimeRequestStatus.Pending, RaisedAt = ev.When
            });
        session.Status = SessionStatus.WaitingForApproval;
        runner!.Version++;
        Ambient.Relation<IAgentRunnerTable>().Update(runner);
        Sessions.Changed(signals, session, ev.When);
        return new() { Outcome = CommandOutcome.Ok, SessionId = session.Id };
    }
}

public sealed class AgentRunFinishedHandler(IDomainSignals signals) : IEventHandler<AgentRunFinished, SessionCommandResult>
{
    public SessionCommandResult? Validate(AgentRunFinished ev)
    {
        AgentRunners.Items(ev.ItemsJson);
        return string.IsNullOrEmpty(ev.Outcome) ? new() { Outcome = CommandOutcome.Invalid } : null;
    }

    public SessionCommandResult Consume(AgentRunFinished ev)
    {
        var (session, runner) = AgentRunners.Held(ev.SessionId, ev.NodeId, ev.Version);
        if (session == null) return new() { Outcome = CommandOutcome.Invalid };
        var newer = Ambient.Relation<ISessionItemTable>().ListById(session.Id, new AdvancedEnumeratorParam<long>(
                EnumerationOrder.Ascending, ev.SeenSeq, KeyProposition.Excluded, 0, KeyProposition.Ignored))
            .Where(item => item.Kind == SessionItemKind.User).ToList();
        // A steering message that came in while the run was ending keeps it going; the model answers it next.
        if (!ev.Interrupted && newer.Any(item => !AgentRunners.IsQueued(item))) return new() { Outcome = CommandOutcome.Ok, SessionId = session.Id };

        var runners = Ambient.Relation<IAgentRunnerTable>();
        AgentRunners.Append(session, AgentRunners.Items(ev.ItemsJson), ev.When);
        if (ev.Error != null) Sessions.AppendItem(session, SessionItemKind.Notice, ev.When, ev.Error, isError: true);
        var report = new RunEndedReport(ev.When, ev.Outcome, ev.IsError, runner!.CostUsd, runner.InputTokens, runner.OutputTokens,
            (long)(ev.When - runner.RunStartedAt).TotalMilliseconds);
        Sessions.AppendItem(session, SessionItemKind.RunEnded, ev.When, ev.Outcome,
            data: JsonSerializer.Serialize<SessionReport>(report, ProtocolJsonContext.Default.SessionReport), isError: ev.IsError);
        Sessions.CancelPending(session, ev.When);
        if (newer.Count > 0 || ev.HasQueued)
        {
            // Queued messages start the next run on the same node.
            runner.Version++;
            runner.InterruptRequested = false;
            runner.RunStartedAt = ev.When;
            runner.InputTokens = runner.CachedInputTokens = runner.CacheWriteTokens = runner.OutputTokens = runner.ReasoningTokens = 0;
            runner.CostUsd = 0;
            runners.Update(runner);
            session.Status = SessionStatus.Running;
        }
        else
        {
            runners.RemoveById(session.Id);
            session.Status = SessionStatus.Idle;
            AgentRunners.RunnerChanged(signals, session.Id);
        }
        Sessions.Changed(signals, session, ev.When);
        return new() { Outcome = CommandOutcome.Ok, SessionId = session.Id };
    }
}

public sealed class AgentRunClaimedHandler(IDomainSignals signals) : IEventHandler<AgentRunClaimed, SessionCommandResult>
{
    public SessionCommandResult? Validate(AgentRunClaimed ev) =>
        string.IsNullOrEmpty(ev.NodeId) ? new() { Outcome = CommandOutcome.Invalid } : null;

    public SessionCommandResult Consume(AgentRunClaimed ev)
    {
        var runners = Ambient.Relation<IAgentRunnerTable>();
        if (runners.FindByIdOrDefault(ev.SessionId) is not { } runner || runner.NodeId != ev.PreviousNodeId ||
            Ambient.Relation<ISessionTable>().FindByIdOrDefault(ev.SessionId) is not { } session)
            return new() { Outcome = CommandOutcome.Invalid };
        runner.NodeId = ev.NodeId;
        runner.PeerUrl = ev.PeerUrl;
        runner.ClaimedAt = ev.When;
        runner.Version++;
        runners.Update(runner);
        AgentRunners.RunnerChanged(signals, session.Id);
        Sessions.Changed(signals, session, ev.When);
        return new() { Outcome = CommandOutcome.Ok, SessionId = session.Id };
    }
}
