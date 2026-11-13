using System.Text.Json;
using BoSoFa.Protocol;
using BTDB.ODBLayer;
using BTDB.Replication.EventSourcing;

namespace BoSoFa.Server.Domain;

/// <summary>
/// A named set of secrets and variables with rules of its own (docs/secrets.md §10), such as <c>production</c>. Its values are
/// <see cref="SecretEntry"/> rows owned by <see cref="Id"/>. A profile names it; a session of that profile runs only on
/// <see cref="Machines"/> (empty: any) and, when it has <see cref="Reviewers"/>, starts only once one of them other than its creator
/// approved it.
/// </summary>
public class SecretEnvironment
{
    [PrimaryKey(1)] public ulong Id { get; set; }
    [SecondaryKey("Name")] public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>People ids as a JSON array.</summary>
    public string Reviewers { get; set; } = "[]";

    /// <summary>Machine ids as a JSON array; empty for any.</summary>
    public string Machines { get; set; } = "[]";

    public ulong ChangedById { get; set; }
    public DateTime ChangedAt { get; set; }
}

public interface ISecretEnvironmentTable : IRelation<SecretEnvironment>
{
    void Insert(SecretEnvironment environment);
    void Update(SecretEnvironment environment);
    SecretEnvironment? FindByIdOrDefault(ulong id);
    SecretEnvironment? FindByNameOrDefault(string name);
    bool RemoveById(ulong id);
}

/// <summary>A session's admission to its environment: pending until a reviewer answers, then approved for the session's life,
/// restarts included, or rejected. <see cref="StartCommand"/> is the machine's start command, held until then (empty for a
/// server agent session, whose loop is claimed instead).</summary>
public class EnvironmentApproval
{
    [PrimaryKey(1)] public ulong SessionId { get; set; }
    public ulong EnvironmentId { get; set; }
    public EnvironmentApprovalStatus Status { get; set; }
    public ulong RequestedById { get; set; }
    public DateTime RequestedAt { get; set; }
    public ulong AnsweredById { get; set; }
    public DateTime? AnsweredAt { get; set; }
    public string StartCommand { get; set; } = "";
}

public interface IEnvironmentApprovalTable : IRelation<EnvironmentApproval>
{
    void Insert(EnvironmentApproval approval);
    void Update(EnvironmentApproval approval);
    EnvironmentApproval? FindByIdOrDefault(ulong sessionId);
    bool RemoveById(ulong sessionId);
}

/// <summary>An admin creates an environment or changes its rules; its name stays.</summary>
public class EnvironmentSaved : EventWithResult<SessionCommandResult>
{
    public ulong UserId { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Reviewers { get; set; } = "[]";
    public string Machines { get; set; } = "[]";
}

/// <summary>An admin deletes an environment no profile names and no session waits for, with its values.</summary>
public class EnvironmentDeleted : EventWithResult<SessionCommandResult>
{
    public ulong UserId { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>A reviewer approves or rejects a session that waits for its environment.</summary>
public class EnvironmentApprovalAnswered : EventWithResult<SessionCommandResult>
{
    public ulong UserId { get; set; }
    public ulong SessionId { get; set; }
    public bool Approve { get; set; }
}

public static class SecretEnvironments
{
    public const int MaxDescriptionLength = 500;

    public static SecretEnvironment? Find(ulong id) => id == 0 ? null : Ambient.Relation<ISecretEnvironmentTable>().FindByIdOrDefault(id);

    public static SecretEnvironment? Find(string? name) =>
        string.IsNullOrEmpty(name) ? null : Ambient.Relation<ISecretEnvironmentTable>().FindByNameOrDefault(name);

    public static ulong[] Reviewers(SecretEnvironment environment) => Secrets.Ids(environment.Reviewers);

    /// <summary>Whether <paramref name="userId"/> may answer for a session of <paramref name="requestedById"/>: a reviewer, never the
    /// session's own creator.</summary>
    public static bool MayAnswer(SecretEnvironment environment, ulong userId, ulong requestedById) =>
        userId != 0 && userId != requestedById && Reviewers(environment).Contains(userId);

    /// <summary>Whether a session waits for a reviewer: nothing reaches its machine or its loop until one answers.</summary>
    public static bool IsWaiting(ulong sessionId) =>
        Ambient.Relation<IEnvironmentApprovalTable>().FindByIdOrDefault(sessionId) is { Status: EnvironmentApprovalStatus.Pending };

    /// <summary>The profile names an environment whose machines do not include this one.</summary>
    public static bool Refuses(Profile? profile, ulong machineId) =>
        Find(profile?.EnvironmentName) is { } environment && !Secrets.AllowedOn(environment.Machines, machineId);

    /// <summary>Holds a new session whose environment needs a reviewer: records the approval it waits for, with what would start it,
    /// and says so on the session. False when it may start now.</summary>
    public static bool Hold(Session session, Profile? profile, string startCommand, DateTime when)
    {
        if (Find(profile?.EnvironmentName) is not { } environment || Reviewers(environment) is not { Length: > 0 } reviewers) return false;
        Ambient.Relation<IEnvironmentApprovalTable>().Insert(new()
        {
            SessionId = session.Id, EnvironmentId = environment.Id, Status = EnvironmentApprovalStatus.Pending, RequestedById = session.CreatedById,
            RequestedAt = when, StartCommand = startCommand
        });
        session.Status = SessionStatus.Starting;
        var users = Ambient.Relation<IUserTable>();
        var names = reviewers.Where(id => id != session.CreatedById).Select(id => users.FindByIdOrDefault(id)?.Name).OfType<string>().ToArray();
        Sessions.AppendItem(session, SessionItemKind.Notice, when, names.Length == 0
            ? $"This session uses the {environment.Name} environment, whose only reviewer is you: someone else must review it, so ask an admin to add one."
            : $"This session uses the {environment.Name} environment, so it starts once {Or(names)} approves it.");
        Audit.Record(when, session.CreatedById, "environment.requested", environment.Name, $"session {session.Id}");
        return true;
    }

    static string Or(string[] names) => names.Length == 1 ? names[0] : string.Join(", ", names[..^1]) + " or " + names[^1];

    /// <summary>A session stopped while it waited: its approval is no longer asked for. True when it was waiting.</summary>
    public static bool Withdraw(Session session)
    {
        var approvals = Ambient.Relation<IEnvironmentApprovalTable>();
        if (approvals.FindByIdOrDefault(session.Id) is not { Status: EnvironmentApprovalStatus.Pending }) return false;
        approvals.RemoveById(session.Id);
        return true;
    }

    /// <summary>
    /// Whether a session may have its environment's values now (resolution checks again what creation checked): the environment
    /// exists, allows the session's machine, and approved the session when it has reviewers. Rules that changed later count: a
    /// session that started without reviewers waits for nobody, but one whose environment gained reviewers since cannot resolve.
    /// </summary>
    public static (SecretEnvironment? Environment, string? Problem) Admit(Session session, Profile profile)
    {
        if (string.IsNullOrEmpty(profile.EnvironmentName)) return (null, null);
        if (Find(profile.EnvironmentName) is not { } environment)
            return (null, $"The environment {profile.EnvironmentName} of the profile {profile.Name} does not exist any more.");
        if (!Secrets.AllowedOn(environment.Machines, session.MachineId))
            return (null, $"The {environment.Name} environment is not for this machine.");
        if (Reviewers(environment).Length > 0)
        {
            var approval = Ambient.Relation<IEnvironmentApprovalTable>().FindByIdOrDefault(session.Id);
            if (approval is not { Status: EnvironmentApprovalStatus.Approved } || approval.EnvironmentId != environment.Id)
                return (null, approval?.Status == EnvironmentApprovalStatus.Pending
                    ? $"This session waits for a reviewer of the {environment.Name} environment."
                    : $"The {environment.Name} environment needs a reviewer's approval, which this session does not have.");
        }
        return (environment, null);
    }
}

public sealed class EnvironmentSavedHandler : IEventHandler<EnvironmentSaved, SessionCommandResult>
{
    public SessionCommandResult? Validate(EnvironmentSaved ev) =>
        CatalogNames.IsValid(ev.Name) && ev.Description.Length <= SecretEnvironments.MaxDescriptionLength ? null : new() { Outcome = CommandOutcome.Invalid };

    public SessionCommandResult Consume(EnvironmentSaved ev)
    {
        if (!Catalog.IsAdmin(ev.UserId)) return new() { Outcome = CommandOutcome.Forbidden };
        var users = Ambient.Relation<IUserTable>();
        if (Secrets.Ids(ev.Reviewers).Any(id => users.FindByIdOrDefault(id) == null)) return new() { Outcome = CommandOutcome.NotFound };
        var table = Ambient.Relation<ISecretEnvironmentTable>();
        var environment = table.FindByNameOrDefault(ev.Name);
        var created = environment == null;
        environment ??= new() { Id = Ambient.AllocateId(), Name = ev.Name };
        environment.Description = ev.Description.Trim();
        environment.Reviewers = Secrets.Json(Secrets.Ids(ev.Reviewers));
        environment.Machines = Secrets.Json(Secrets.Ids(ev.Machines));
        environment.ChangedById = ev.UserId;
        environment.ChangedAt = ev.When;
        if (created) table.Insert(environment);
        else table.Update(environment);
        Audit.Record(ev.When, ev.UserId, created ? "environment.created" : "environment.changed", ev.Name,
            $"reviewers {(environment.Reviewers == "[]" ? "none" : environment.Reviewers)}, machines {(environment.Machines == "[]" ? "any" : environment.Machines)}");
        return new() { Outcome = CommandOutcome.Ok };
    }
}

public sealed class EnvironmentDeletedHandler : IEventHandler<EnvironmentDeleted, SessionCommandResult>
{
    public SessionCommandResult? Validate(EnvironmentDeleted ev) => null;

    public SessionCommandResult Consume(EnvironmentDeleted ev)
    {
        var table = Ambient.Relation<ISecretEnvironmentTable>();
        if (table.FindByNameOrDefault(ev.Name) is not { } environment) return new() { Outcome = CommandOutcome.NotFound };
        if (!Catalog.IsAdmin(ev.UserId)) return new() { Outcome = CommandOutcome.Forbidden };
        if (Ambient.Relation<IProfileTable>().Any(profile => profile.EnvironmentName == environment.Name) ||
            Ambient.Relation<IEnvironmentApprovalTable>().Any(approval => approval.EnvironmentId == environment.Id && approval.Status == EnvironmentApprovalStatus.Pending))
            return new() { Outcome = CommandOutcome.Invalid };
        var secrets = Ambient.Relation<ISecretTable>();
        foreach (var secret in secrets.ListById(environment.Id).ToList())
        {
            Secrets.Retire(secret, ev.When); // still masked a while
            secrets.RemoveById(secret.OwnerId, secret.Name);
            Secrets.ForgetUses(secret.OwnerId, secret.Name);
        }
        table.RemoveById(environment.Id);
        Audit.Record(ev.When, ev.UserId, "environment.deleted", ev.Name);
        return new() { Outcome = CommandOutcome.Ok };
    }
}

public sealed class EnvironmentApprovalAnsweredHandler(IDomainSignals signals, BTDB.BackendActions.BackendActions actions)
    : IEventHandler<EnvironmentApprovalAnswered, SessionCommandResult>
{
    public SessionCommandResult? Validate(EnvironmentApprovalAnswered ev) => null;

    public SessionCommandResult Consume(EnvironmentApprovalAnswered ev)
    {
        var approvals = Ambient.Relation<IEnvironmentApprovalTable>();
        if (approvals.FindByIdOrDefault(ev.SessionId) is not { Status: EnvironmentApprovalStatus.Pending } approval ||
            Ambient.Relation<ISessionTable>().FindByIdOrDefault(ev.SessionId) is not { Status: not (SessionStatus.Stopped or SessionStatus.Failed) } session ||
            SecretEnvironments.Find(approval.EnvironmentId) is not { } environment)
            return new() { Outcome = CommandOutcome.NotFound };
        if (!SecretEnvironments.MayAnswer(environment, ev.UserId, approval.RequestedById)) return new() { Outcome = CommandOutcome.Forbidden };
        approval.Status = ev.Approve ? EnvironmentApprovalStatus.Approved : EnvironmentApprovalStatus.Rejected;
        approval.AnsweredById = ev.UserId;
        approval.AnsweredAt = ev.When;
        var command = approval.StartCommand;
        approval.StartCommand = "";
        approvals.Update(approval);
        var reviewer = Ambient.Relation<IUserTable>().FindByIdOrDefault(ev.UserId)?.Name ?? "A reviewer";
        if (ev.Approve)
        {
            Sessions.AppendItem(session, SessionItemKind.Notice, ev.When, $"{reviewer} approved the use of the {environment.Name} environment.");
            if (session.Harness == Harnesses.Agent) AgentRunners.Claim(signals, actions, session, ev.When);
            else Sessions.IssueCommand(signals, session.MachineId, command, ev.When);
        }
        else
        {
            Sessions.AppendItem(session, SessionItemKind.Notice, ev.When,
                $"{reviewer} did not approve the use of the {environment.Name} environment, so this session did not start.", isError: true);
            session.Status = SessionStatus.Failed;
        }
        Sessions.Changed(signals, session, ev.When);
        Audit.Record(ev.When, ev.UserId, ev.Approve ? "environment.approved" : "environment.rejected", environment.Name, $"session {session.Id}");
        return new() { Outcome = CommandOutcome.Ok, SessionId = session.Id };
    }
}
