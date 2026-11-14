using System.Text.Json;
using BoSoFa.Protocol;
using BTDB;
using BTDB.BackendActions;
using BTDB.ODBLayer;
using BTDB.Replication.EventSourcing;
using ProtocolShell = BoSoFa.Protocol.ShellCommands;

namespace BoSoFa.Server.Domain;

/// <summary>The person runs a command in the session's folder on its machine (<c>!</c> in the prompt, docs/shell-commands.md).</summary>
public class SessionShellCommandRun : EventWithResult<SessionCommandResult>
{
    public ulong SessionId { get; set; }
    public ulong UserId { get; set; }
    public string Command { get; set; } = "";
}

/// <summary>The person stops a command they ran; <c>Seq</c> is its <see cref="SessionItemKind.ShellCommand"/> item.</summary>
public class SessionShellCommandStopRequested : EventWithResult<SessionCommandResult>
{
    public ulong SessionId { get; set; }
    public ulong UserId { get; set; }
    public long Seq { get; set; }
}

/// <summary>The backend action that runs one command on the session's machine and waits for it. It mostly waits, so it counts as
/// little CPU as an action can.</summary>
[BackendAction(Name = "session-shell-command", Cpu = 10)]
public class SessionShellCommand : ActionInput<SessionShellCommandResult>
{
    public ulong SessionId { get; set; }

    /// <summary>The command's item.</summary>
    public long Seq { get; set; }
}

[Generate]
public class SessionShellCommandResult
{
    /// <summary>The output, masked; for a command that could not run, why.</summary>
    public string Text { get; set; } = "";

    public string State { get; set; } = ProtocolShell.Done;
    public int? ExitCode { get; set; }
}

/// <summary>A command that runs: a row exists from its start until its output is recorded.</summary>
public class ShellCommandRun
{
    [PrimaryKey(1)] public ulong SessionId { get; set; }
    [PrimaryKey(2)] public long Seq { get; set; }

    /// <summary>The backend action (of the session creator's tenant) that runs it.</summary>
    public ulong ActionId { get; set; }
}

public interface IShellCommandRunTable : IRelation<ShellCommandRun>
{
    void Insert(ShellCommandRun run);
    ShellCommandRun? FindByIdOrDefault(ulong sessionId, long seq);
    bool RemoveById(ulong sessionId, long seq);
    IEnumerable<ShellCommandRun> ListById(ulong sessionId);
}

public static class ShellCommands
{
    static QualifiedActionId ActionOf(Session session, ShellCommandRun run) => new(session.CreatedById, run.ActionId);

    /// <summary>Records how a command ended and lets go of its row. Runs once per final outcome; the action reports a command that
    /// could not run as an outcome of its own, so a failure here was unexpected.</summary>
    /// <param name="folder">Where it ran, when not the session's folder: a command that moved in from the session's draft.</param>
    public static void Finished(IDomainSignals signals, SessionShellCommand input, ActionOutcomeKind kind, SessionShellCommandResult? result, DateTime when,
        string? folder = null)
    {
        if (kind == ActionOutcomeKind.Progress) return;
        var runs = Ambient.Relation<IShellCommandRunTable>();
        runs.RemoveById(input.SessionId, input.Seq);
        if (Ambient.Relation<ISessionTable>().FindByIdOrDefault(input.SessionId) is not { } session ||
            Ambient.Relation<ISessionItemTable>().FindByIdOrDefault(session.Id, input.Seq) is not { Kind: SessionItemKind.ShellCommand } command)
            return;
        var (text, data) = OutputOf(command.Text, folder ?? session.WorkingDirectory, command.At, kind, result, when);
        Sessions.AppendItem(session, SessionItemKind.ShellOutput, when, text, data: JsonSerializer.Serialize(data, ProtocolJsonContext.Default.ShellOutputData),
            isError: IsError(data), itemRef: ProtocolShell.RefOf(command.Seq));
        Sessions.Changed(signals, session, when);
    }

    /// <summary>What a command's action ended with, as its output item tells it.</summary>
    internal static (string Text, ShellOutputData Data) OutputOf(string command, string folder, DateTime started, ActionOutcomeKind kind,
        SessionShellCommandResult? result, DateTime when)
    {
        var (text, state, exitCode) = kind switch
        {
            ActionOutcomeKind.Completed when result != null => (result.Text, result.State, result.ExitCode),
            ActionOutcomeKind.Cancelled => ("", ProtocolShell.Stopped, (int?)null),
            _ => ("The command could not be run.", ProtocolShell.Failed, null)
        };
        return (text, new ShellOutputData(command, folder, state, exitCode, (long)(when - started).TotalMilliseconds));
    }

    internal static bool IsError(ShellOutputData data) => data.State != ProtocolShell.Done || data.ExitCode != 0;

    /// <summary>Stops every command of the session that runs; each still records how it ended.</summary>
    public static void StopAll(BackendActions actions, Session session)
    {
        foreach (var run in Ambient.Relation<IShellCommandRunTable>().ListById(session.Id).ToList())
            actions.Cancel(ActionOf(session, run));
    }

    internal static void Stop(BackendActions actions, Session session, ShellCommandRun run) => actions.Cancel(ActionOf(session, run));

    internal static string Audited(string command) => command.Length <= 200 ? command : command[..199] + "…";

    /// <summary>The outputs a message carries, each a <see cref="SessionItemKind.ShellOutput"/> of the session; null when one is not.</summary>
    internal static List<(ShellOutputData Data, string Output)>? OutputsOf(Session session, IReadOnlyCollection<long> seqs)
    {
        if (seqs.Count > ProtocolShell.MaximumPerMessage || seqs.Distinct().Count() != seqs.Count) return null;
        var items = Ambient.Relation<ISessionItemTable>();
        var outputs = new List<(ShellOutputData, string)>();
        foreach (var seq in seqs.Order())
        {
            if (items.FindByIdOrDefault(session.Id, seq) is not { Kind: SessionItemKind.ShellOutput, Data: { } json } item) return null;
            outputs.Add((JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.ShellOutputData)!, item.Text));
        }
        return outputs;
    }
}

public sealed class SessionShellCommandRunHandler(IDomainSignals signals, BackendActions actions) : IEventHandler<SessionShellCommandRun, SessionCommandResult>
{
    public SessionCommandResult? Validate(SessionShellCommandRun ev) =>
        string.IsNullOrWhiteSpace(ev.Command) || ev.Command.Trim().Length > ProtocolShell.MaximumCommandLength ? new() { Outcome = CommandOutcome.Invalid } : null;

    public SessionCommandResult Consume(SessionShellCommandRun ev)
    {
        var (session, _, outcome) = Sessions.Controlled(ev.SessionId, ev.UserId);
        if (session == null) return new() { Outcome = outcome };
        var runs = Ambient.Relation<IShellCommandRunTable>();
        if (session.MachineId == 0 || session.Status == SessionStatus.Failed || runs.ListById(session.Id).Count() >= ProtocolShell.MaximumRunning)
            return new() { Outcome = CommandOutcome.Invalid };
        var command = ev.Command.Trim();
        var seq = Sessions.AppendItem(session, SessionItemKind.ShellCommand, ev.When, command);
        var action = actions.Enqueue(new SessionShellCommand { TenantId = session.CreatedById, SessionId = session.Id, Seq = seq });
        runs.Insert(new() { SessionId = session.Id, Seq = seq, ActionId = action.ActionId });
        Sessions.Changed(signals, session, ev.When);
        Audit.Record(ev.When, ev.UserId, "session.shell_command", $"session {session.Id}", ShellCommands.Audited(command));
        return new() { Outcome = CommandOutcome.Ok, SessionId = session.Id };
    }
}

public sealed class SessionShellCommandStopRequestedHandler(BackendActions actions) : IEventHandler<SessionShellCommandStopRequested, SessionCommandResult>
{
    public SessionCommandResult? Validate(SessionShellCommandStopRequested ev) => null;

    public SessionCommandResult Consume(SessionShellCommandStopRequested ev)
    {
        var (session, _, outcome) = Sessions.Controlled(ev.SessionId, ev.UserId, evenWaiting: true);
        if (session == null) return new() { Outcome = outcome };
        // One that already ended has nothing to stop.
        if (Ambient.Relation<IShellCommandRunTable>().FindByIdOrDefault(session.Id, ev.Seq) is { } run) ShellCommands.Stop(actions, session, run);
        return new() { Outcome = CommandOutcome.Ok, SessionId = session.Id };
    }
}
