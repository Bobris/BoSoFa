using BTDB.ODBLayer;
using BTDB.Replication.EventSourcing;

namespace BoSoFa.Server.Domain;

/// <summary>A desktop app that is signed in. Only hashes of its tokens are stored. The refresh token rotates at each use.</summary>
public class DesktopSession
{
    [PrimaryKey(1)] public ulong Id { get; set; }
    [SecondaryKey("RefreshHash")] public string RefreshHash { get; set; } = "";
    [SecondaryKey("PreviousRefreshHash")] public string PreviousRefreshHash { get; set; } = "";
    [SecondaryKey("CodeHash")] public string CodeHash { get; set; } = "";
    public ulong UserId { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime LastRefreshedAt { get; set; }
}

public interface IDesktopSessionTable : IRelation<DesktopSession>
{
    void Insert(DesktopSession session);
    void Update(DesktopSession session);
    DesktopSession? FindByIdOrDefault(ulong id);
    DesktopSession? FindByRefreshHashOrDefault(string refreshHash);
    DesktopSession? FindByPreviousRefreshHashOrDefault(string previousRefreshHash);
    DesktopSession? FindByCodeHashOrDefault(string codeHash);
    bool RemoveById(ulong id);
}

[BTDB.Generate]
public class DesktopSessionResult
{
    public CommandOutcome Outcome { get; set; }
    public ulong SessionId { get; set; }
    public ulong UserId { get; set; }
}

public class DesktopSessionStarted : EventWithResult<DesktopSessionResult>
{
    public ulong UserId { get; set; }
    public string CodeHash { get; set; } = "";
    public string RefreshHash { get; set; } = "";
    public string Name { get; set; } = "";
}

public class DesktopSessionRefreshed : EventWithResult<DesktopSessionResult>
{
    public string RefreshHash { get; set; } = "";
    public string NewRefreshHash { get; set; } = "";
}

public class DesktopSessionRevoked : EventWithResult<DesktopSessionResult>
{
    public string RefreshHash { get; set; } = "";
}

public sealed class DesktopSessionStartedHandler : IEventHandler<DesktopSessionStarted, DesktopSessionResult>
{
    public DesktopSessionResult? Validate(DesktopSessionStarted ev) =>
        ev.CodeHash.Length != 64 || ev.RefreshHash.Length != 64 || ev.Name.Length > 100 ? new() { Outcome = CommandOutcome.Invalid } : null;

    public DesktopSessionResult Consume(DesktopSessionStarted ev)
    {
        var sessions = Ambient.Relation<IDesktopSessionTable>();
        if (sessions.FindByCodeHashOrDefault(ev.CodeHash) != null) return new() { Outcome = CommandOutcome.Invalid }; // a code works once
        if (Ambient.Relation<IUserTable>().FindByIdOrDefault(ev.UserId) == null) return new() { Outcome = CommandOutcome.NotFound };
        var session = new DesktopSession
        {
            Id = Ambient.AllocateId(), RefreshHash = ev.RefreshHash, CodeHash = ev.CodeHash, UserId = ev.UserId, Name = ev.Name, CreatedAt = ev.When,
            LastRefreshedAt = ev.When
        };
        sessions.Insert(session);
        Audit.Record(ev.When, ev.UserId, "desktop.signed-in", ev.Name);
        return new() { Outcome = CommandOutcome.Ok, SessionId = session.Id, UserId = ev.UserId };
    }
}

public sealed class DesktopSessionRefreshedHandler : IEventHandler<DesktopSessionRefreshed, DesktopSessionResult>
{
    /// <summary>A desktop app unused for this long signs in again.</summary>
    public static readonly TimeSpan IdleLimit = TimeSpan.FromDays(60);

    public DesktopSessionResult? Validate(DesktopSessionRefreshed ev) =>
        ev.RefreshHash.Length != 64 || ev.NewRefreshHash.Length != 64 ? new() { Outcome = CommandOutcome.Invalid } : null;

    public DesktopSessionResult Consume(DesktopSessionRefreshed ev)
    {
        var sessions = Ambient.Relation<IDesktopSessionTable>();
        if (sessions.FindByPreviousRefreshHashOrDefault(ev.RefreshHash) is { } replayed)
        {
            // An old token came back, so it leaked or was copied: nobody can tell which holder is real.
            sessions.RemoveById(replayed.Id);
            Audit.Record(ev.When, replayed.UserId, "desktop.token-reused", replayed.Name);
            return new() { Outcome = CommandOutcome.Forbidden };
        }
        if (sessions.FindByRefreshHashOrDefault(ev.RefreshHash) is not { } session) return new() { Outcome = CommandOutcome.NotFound };
        if (ev.When - session.LastRefreshedAt > IdleLimit)
        {
            sessions.RemoveById(session.Id);
            return new() { Outcome = CommandOutcome.NotFound };
        }
        session.PreviousRefreshHash = session.RefreshHash;
        session.RefreshHash = ev.NewRefreshHash;
        session.LastRefreshedAt = ev.When;
        sessions.Update(session);
        return new() { Outcome = CommandOutcome.Ok, SessionId = session.Id, UserId = session.UserId };
    }
}

public sealed class DesktopSessionRevokedHandler : IEventHandler<DesktopSessionRevoked, DesktopSessionResult>
{
    public DesktopSessionResult? Validate(DesktopSessionRevoked ev) => null;

    public DesktopSessionResult Consume(DesktopSessionRevoked ev)
    {
        var sessions = Ambient.Relation<IDesktopSessionTable>();
        if (sessions.FindByRefreshHashOrDefault(ev.RefreshHash) is not { } session) return new() { Outcome = CommandOutcome.NotFound };
        sessions.RemoveById(session.Id);
        Audit.Record(ev.When, session.UserId, "desktop.signed-out", session.Name);
        return new() { Outcome = CommandOutcome.Ok, SessionId = session.Id, UserId = session.UserId };
    }
}
