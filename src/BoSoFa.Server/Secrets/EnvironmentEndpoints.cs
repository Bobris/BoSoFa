using BoSoFa.Protocol;
using BoSoFa.Server.Btdb;
using BoSoFa.Server.Domain;
using BTDB.Replication.EventSourcing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.HttpResults;

namespace BoSoFa.Server.Secrets;

/// <summary>
/// Environments (docs/secrets.md §10): named sets of secrets and variables that admins keep, with the machines their sessions may
/// run on and the reviewers who approve each session before it starts. Everyone sees them, as they see shared values; reviewers
/// answer the sessions that wait for them.
/// </summary>
public static class EnvironmentEndpoints
{
    public static void MapEnvironmentEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1/environments").RequireAuthorization();
        api.MapGet("", ListAsync);
        api.MapPut("/{name}", SaveAsync);
        api.MapDelete("/{name}", DeleteAsync);
        api.MapPut("/{environment}/secrets/{name}", (string environment, string name, SetSecretRequest request, BtdbNode node,
            IDataProtectionProvider protection, HttpContext http) => SecretEndpoints.SetAsync(new(null, environment), name, request, node, protection, http));
        api.MapPut("/{environment}/secrets/{name}/settings", (string environment, string name, SecretSettingsRequest request, BtdbNode node,
            HttpContext http) => SecretEndpoints.ChangeSettingsAsync(new(null, environment), name, request, node, http));
        api.MapDelete("/{environment}/secrets/{name}", (string environment, string name, BtdbNode node, HttpContext http) =>
            SecretEndpoints.DeleteAsync(new(null, environment), name, node, http));

        var approvals = app.MapGroup("/api/v1/environment-approvals").RequireAuthorization();
        approvals.MapGet("", ListApprovalsAsync);
        approvals.MapPost("/{sessionId}", AnswerAsync);
    }

    static async Task<EnvironmentDto[]> ListAsync(BtdbNode node, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using var query = publisher.StartQuery();
        var userId = http.User.UserId() ?? 0;
        var users = Ambient.Relation<IUserTable>();
        var machines = Ambient.Relation<IMachineTable>();
        var secrets = Ambient.Relation<ISecretTable>();
        return Ambient.Relation<ISecretEnvironmentTable>().OrderBy(environment => environment.Name, StringComparer.Ordinal).Select(environment => new EnvironmentDto(
            environment.Name, environment.Description,
            Domain.Secrets.Ids(environment.Reviewers).Select(id => new NamedRef(id, users.FindByIdOrDefault(id)?.Name ?? "someone removed")).ToArray(),
            Domain.Secrets.Ids(environment.Machines).Select(id => new NamedRef(id, machines.FindByIdOrDefault(id)?.Name ?? "a removed machine")).ToArray(),
            secrets.ListById(environment.Id).Select(secret => SecretEndpoints.ToDto(secret, userId)).OrderBy(secret => secret.Name, StringComparer.Ordinal).ToArray(),
            users.FindByIdOrDefault(environment.ChangedById)?.Name ?? "", environment.ChangedAt)).ToArray();
    }

    static async Task<Results<NoContent, ForbidHttpResult, BadRequest<string>>> SaveAsync(string name, SaveEnvironmentRequest request, BtdbNode node,
        HttpContext http)
    {
        if (!CatalogNames.IsValid(name))
            return TypedResults.BadRequest("A name is lowercase letters, digits, - and _, starting with a letter or a digit, at most 64 characters.");
        if (request.Description.Length > SecretEnvironments.MaxDescriptionLength)
            return TypedResults.BadRequest($"A description is at most {SecretEnvironments.MaxDescriptionLength} characters.");
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using (publisher.StartQuery())
        {
            if (request.Reviewers.FirstOrDefault(id => Ambient.Relation<IUserTable>().FindByIdOrDefault(id) == null) is not 0 and var person)
                return TypedResults.BadRequest($"There is no person {person}.");
            if (request.Machines.FirstOrDefault(id => Ambient.Relation<IMachineTable>().FindByIdOrDefault(id) == null) is not 0 and var machine)
                return TypedResults.BadRequest($"There is no machine {machine}.");
        }
        var result = await publisher.PublishAndWaitAsync(new EnvironmentSaved
        {
            UserId = http.User.UserId() ?? 0, Name = name, Description = request.Description, Reviewers = Domain.Secrets.Json(request.Reviewers),
            Machines = Domain.Secrets.Json(request.Machines)
        }, http.RequestAborted);
        return result.Outcome switch
        {
            CommandOutcome.Ok => TypedResults.NoContent(),
            CommandOutcome.Forbidden => TypedResults.Forbid(),
            _ => TypedResults.BadRequest("The environment could not be saved.")
        };
    }

    static async Task<Results<NoContent, NotFound, ForbidHttpResult, BadRequest<string>>> DeleteAsync(string name, BtdbNode node, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using (publisher.StartQuery())
        {
            if (SecretEnvironments.Find(name) is not { } environment) return TypedResults.NotFound();
            if (Ambient.Relation<IProfileTable>().FirstOrDefault(profile => profile.EnvironmentName == name) is { } profile)
                return TypedResults.BadRequest($"The profile {profile.Name} uses {name}: choose another environment there first.");
            if (Ambient.Relation<IEnvironmentApprovalTable>().Any(approval => approval.EnvironmentId == environment.Id && approval.Status == EnvironmentApprovalStatus.Pending))
                return TypedResults.BadRequest($"A session waits for a reviewer of {name}.");
        }
        var result = await publisher.PublishAndWaitAsync(new EnvironmentDeleted { UserId = http.User.UserId() ?? 0, Name = name }, http.RequestAborted);
        return result.Outcome switch
        {
            CommandOutcome.Ok => TypedResults.NoContent(),
            CommandOutcome.Forbidden => TypedResults.Forbid(),
            CommandOutcome.NotFound => TypedResults.NotFound(),
            _ => TypedResults.BadRequest($"{name} is in use.")
        };
    }

    /// <summary>The sessions that wait for the caller's approval, oldest first.</summary>
    static async Task<EnvironmentApprovalDto[]> ListApprovalsAsync(BtdbNode node, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using var query = publisher.StartQuery();
        var userId = http.User.UserId() ?? 0;
        var sessions = Ambient.Relation<ISessionTable>();
        var users = Ambient.Relation<IUserTable>();
        var machines = Ambient.Relation<IMachineTable>();
        var list = new List<EnvironmentApprovalDto>();
        foreach (var approval in Ambient.Relation<IEnvironmentApprovalTable>().Where(approval => approval.Status == EnvironmentApprovalStatus.Pending))
        {
            if (SecretEnvironments.Find(approval.EnvironmentId) is not { } environment || !SecretEnvironments.MayAnswer(environment, userId, approval.RequestedById))
                continue;
            if (sessions.FindByIdOrDefault(approval.SessionId) is not { Status: not (SessionStatus.Stopped or SessionStatus.Failed) } session) continue;
            list.Add(new(session.Id, environment.Name, users.FindByIdOrDefault(approval.RequestedById)?.Name ?? "",
                session.MachineId == 0 ? "the server" : machines.FindByIdOrDefault(session.MachineId)?.Name ?? "", session.Harness, session.ProfileName,
                session.WorkingDirectory, approval.RequestedAt));
        }
        return [.. list.OrderBy(approval => approval.RequestedAt)];
    }

    static async Task<Results<NoContent, NotFound, ForbidHttpResult>> AnswerAsync(ulong sessionId, AnswerEnvironmentApprovalRequest request, BtdbNode node,
        HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        var result = await publisher.PublishAndWaitAsync(new EnvironmentApprovalAnswered
        {
            UserId = http.User.UserId() ?? 0, SessionId = sessionId, Approve = request.Approve
        }, http.RequestAborted);
        return result.Outcome switch
        {
            CommandOutcome.Ok => TypedResults.NoContent(),
            CommandOutcome.Forbidden => TypedResults.Forbid(),
            _ => TypedResults.NotFound()
        };
    }
}
