using BoSoFa.Server.Domain;
using BTDB.BackendActions;
using BTDB.Replication.EventSourcing;

namespace BoSoFa.Server.Agents;

/// <summary>Runs an agent session's loop as a backend action (docs/server-agent.md §4.2). The actions object is resolved late:
/// it is built from this handler.</summary>
public sealed class AgentRunHandler(AgentRunnerService runner, IDomainSignals signals, IServiceProvider services) : IActionHandler<AgentRun, AgentRunResult>
{
    public async Task<ActionResult<AgentRunResult>> ExecuteAsync(AgentRun input, IActionContext context) => await runner.RunAsync(input, context);

    public void OnFinished(AgentRun input, ActionOutcome<AgentRunResult> outcome) =>
        AgentRunners.Finished(signals, services.GetRequiredService<BackendActions>(), input, outcome.Kind, Ambient.CurrentEvent!.When);
}
