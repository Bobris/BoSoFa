using System.Globalization;
using System.Text.RegularExpressions;
using BoSoFa.Protocol;
using BoSoFa.Server.Accounts;
using BoSoFa.Server.Agents;
using BoSoFa.Server.Btdb;
using BoSoFa.Server.Domain;
using BoSoFa.Server.Machines;
using BoSoFa.Server.Secrets;
using BTDB.BackendActions;
using BTDB.Replication.EventSourcing;
using Microsoft.AspNetCore.DataProtection;
using ProtocolShell = BoSoFa.Protocol.ShellCommands;
using DomainShell = BoSoFa.Server.Domain.ShellCommands;

namespace BoSoFa.Server.Sessions;

/// <summary>
/// Runs a command the person typed after <c>!</c> (docs/shell-commands.md) on the session's machine, in its folder, with the
/// variables of its profile resolved for its creator as the agent's own shell gets them. Its output streams under the command's ref
/// and is masked on the daemon and again here. Stopping the action stops the command.
/// </summary>
public sealed class ShellCommandHandler(BtdbNode node, MachineRpcClient rpc, IDataProtectionProvider protection, ServerSecretMasker masker,
    IDomainSignals signals, TimeProvider time, ILogger<ShellCommandHandler> logger) : IActionHandler<SessionShellCommand, SessionShellCommandResult>
{
    public async Task<ActionResult<SessionShellCommandResult>> ExecuteAsync(SessionShellCommand input, IActionContext context)
    {
        var publisher = await node.Ready.WaitAsync(context.Cancellation);
        Session session;
        string command;
        Dictionary<string, string> environment = [];
        SecretResolver? resolver = null;
        using (publisher.StartQuery())
        {
            if (Ambient.Relation<ISessionTable>().FindByIdOrDefault(input.SessionId) is not { MachineId: not 0 } found ||
                Ambient.Relation<ISessionItemTable>().FindByIdOrDefault(found.Id, input.Seq) is not { Kind: SessionItemKind.ShellCommand } item)
                return Failed("The session is gone.");
            (session, command) = (found, item.Text);
            if (session.ProfileName != null && Ambient.Relation<IProfileTable>().FindByIdOrDefault(session.ProfileName) is { } profile)
            {
                var (secretEnvironment, problem) = SecretEnvironments.Admit(session, profile);
                if (problem != null) return Failed(problem);
                resolver = new SecretResolver(session.CreatedById, protection, session.MachineId, secretEnvironment);
                environment = CatalogEndpoints.EnvironmentOf(profile, resolver);
                try { resolver.ThrowIfMissing(); }
                catch (MissingSecretsException missing) { return Failed(missing.Message); }
            }
        }
        if (resolver != null) await resolver.RecordAsync(publisher, session, logger);
        var machine = new AgentMachine(rpc, session.MachineId, session.WorkingDirectory, session.Id, time, environment, resolver?.Secrets);
        return await RunAsync(machine, $"shell-{session.Id}-{input.Seq}", ProtocolShell.RefOf(input.Seq), command, context, masker);
    }

    /// <param name="key">The same for every attempt of the action, so one that follows a lost node joins the command instead of running it again.</param>
    internal static async Task<ActionResult<SessionShellCommandResult>> RunAsync(AgentMachine machine, string key, string liveRef, string command,
        IActionContext context, ServerSecretMasker masker)
    {
        var cancellation = context.Cancellation;
        // Stop (the person, or the session stopping) ends the command on the machine, whose answer then has what it printed; an
        // interruption (this node going away) leaves it running for the next attempt.
        using var abandon = new CancellationTokenSource();
        await using var stopping = cancellation.Register(() =>
        {
            if (context.CancellationReason == ActionCancellationReason.Interrupted) abandon.Cancel();
            else _ = machine.StopAsync(key);
        });
        ToolRunResult result;
        try { result = await machine.RunCommandAsync(new(key, liveRef, 0), command, abandon.Token); }
        catch (OperationCanceledException) when (abandon.IsCancellationRequested) { return ActionResult.Interrupted; }
        result = FromOlderDaemon(result);
        var stopped = result.Stopped || (cancellation.IsCancellationRequested && result.ExitCode == null && !result.TimedOut);
        return new SessionShellCommandResult
        {
            Text = masker.Masker.MaskText(OutputOf(result, stopped)),
            ExitCode = result.ExitCode,
            State = stopped ? ProtocolShell.Stopped : result.TimedOut ? ProtocolShell.TimedOut : result.ExitCode != null ? ProtocolShell.Done : ProtocolShell.Failed
        };
    }

    /// <summary>A daemon from before <see cref="ToolRunResult.ExitCode"/> says how the command ended only in its text, for the model.</summary>
    internal static ToolRunResult FromOlderDaemon(ToolRunResult result)
    {
        if (result.ExitCode != null || result.TimedOut || result.Stopped) return result;
        if (Regex.IsMatch(result.Text, @"\[Stopped after \d+ seconds\.\]$")) return result with { TimedOut = true };
        if (Regex.Match(result.Text, @"\[Exit code (-?\d+)\]$") is { Success: true } exit &&
            int.TryParse(exit.Groups[1].ValueSpan, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code))
            return result with { ExitCode = code };
        // Without an error it ended by itself with 0; with one and neither of those, the server says why it did not run.
        return result.IsError ? result : result with { ExitCode = 0 };
    }

    /// <summary>What the command printed: the shell tool adds for the model how it ended, which the item says on its own.</summary>
    static string OutputOf(ToolRunResult result, bool stopped)
    {
        var text = result.Text;
        if (result.ExitCode == 0 && text == "The command printed nothing.") return "";
        // A daemon from before Stopped says only that.
        if (stopped) return result.Stopped && text.EndsWith("[Stopped.]", StringComparison.Ordinal) ? text[..^"[Stopped.]".Length] : "";
        var ending = result.TimedOut ? text.LastIndexOf("[Stopped after ", StringComparison.Ordinal)
            : result.ExitCode is { } code && code != 0 && text.EndsWith($"[Exit code {code}]", StringComparison.Ordinal) ? text.LastIndexOf("[Exit code ", StringComparison.Ordinal)
            : -1;
        return ending < 0 ? text : text[..ending];
    }

    internal static SessionShellCommandResult Failed(string why) => new() { Text = why, State = ProtocolShell.Failed };

    public void OnFinished(SessionShellCommand input, ActionOutcome<SessionShellCommandResult> outcome) =>
        DomainShell.Finished(signals, input, outcome.Kind, outcome.Result, Ambient.CurrentEvent!.When);
}

/// <summary>
/// Runs a command typed after <c>!</c> in the prompt of a draft, a session not started yet: on the machine and in the folder the draft chose, with
/// the variables of its profile resolved for its owner. Its output streams under the draft's id. A command that still runs when the draft
/// becomes a session goes on there.
/// </summary>
public sealed class DraftShellCommandHandler(BtdbNode node, MachineRpcClient rpc, IDataProtectionProvider protection, ServerSecretMasker masker,
    IDomainSignals signals, TimeProvider time, ILogger<DraftShellCommandHandler> logger) : IActionHandler<DraftShellCommand, SessionShellCommandResult>
{
    public async Task<ActionResult<SessionShellCommandResult>> ExecuteAsync(DraftShellCommand input, IActionContext context)
    {
        var publisher = await node.Ready.WaitAsync(context.Cancellation);
        DraftItem command;
        Dictionary<string, string> environment = [];
        SecretResolver? resolver = null;
        using (publisher.StartQuery())
        {
            if (Ambient.Relation<IDraftItemTable>().FindByIdOrDefault(input.DraftId, input.Seq) is not { Kind: SessionItemKind.ShellCommand } found)
                return ShellCommandHandler.Failed("The draft is gone.");
            command = found;
            if (command.ProfileName != null)
            {
                if (Ambient.Relation<IProfileTable>().FindByIdOrDefault(command.ProfileName) is not { } profile)
                    return ShellCommandHandler.Failed($"The profile {command.ProfileName} does not exist any more.");
                if (DraftShellCommands.Refusal(profile, command.MachineId) is { } refusal) return ShellCommandHandler.Failed(refusal);
                resolver = new SecretResolver(command.OwnerId, protection, command.MachineId, SecretEnvironments.Find(profile.EnvironmentName));
                environment = CatalogEndpoints.EnvironmentOf(profile, resolver);
                try { resolver.ThrowIfMissing(); }
                catch (MissingSecretsException missing) { return ShellCommandHandler.Failed(missing.Message); }
            }
        }
        if (resolver != null) await resolver.RecordForDraftAsync(publisher, command.DraftId, command.MachineId, command.OwnerId, logger);
        var machine = new AgentMachine(rpc, command.MachineId, command.Folder, command.DraftId, time, environment, resolver?.Secrets);
        return await ShellCommandHandler.RunAsync(machine, $"shell-draft-{command.DraftId}-{command.Seq}", ProtocolShell.RefOf(command.Seq), command.Text,
            context, masker);
    }

    public void OnFinished(DraftShellCommand input, ActionOutcome<SessionShellCommandResult> outcome) =>
        DraftShellCommands.Finished(signals, input, outcome.Kind, outcome.Result, Ambient.CurrentEvent!.When);
}
