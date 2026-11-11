using BoSoFa.Server.Domain;
using BTDB.BackendActions;
using BTDB.Replication.EventSourcing;

namespace BoSoFa.Server.Agents;

/// <summary>Answers a server agent session's side question (/btw) as a backend action, beside the session's run.</summary>
public sealed class AgentSideQuestionHandler(AgentRunnerService runner, IDomainSignals signals) : IActionHandler<AgentSideQuestion, AgentSideAnswer>
{
    public async Task<ActionResult<AgentSideAnswer>> ExecuteAsync(AgentSideQuestion input, IActionContext context) =>
        await runner.AnswerSideQuestionAsync(input, context);

    public void OnFinished(AgentSideQuestion input, ActionOutcome<AgentSideAnswer> outcome) =>
        SideQuestions.Finished(signals, input, outcome.Kind, outcome.Result, Ambient.CurrentEvent!.When);
}
