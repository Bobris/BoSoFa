using BoSoFa.Protocol;
using BoSoFa.Server.Btdb;
using BoSoFa.Server.Domain;
using BTDB.ODBLayer;
using BTDB.Replication.EventSourcing;
using Microsoft.AspNetCore.Http.HttpResults;

namespace BoSoFa.Server.Tokens;

/// <summary>The audit log, newest first, for admins.</summary>
public static class AuditEndpoints
{
    const int PageSize = 100;

    public static void MapAuditEndpoints(this WebApplication app) => app.MapGet("/api/v1/audit", ListAsync).RequireAuthorization();

    static async Task<Results<Ok<AuditEntryDto[]>, ForbidHttpResult>> ListAsync(ulong? before, BtdbNode node, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using var query = publisher.StartQuery();
        var users = Ambient.Relation<IUserTable>();
        if (users.FindByIdOrDefault(http.User.UserId() ?? 0)?.Role != UserRole.Admin) return TypedResults.Forbid();
        var range = before is { } last
            ? new AdvancedEnumeratorParam<ulong>(EnumerationOrder.Descending, 0, KeyProposition.Ignored, last, KeyProposition.Excluded)
            : new AdvancedEnumeratorParam<ulong>(EnumerationOrder.Descending);
        return TypedResults.Ok(Ambient.Relation<IAuditEntryTable>().ListById(range).Take(PageSize)
            .Select(entry => new AuditEntryDto(entry.Id, entry.At, entry.ActorId, users.FindByIdOrDefault(entry.ActorId)?.Name ?? "", entry.Action,
                entry.Target, entry.Detail)).ToArray());
    }

    /// <summary>Records something that is not a command of its own; a lost entry must not fail what it describes.</summary>
    public static async Task RecordAsync(EventPublisher publisher, ulong actorId, string action, string target, string detail, ILogger logger)
    {
        try { await publisher.PublishAsync(new ActionAudited { ActorId = actorId, Action = action, Target = target, Detail = detail }); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException)
        {
            logger.LogWarning("Could not audit {Action} on {Target}: {Error}", action, target, exception.Message);
        }
    }
}
