using System.Text.Json;
using BoSoFa.Protocol;
using BTDB.BackendActions;
using BTDB.ODBLayer;
using BTDB.Replication.EventSourcing;
using ProtocolShell = BoSoFa.Protocol.ShellCommands;

namespace BoSoFa.Server.Domain;

/// <summary>
/// One item of the shell commands run from the prompt of a draft (docs/shell-commands.md): a <see cref="SessionItemKind.ShellCommand"/> and,
/// once it ended, its <see cref="SessionItemKind.ShellOutput"/>, with the draft's own seqs and refs. When the draft becomes a session they move
/// into its transcript.
/// </summary>
public class DraftItem
{
    [PrimaryKey(1)] public ulong DraftId { get; set; }
    [PrimaryKey(2)] public long Seq { get; set; }
    public ulong OwnerId { get; set; }
    public SessionItemKind Kind { get; set; }
    public DateTime At { get; set; }
    public string Text { get; set; } = "";
    public string? Data { get; set; }
    public bool IsError { get; set; }
    public string? Ref { get; set; }

    /// <summary>For a command: the machine and the folder it runs in, and the profile whose variables it gets.</summary>
    public ulong MachineId { get; set; }

    public string Folder { get; set; } = "";
    public string? ProfileName { get; set; }

    /// <summary>While a command runs: its backend action, of the owner's tenant; zero once it ended.</summary>
    public ulong ActionId { get; set; }

    /// <summary>A command that still ran when its draft became a session: that session, and the command's item there, get its output.</summary>
    public ulong MovedToSessionId { get; set; }

    public long MovedToSeq { get; set; }

    public SessionItemDto ToDto() => new(Seq, Kind, At, Text, null, null, Data, IsError, Ref);
}

public interface IDraftItemTable : IRelation<DraftItem>
{
    void Insert(DraftItem item);
    void Update(DraftItem item);
    DraftItem? FindByIdOrDefault(ulong draftId, long seq);
    bool RemoveById(ulong draftId, long seq);
    IEnumerable<DraftItem> ListById(ulong draftId);
}

/// <summary>The person runs a command from the prompt of their draft.</summary>
public class DraftShellCommandRun : EventWithResult<SessionCommandResult>
{
    public ulong UserId { get; set; }
    public ulong DraftId { get; set; }
    public ulong MachineId { get; set; }
    public string WorkingDirectory { get; set; } = "";
    public string? ProfileName { get; set; }
    public string Command { get; set; } = "";
}

/// <summary>The person stops a command of their draft; <c>Seq</c> is its item.</summary>
public class DraftShellCommandStopRequested : EventWithResult<SessionCommandResult>
{
    public ulong UserId { get; set; }
    public ulong DraftId { get; set; }
    public long Seq { get; set; }
}

/// <summary>The backend action that runs one command of a draft, as <see cref="SessionShellCommand"/> does for a session.</summary>
[BackendAction(Name = "draft-shell-command", Cpu = 10)]
public class DraftShellCommand : ActionInput<SessionShellCommandResult>
{
    public ulong DraftId { get; set; }

    /// <summary>The command's item.</summary>
    public long Seq { get; set; }
}

public static class DraftShellCommands
{
    /// <summary>
    /// Whether a draft's command may have the values of the profile's environment on this machine. One whose environment has reviewers
    /// may not: only a session asks them, so a draft would get what nobody approved.
    /// </summary>
    public static string? Refusal(Profile profile, ulong machineId)
    {
        if (string.IsNullOrEmpty(profile.EnvironmentName)) return null;
        if (SecretEnvironments.Find(profile.EnvironmentName) is not { } environment)
            return $"The environment {profile.EnvironmentName} of the profile {profile.Name} does not exist any more.";
        if (!Secrets.AllowedOn(environment.Machines, machineId)) return $"The {environment.Name} environment is not for this machine.";
        return SecretEnvironments.Reviewers(environment).Length > 0
            ? $"The {environment.Name} environment needs a reviewer's approval, which only a session gets: start the session, then run the command there."
            : null;
    }

    static QualifiedActionId ActionOf(DraftItem command) => new(command.OwnerId, command.ActionId);

    public static IEnumerable<DraftItem> ItemsOf(ulong draftId) => Ambient.Relation<IDraftItemTable>().ListById(draftId);

    static void Changed(IDomainSignals signals, ulong draftId) => Ambient.RegisterPostCommit(() => signals.DraftChanged(draftId));

    /// <summary>Records how a command ended: in the draft, or in the session it moved to.</summary>
    public static void Finished(IDomainSignals signals, DraftShellCommand input, ActionOutcomeKind kind, SessionShellCommandResult? result, DateTime when)
    {
        if (kind == ActionOutcomeKind.Progress) return;
        var items = Ambient.Relation<IDraftItemTable>();
        if (items.FindByIdOrDefault(input.DraftId, input.Seq) is not { Kind: SessionItemKind.ShellCommand } command) return;
        if (command.MovedToSessionId != 0)
        {
            items.RemoveById(command.DraftId, command.Seq);
            ShellCommands.Finished(signals, new() { SessionId = command.MovedToSessionId, Seq = command.MovedToSeq }, kind, result, when, command.Folder);
            return;
        }
        command.ActionId = 0;
        items.Update(command);
        var (text, data) = ShellCommands.OutputOf(command.Text, command.Folder, command.At, kind, result, when);
        items.Insert(new()
        {
            DraftId = command.DraftId, Seq = NextSeq(command.DraftId), OwnerId = command.OwnerId, Kind = SessionItemKind.ShellOutput, At = when,
            Text = text, Data = JsonSerializer.Serialize(data, ProtocolJsonContext.Default.ShellOutputData), IsError = ShellCommands.IsError(data),
            Ref = ProtocolShell.RefOf(command.Seq)
        });
        Changed(signals, command.DraftId);
    }

    internal static long NextSeq(ulong draftId) => (ItemsOf(draftId).LastOrDefault()?.Seq ?? 0) + 1;

    /// <summary>The draft is gone: its commands stop and are forgotten (their actions then find nothing to record into).</summary>
    public static void RemoveAll(BackendActions actions, ulong draftId)
    {
        var items = Ambient.Relation<IDraftItemTable>();
        foreach (var item in ItemsOf(draftId).ToList())
        {
            if (item is { Kind: SessionItemKind.ShellCommand, ActionId: not 0, MovedToSessionId: 0 }) actions.Cancel(ActionOf(item));
            if (item.MovedToSessionId == 0) items.RemoveById(item.DraftId, item.Seq);
        }
    }

    static List<DraftItem> Movable(ulong draftId) => draftId == 0 ? [] : ItemsOf(draftId).Where(item => item.MovedToSessionId == 0).ToList();

    /// <summary>Whether a session of this person may start from the draft with these of its outputs (draft seqs) in the prompt.</summary>
    public static bool CanMove(ulong userId, ulong draftId, IReadOnlyCollection<long> outputSeqs)
    {
        var all = Movable(draftId);
        return outputSeqs.Count <= ProtocolShell.MaximumPerMessage && outputSeqs.Distinct().Count() == outputSeqs.Count &&
               all.All(item => item.OwnerId == userId) &&
               outputSeqs.All(seq => all.Any(item => item.Seq == seq && item.Kind == SessionItemKind.ShellOutput));
    }

    /// <summary>
    /// Moves the draft's commands into the session's transcript, in the order they ran; a command that still runs goes on there, and its
    /// output is recorded there. Returns the outputs named by <paramref name="outputSeqs"/> (draft seqs) for the prompt, with the
    /// session's seqs of their items. <see cref="CanMove"/> checked them.
    /// </summary>
    internal static (List<(ShellOutputData Data, string Output)> Outputs, List<long> Seqs) MoveTo(Session session, ulong draftId,
        IReadOnlyCollection<long> outputSeqs)
    {
        var all = Movable(draftId);
        var items = Ambient.Relation<IDraftItemTable>();
        var runs = Ambient.Relation<IShellCommandRunTable>();
        var commands = new Dictionary<long, long>();
        var outputs = new List<(ShellOutputData, string)>();
        var seqs = new List<long>();
        foreach (var item in all)
        {
            if (item.Kind == SessionItemKind.ShellCommand)
            {
                var seq = Sessions.AppendItem(session, SessionItemKind.ShellCommand, item.At, item.Text);
                commands[item.Seq] = seq;
                if (item.ActionId != 0)
                {
                    // It goes on, now as the session's: its Stop, its limit and the session's own stop count it.
                    runs.Insert(new() { SessionId = session.Id, Seq = seq, ActionId = item.ActionId });
                    (item.MovedToSessionId, item.MovedToSeq) = (session.Id, seq);
                    items.Update(item);
                    continue;
                }
            }
            else if (ProtocolShell.CommandSeqOf(item.Ref) is { } commandSeq && commands.TryGetValue(commandSeq, out var command))
            {
                var seq = Sessions.AppendItem(session, SessionItemKind.ShellOutput, item.At, item.Text, data: item.Data, isError: item.IsError,
                    itemRef: ProtocolShell.RefOf(command));
                if (outputSeqs.Contains(item.Seq) && item.Data != null)
                {
                    outputs.Add((JsonSerializer.Deserialize(item.Data, ProtocolJsonContext.Default.ShellOutputData)!, item.Text));
                    seqs.Add(seq);
                }
            }
            items.RemoveById(item.DraftId, item.Seq);
        }
        return (outputs, seqs);
    }
}

public sealed class DraftShellCommandRunHandler(IDomainSignals signals, BackendActions actions) : IEventHandler<DraftShellCommandRun, SessionCommandResult>
{
    public SessionCommandResult? Validate(DraftShellCommandRun ev) =>
        string.IsNullOrWhiteSpace(ev.Command) || ev.Command.Trim().Length > ProtocolShell.MaximumCommandLength || string.IsNullOrWhiteSpace(ev.WorkingDirectory)
            ? new() { Outcome = CommandOutcome.Invalid }
            : null;

    public SessionCommandResult Consume(DraftShellCommandRun ev)
    {
        if (Ambient.Relation<ISessionDraftTable>().FindByIdOrDefault(ev.UserId, ev.DraftId) is null) return new() { Outcome = CommandOutcome.NotFound };
        if (Ambient.Relation<IMachineTable>().FindByIdOrDefault(ev.MachineId) is not { } machine) return new() { Outcome = CommandOutcome.NotFound };
        if (!MachineAccess.CanControl(ev.UserId, machine)) return new() { Outcome = CommandOutcome.Forbidden };
        if (ev.ProfileName != null)
        {
            if (Ambient.Relation<IProfileTable>().FindByIdOrDefault(ev.ProfileName) is not { } profile) return new() { Outcome = CommandOutcome.NotFound };
            if (DraftShellCommands.Refusal(profile, machine.Id) != null) return new() { Outcome = CommandOutcome.Invalid };
        }
        var items = Ambient.Relation<IDraftItemTable>();
        var all = DraftShellCommands.ItemsOf(ev.DraftId).Where(item => item.MovedToSessionId == 0).ToList();
        var commands = all.Where(item => item.Kind == SessionItemKind.ShellCommand).ToList();
        if (commands.Count(command => command.ActionId != 0) >= ProtocolShell.MaximumRunning) return new() { Outcome = CommandOutcome.Invalid };
        // The oldest that ended make room, with their outputs.
        foreach (var old in commands.Where(command => command.ActionId == 0).Take(Math.Max(0, commands.Count + 1 - ProtocolShell.MaximumPerDraft)).ToList())
        {
            items.RemoveById(old.DraftId, old.Seq);
            foreach (var output in all.Where(item => item.Ref == ProtocolShell.RefOf(old.Seq))) items.RemoveById(output.DraftId, output.Seq);
        }
        var command = ev.Command.Trim();
        var seq = DraftShellCommands.NextSeq(ev.DraftId);
        var action = actions.Enqueue(new DraftShellCommand { TenantId = ev.UserId, DraftId = ev.DraftId, Seq = seq });
        items.Insert(new()
        {
            DraftId = ev.DraftId, Seq = seq, OwnerId = ev.UserId, Kind = SessionItemKind.ShellCommand, At = ev.When, Text = command,
            MachineId = machine.Id, Folder = ev.WorkingDirectory.Trim(), ProfileName = ev.ProfileName, ActionId = action.ActionId
        });
        Ambient.RegisterPostCommit(() => signals.DraftChanged(ev.DraftId));
        Audit.Record(ev.When, ev.UserId, "draft.shell_command", $"draft {ev.DraftId}",
            $"on machine {machine.Id} in {ev.WorkingDirectory.Trim()}: {ShellCommands.Audited(command)}");
        return new() { Outcome = CommandOutcome.Ok, SessionId = ev.DraftId };
    }
}

public sealed class DraftShellCommandStopRequestedHandler(BackendActions actions) : IEventHandler<DraftShellCommandStopRequested, SessionCommandResult>
{
    public SessionCommandResult? Validate(DraftShellCommandStopRequested ev) => null;

    public SessionCommandResult Consume(DraftShellCommandStopRequested ev)
    {
        if (Ambient.Relation<ISessionDraftTable>().FindByIdOrDefault(ev.UserId, ev.DraftId) is null) return new() { Outcome = CommandOutcome.NotFound };
        // One that already ended has nothing to stop.
        if (Ambient.Relation<IDraftItemTable>().FindByIdOrDefault(ev.DraftId, ev.Seq) is
            { Kind: SessionItemKind.ShellCommand, ActionId: not 0, MovedToSessionId: 0 } command)
            actions.Cancel(new QualifiedActionId(command.OwnerId, command.ActionId));
        return new() { Outcome = CommandOutcome.Ok, SessionId = ev.DraftId };
    }
}
