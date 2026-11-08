using BTDB.ODBLayer;
using BTDB.Replication.EventSourcing;

namespace BoSoFa.Server.Domain;

/// <summary>Who did what (plan §4). Most entries are written by the handler of the command itself, so a command and its
/// entry commit together; actions that are not commands (a file transfer, a lease) come as <see cref="ActionAudited"/>.</summary>
public class AuditEntry
{
    [PrimaryKey(1)] public ulong Id { get; set; }
    public DateTime At { get; set; }

    /// <summary>0 when no person did it.</summary>
    public ulong ActorId { get; set; }
    public string Action { get; set; } = "";
    public string Target { get; set; } = "";
    public string Detail { get; set; } = "";
}

public interface IAuditEntryTable : IRelation<AuditEntry>
{
    void Insert(AuditEntry entry);
    IEnumerable<AuditEntry> ListById(AdvancedEnumeratorParam<ulong> range);
}

public static class Audit
{
    /// <summary>Must run inside a handler: it allocates an ID and uses the event's time.</summary>
    public static void Record(DateTime when, ulong actorId, string action, string target, string detail = "") =>
        Ambient.Relation<IAuditEntryTable>().Insert(new()
        {
            Id = Ambient.AllocateId(), At = when, ActorId = actorId, Action = action, Target = target, Detail = detail
        });
}

public class ActionAudited : PureEvent
{
    public ulong ActorId { get; set; }
    public string Action { get; set; } = "";
    public string Target { get; set; } = "";
    public string Detail { get; set; } = "";
}

public sealed class ActionAuditedHandler : IEventHandler<ActionAudited>
{
    public void Validate(ActionAudited ev) { }

    public void Consume(ActionAudited ev) => Audit.Record(ev.When, ev.ActorId, ev.Action, ev.Target, ev.Detail);
}
