using BTDB.ODBLayer;
using BTDB.Replication.EventSourcing;

namespace BtdbVolume;

/// <summary>One persisted transcript item, as planned for BoSoFa's §4.2 <c>SessionItem</c> relation.</summary>
public class SessionItem
{
    [PrimaryKey(1)] public ulong SessionId { get; set; }
    [PrimaryKey(2)] public ulong Seq { get; set; }
    public string Kind { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTime When { get; set; }
    public ulong EventId { get; set; }
}

public interface ISessionItemTable : IRelation<SessionItem>
{
    IEnumerable<SessionItem> FindById(ulong sessionId);
    IEnumerable<SessionItem> ListById(ulong sessionId, AdvancedEnumeratorParam<ulong> param);
    int CountById(ulong sessionId);
}

[BTDB.Generate]
public class SessionItemData
{
    public ulong Seq { get; set; }
    public string Kind { get; set; } = "";
    public string Payload { get; set; } = "";
}

/// <summary>Appends one or more items of one session; the daemon would send them in seq order.</summary>
public class SessionItemsAppended : PureEvent
{
    public ulong SessionId { get; set; }
    public List<SessionItemData> Items { get; set; } = new();
}

public sealed class SessionItemsAppendedHandler : IEventHandler<SessionItemsAppended>
{
    public void Validate(SessionItemsAppended ev)
    {
        if (ev.Items.Count == 0) throw new ArgumentException("No items.");
    }

    public void Consume(SessionItemsAppended ev)
    {
        var items = Ambient.Relation<ISessionItemTable>();
        foreach (var item in ev.Items)
            items.Upsert(new()
            {
                SessionId = ev.SessionId, Seq = item.Seq, Kind = item.Kind, Payload = item.Payload, When = ev.When,
                EventId = ev.EventId
            });
    }
}

public static class SpikeDomain
{
    public static readonly Type[] Relations = [typeof(ISessionItemTable)];

    public static EventDispatcher CreateDispatcher() => new EventDispatcher()
        .Register(new SessionItemsAppendedHandler());
}
