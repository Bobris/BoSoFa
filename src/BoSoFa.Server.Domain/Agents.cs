using System.Text.Json;
using BoSoFa.Protocol;
using BTDB;
using BTDB.BackendActions;
using BTDB.ODBLayer;
using BTDB.Replication.EventSourcing;

namespace BoSoFa.Server.Domain;

/// <summary>The backend action that runs an agent session's loop until its run ends or waits only for a person
/// (docs/server-agent.md §4.2). The tenant is the session's creator, so people share the workers fairly. A run mostly waits
/// for the model, so it counts as little CPU as an action can.</summary>
[BackendAction(Name = "agent-run", Cpu = 10)]
public class AgentRun : ActionInput<AgentRunResult>
{
    public ulong SessionId { get; set; }
}

[Generate]
public class AgentRunResult
{
    /// <summary>The loop stopped because the run waits for a person.</summary>
    public bool Waiting { get; set; }
}

/// <summary>An agent session's run (docs/server-agent.md §4.2). A row exists only while the session has work: a run in progress,
/// or a run waiting for a person.</summary>
public class AgentRunner
{
    [PrimaryKey(1)] public ulong SessionId { get; set; }

    /// <summary>The node running the attempt in <see cref="Attempt"/>; empty until one attaches.</summary>
    public string NodeId { get; set; } = "";

    /// <summary>Where other nodes reach the running node for its live deltas; empty in single-node mode.</summary>
    public string PeerUrl { get; set; } = "";

    public DateTime ClaimedAt { get; set; }

    /// <summary>Raised by every write of the loop. A write that names an older version is a repeat or comes from an attempt
    /// that lost the session, and is refused.</summary>
    public long Version { get; set; }

    /// <summary>The backend action (of the session creator's tenant) that runs the loop or is about to; 0 while the run waits
    /// only for a person.</summary>
    public ulong ActionId { get; set; }

    /// <summary>The action's attempt whose loop may write; 0 until one attaches.</summary>
    public ulong Attempt { get; set; }

    /// <summary>The previous action's loop stopped by itself, so nothing it did was cut off; a new attempt of an action
    /// clears it.</summary>
    public bool EndedCleanly { get; set; }

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
    public ulong ActionId { get; set; }
    public ulong Attempt { get; set; }

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
    public ulong ActionId { get; set; }
    public ulong Attempt { get; set; }
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
    public ulong ActionId { get; set; }
    public ulong Attempt { get; set; }

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

/// <summary>An attempt of the session's backend action starts its loop on a node; the loops of older attempts may no longer write.</summary>
public class AgentRunAttached : EventWithResult<AgentRunAttachResult>
{
    public ulong SessionId { get; set; }
    public ulong ActionId { get; set; }
    public ulong Attempt { get; set; }
    public string NodeId { get; set; } = "";
    public string PeerUrl { get; set; } = "";
}

public class AgentRunAttachResult
{
    public bool Attached { get; set; }

    /// <summary>The previous loop stopped by itself: an open call it approved never started.</summary>
    public bool Clean { get; set; }
}

/// <summary>Published by a new leader: every run with work but no backend action gets one. Covers runs claimed before backend
/// actions ran them, and an action lost for any other reason.</summary>
public class AgentRunsResumed : PureEvent
{
}

/// <summary>Before backend actions ran the loops: a live node took over the sessions of a node that left the cluster. No longer
/// published; kept so stored events still apply.</summary>
public class AgentRunClaimed : EventWithResult<SessionCommandResult>
{
    public ulong SessionId { get; set; }
    public string NodeId { get; set; } = "";
    public string PeerUrl { get; set; } = "";
    public string PreviousNodeId { get; set; } = "";
}

public static class AgentRunners
{
    /// <summary>A loop that failed starts again after this.</summary>
    static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(5);

    /// <summary>Starts a run for this message unless one is in progress.</summary>
    public static void Claim(IDomainSignals signals, BackendActions actions, Session session, DateTime when)
    {
        var runners = Ambient.Relation<IAgentRunnerTable>();
        if (runners.FindByIdOrDefault(session.Id) != null) return;
        runners.Insert(new()
        {
            SessionId = session.Id, ClaimedAt = when, RunStartedAt = when, EndedCleanly = true, ActionId = Start(actions, session, null)
        });
        session.Status = SessionStatus.Running;
        RunnerChanged(signals, session.Id);
    }

    static ulong Start(BackendActions actions, Session session, DateTime? startAt) =>
        actions.Enqueue(new AgentRun { TenantId = session.CreatedById, SessionId = session.Id }, startAt: startAt).ActionId;

    public static QualifiedActionId ActionOf(Session session, AgentRunner runner) => new(session.CreatedById, runner.ActionId);

    /// <summary>Whether the loop has something to do: a run that waits only for a person's answer has nothing.</summary>
    static bool NeedsLoop(Session session, AgentRunner runner) => runner.InterruptRequested ||
        !Ambient.Relation<IRuntimeRequestTable>().ListById(session.Id).Any(request => request.Status == RuntimeRequestStatus.Pending);

    /// <summary>The run got something to do: it gets a backend action unless it has one.</summary>
    public static void Ensure(BackendActions actions, Session session)
    {
        var runners = Ambient.Relation<IAgentRunnerTable>();
        if (runners.FindByIdOrDefault(session.Id) is not { ActionId: 0 } runner || !NeedsLoop(session, runner)) return;
        runner.ActionId = Start(actions, session, null);
        runners.Update(runner);
    }

    /// <summary>The action stopped: the run has no action now, or the next one when it still has something to do. Outcomes of
    /// an action the run no longer has change nothing.</summary>
    public static void Finished(IDomainSignals signals, BackendActions actions, AgentRun input, ActionOutcomeKind kind, DateTime when)
    {
        if (kind == ActionOutcomeKind.Progress) return;
        var runners = Ambient.Relation<IAgentRunnerTable>();
        if (runners.FindByIdOrDefault(input.SessionId) is not { } runner || runner.ActionId != input.ActionId) return;
        if (Ambient.Relation<ISessionTable>().FindByIdOrDefault(input.SessionId) is not { } session)
        {
            runners.RemoveById(input.SessionId);
            return;
        }
        Detach(runner);
        runner.EndedCleanly = kind == ActionOutcomeKind.Completed;
        if (NeedsLoop(session, runner)) runner.ActionId = Start(actions, session, kind == ActionOutcomeKind.Completed ? null : when + RetryAfterFailure);
        runners.Update(runner);
        RunnerChanged(signals, session.Id);
    }

    static void Detach(AgentRunner runner)
    {
        runner.ActionId = runner.Attempt = 0;
        runner.NodeId = runner.PeerUrl = "";
    }

    /// <summary>Gives every run with work an action, unless it has one that still exists.</summary>
    public static void Resume(IDomainSignals signals, BackendActions actions)
    {
        var runners = Ambient.Relation<IAgentRunnerTable>();
        foreach (var runner in runners.ToList())
        {
            if (Ambient.Relation<ISessionTable>().FindByIdOrDefault(runner.SessionId) is not { } session ||
                (runner.ActionId != 0 && actions.FindAction(ActionOf(session, runner)) != null))
                continue;
            Detach(runner);
            if (NeedsLoop(session, runner)) runner.ActionId = Start(actions, session, null);
            runners.Update(runner);
            RunnerChanged(signals, session.Id);
        }
    }

    public static void RunnerChanged(IDomainSignals signals, ulong sessionId) =>
        Ambient.RegisterPostCommit(() => signals.AgentRunnerChanged(sessionId));

    /// <summary>The session and its runner when this write comes from the attempt that holds it and is the next one.</summary>
    public static (Session? Session, AgentRunner? Runner) Held(ulong sessionId, ulong actionId, ulong attempt, long version)
    {
        if (Ambient.Relation<IAgentRunnerTable>().FindByIdOrDefault(sessionId) is not { } runner ||
            runner.ActionId != actionId || runner.Attempt != attempt || attempt == 0 || runner.Version != version)
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
        var (session, runner) = AgentRunners.Held(ev.SessionId, ev.ActionId, ev.Attempt, ev.Version);
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
        var (session, runner) = AgentRunners.Held(ev.SessionId, ev.ActionId, ev.Attempt, ev.Version);
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
        var (session, runner) = AgentRunners.Held(ev.SessionId, ev.ActionId, ev.Attempt, ev.Version);
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
            // Queued messages start the next run in the same loop.
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

public sealed class AgentRunAttachedHandler(IDomainSignals signals, BackendActions actions) : IEventHandler<AgentRunAttached, AgentRunAttachResult>
{
    public AgentRunAttachResult? Validate(AgentRunAttached ev) => ev.ActionId == 0 || ev.Attempt == 0 ? new() : null;

    public AgentRunAttachResult Consume(AgentRunAttached ev)
    {
        var runners = Ambient.Relation<IAgentRunnerTable>();
        if (runners.FindByIdOrDefault(ev.SessionId) is not { } runner || runner.ActionId != ev.ActionId ||
            Ambient.Relation<ISessionTable>().FindByIdOrDefault(ev.SessionId) is not { } session ||
            actions.FindAction(AgentRunners.ActionOf(session, runner)) is not { State: ActionState.Started } action || action.RunAttempts != ev.Attempt)
            return new();
        var clean = runner.EndedCleanly && runner.Attempt == 0;
        runner.EndedCleanly = false;
        runner.Attempt = ev.Attempt;
        runner.NodeId = ev.NodeId;
        runner.PeerUrl = ev.PeerUrl;
        runner.Version++;
        runners.Update(runner);
        AgentRunners.RunnerChanged(signals, session.Id);
        return new() { Attached = true, Clean = clean };
    }
}

public sealed class AgentRunsResumedHandler(IDomainSignals signals, BackendActions actions) : IEventHandler<AgentRunsResumed>
{
    public void Validate(AgentRunsResumed ev) { }

    public void Consume(AgentRunsResumed ev) => AgentRunners.Resume(signals, actions);
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
