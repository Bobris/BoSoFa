using System.Globalization;
using BoSoFa.Protocol;
using BTDB;
using BTDB.BackendActions;
using BTDB.Replication.EventSourcing;

namespace BoSoFa.Server.Domain;

/// <summary>The backend action that answers a server agent session's side question (/btw) once, beside any run: one model call
/// with the conversation so far and no tools. It mostly waits for the model, so it counts as little CPU as an action can.</summary>
[BackendAction(Name = "agent-side-question", Cpu = 10)]
public class AgentSideQuestion : ActionInput<AgentSideAnswer>
{
    public ulong SessionId { get; set; }

    /// <summary>The question's item; the conversation is what came before it.</summary>
    public long QuestionSeq { get; set; }
}

[Generate]
public class AgentSideAnswer
{
    public string Text { get; set; } = "";
    public bool IsError { get; set; }
    public double CostUsd { get; set; }
}

public static class SideQuestions
{
    /// <summary>What ties an answer to its question: the question's sequence number.</summary>
    public static string RefOf(long questionSeq) => questionSeq.ToString(CultureInfo.InvariantCulture);

    public static void Start(BackendActions actions, Session session, long questionSeq) =>
        actions.Enqueue(new AgentSideQuestion { TenantId = session.CreatedById, SessionId = session.Id, QuestionSeq = questionSeq });

    /// <summary>Records the answer, or that there is none. Runs once per final outcome; the action reports the problems it expects
    /// as an answer marked as an error, so a failure here was unexpected.</summary>
    public static void Finished(IDomainSignals signals, AgentSideQuestion input, ActionOutcomeKind kind, AgentSideAnswer? answer, DateTime when)
    {
        if (kind == ActionOutcomeKind.Progress || Ambient.Relation<ISessionTable>().FindByIdOrDefault(input.SessionId) is not { } session) return;
        if (kind == ActionOutcomeKind.Completed && answer != null)
        {
            session.CostUsd += answer.CostUsd;
            Sessions.AppendItem(session, SessionItemKind.SideAnswer, when, answer.Text, isError: answer.IsError, itemRef: RefOf(input.QuestionSeq));
        }
        else
            Sessions.AppendItem(session, SessionItemKind.SideAnswer, when,
                kind == ActionOutcomeKind.Cancelled ? "The question was cancelled." : "The question could not be answered.",
                isError: true, itemRef: RefOf(input.QuestionSeq));
        Sessions.Changed(signals, session, when);
    }
}
