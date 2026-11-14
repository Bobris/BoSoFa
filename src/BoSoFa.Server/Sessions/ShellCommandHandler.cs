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
        var cancellation = context.Cancellation;
        var publisher = await node.Ready.WaitAsync(cancellation);
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
        // The key is the same for every attempt of the action, so one that follows a lost node joins the command instead of running it again.
        var key = $"shell-{session.Id}-{input.Seq}";
        // Stop (the person, or the session stopping) ends the command on the machine, whose answer then has what it printed; an
        // interruption (this node going away) leaves it running for the next attempt.
        using var abandon = new CancellationTokenSource();
        await using var stopping = cancellation.Register(() =>
        {
            if (context.CancellationReason == ActionCancellationReason.Interrupted) abandon.Cancel();
            else _ = machine.StopAsync(key);
        });
        ToolRunResult result;
        try { result = await machine.RunCommandAsync(new(key, ProtocolShell.RefOf(input.Seq), 0), command, abandon.Token); }
        catch (OperationCanceledException) when (abandon.IsCancellationRequested) { return ActionResult.Interrupted; }
        var stopped = result.Stopped || (cancellation.IsCancellationRequested && result.ExitCode == null && !result.TimedOut);
        return new SessionShellCommandResult
        {
            Text = masker.Masker.MaskText(OutputOf(result, stopped)),
            ExitCode = result.ExitCode,
            State = stopped ? ProtocolShell.Stopped : result.TimedOut ? ProtocolShell.TimedOut : result.ExitCode != null ? ProtocolShell.Done : ProtocolShell.Failed
        };
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

    static SessionShellCommandResult Failed(string why) => new() { Text = why, State = ProtocolShell.Failed };

    public void OnFinished(SessionShellCommand input, ActionOutcome<SessionShellCommandResult> outcome) =>
        DomainShell.Finished(signals, input, outcome.Kind, outcome.Result, Ambient.CurrentEvent!.When);
}
