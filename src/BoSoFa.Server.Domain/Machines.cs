using BoSoFa.Protocol;
using BTDB.ODBLayer;
using BTDB.Replication.EventSourcing;

namespace BoSoFa.Server.Domain;

public class Enrollment
{
    [PrimaryKey(1)] public ulong Id { get; set; }

    /// <summary>Secret the daemon polls with; never shown to people.</summary>
    [SecondaryKey("Key")] public string Key { get; set; } = "";

    /// <summary>Short code a person types or follows to approve the machine.</summary>
    [SecondaryKey("UserCode")] public string UserCode { get; set; } = "";

    public string Name { get; set; } = "";
    public string Os { get; set; } = "";
    public string Arch { get; set; } = "";
    public string DaemonVersion { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public DateTime ExpiresAt { get; set; }

    /// <summary>The machine created on approval; zero while pending.</summary>
    public ulong MachineId { get; set; }

    public EnrollStatus StatusAt(DateTime now) =>
        MachineId != 0 ? EnrollStatus.Approved : now > ExpiresAt ? EnrollStatus.Expired : EnrollStatus.Pending;
}

public interface IEnrollmentTable : IRelation<Enrollment>
{
    void Insert(Enrollment enrollment);
    void Update(Enrollment enrollment);
    Enrollment? FindByKeyOrDefault(string key);
    Enrollment? FindByUserCodeOrDefault(string userCode);
}

public class Machine
{
    [PrimaryKey(1)] public ulong Id { get; set; }
    public ulong OwnerId { get; set; }
    public string Name { get; set; } = "";
    public string Os { get; set; } = "";
    public string Arch { get; set; } = "";
    public string DaemonVersion { get; set; } = "";

    /// <summary>ECDSA P-256 SubjectPublicKeyInfo, base64; verifies the daemon's token requests.</summary>
    public string PublicKey { get; set; } = "";

    public DateTime EnrolledAt { get; set; }
}

public interface IMachineTable : IRelation<Machine>
{
    void Insert(Machine machine);
    void Update(Machine machine);
    Machine? FindByIdOrDefault(ulong id);
}

/// <summary>A daemon asked to join. Key and user code are random values chosen by the server before publishing, so
/// consumption stays deterministic; a user code still pending elsewhere rejects the event and the caller retries.</summary>
public class EnrollmentStarted : EventWithResult<EnrollmentStartedResult>
{
    public string Key { get; set; } = "";
    public string UserCode { get; set; } = "";
    public string Name { get; set; } = "";
    public string Os { get; set; } = "";
    public string Arch { get; set; } = "";
    public string DaemonVersion { get; set; } = "";
    public string PublicKey { get; set; } = "";
}

[BTDB.Generate]
public class EnrollmentStartedResult
{
    public bool Accepted { get; set; }
    public DateTime ExpiresAt { get; set; }
}

public sealed class EnrollmentStartedHandler : IEventHandler<EnrollmentStarted, EnrollmentStartedResult>
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    public EnrollmentStartedResult? Validate(EnrollmentStarted ev)
    {
        if (string.IsNullOrWhiteSpace(ev.Key) || string.IsNullOrWhiteSpace(ev.UserCode) || string.IsNullOrWhiteSpace(ev.PublicKey))
            throw new ArgumentException("An enrollment needs a key, a user code and a public key.");
        return null;
    }

    public EnrollmentStartedResult Consume(EnrollmentStarted ev)
    {
        var enrollments = Ambient.Relation<IEnrollmentTable>();
        if (enrollments.FindByUserCodeOrDefault(ev.UserCode) is { } existing && existing.StatusAt(ev.When) == EnrollStatus.Pending ||
            enrollments.FindByKeyOrDefault(ev.Key) != null)
            return new() { Accepted = false };
        var expiresAt = ev.When + Lifetime;
        enrollments.Insert(new()
        {
            Id = Ambient.AllocateId(), Key = ev.Key, UserCode = ev.UserCode, Name = ev.Name, Os = ev.Os, Arch = ev.Arch,
            DaemonVersion = ev.DaemonVersion, PublicKey = ev.PublicKey, ExpiresAt = expiresAt
        });
        return new() { Accepted = true, ExpiresAt = expiresAt };
    }
}

/// <summary>A signed-in person approved a pending enrollment; the machine is theirs.</summary>
public class EnrollmentApproved : EventWithResult<EnrollmentApprovedResult>
{
    public string UserCode { get; set; } = "";
    public ulong ApproverId { get; set; }
}

[BTDB.Generate]
public class EnrollmentApprovedResult
{
    public EnrollStatus Status { get; set; }
    public ulong MachineId { get; set; }
}

public sealed class EnrollmentApprovedHandler : IEventHandler<EnrollmentApproved, EnrollmentApprovedResult>
{
    public EnrollmentApprovedResult? Validate(EnrollmentApproved ev) => null;

    public EnrollmentApprovedResult Consume(EnrollmentApproved ev)
    {
        var enrollments = Ambient.Relation<IEnrollmentTable>();
        var enrollment = enrollments.FindByUserCodeOrDefault(ev.UserCode);
        if (enrollment == null || Ambient.Relation<IUserTable>().FindByIdOrDefault(ev.ApproverId) == null)
            return new() { Status = EnrollStatus.Expired };
        var status = enrollment.StatusAt(ev.When);
        if (status != EnrollStatus.Pending) return new() { Status = status, MachineId = enrollment.MachineId };
        var machine = new Machine
        {
            Id = Ambient.AllocateId(), OwnerId = ev.ApproverId, Name = enrollment.Name, Os = enrollment.Os,
            Arch = enrollment.Arch, DaemonVersion = enrollment.DaemonVersion, PublicKey = enrollment.PublicKey,
            EnrolledAt = ev.When
        };
        Ambient.Relation<IMachineTable>().Insert(machine);
        enrollment.MachineId = machine.Id;
        enrollments.Update(enrollment);
        return new() { Status = EnrollStatus.Approved, MachineId = machine.Id };
    }
}

/// <summary>Which node holds a machine's daemon connection, so every node can tell whether it is online and where its
/// commands and live deltas go. At most one row per machine: the newest connection wins.</summary>
public class MachinePresence
{
    [PrimaryKey(1)] public ulong MachineId { get; set; }

    /// <summary>The node holding the connection (its replication node id).</summary>
    public string NodeId { get; set; } = "";

    /// <summary>Unique per connection, so a late disconnect of an older connection removes nothing.</summary>
    public string ConnectionId { get; set; } = "";

    public string DaemonVersion { get; set; } = "";
    public string HostName { get; set; } = "";
    public DateTime ConnectedAt { get; set; }
}

public interface IMachinePresenceTable : IRelation<MachinePresence>
{
    MachinePresence? FindByIdOrDefault(ulong machineId);
    bool RemoveById(ulong machineId);
}

/// <summary>A daemon connected to <see cref="NodeId"/>.</summary>
public class MachineConnected : PureEvent
{
    public ulong MachineId { get; set; }
    public string NodeId { get; set; } = "";
    public string ConnectionId { get; set; } = "";
    public string DaemonVersion { get; set; } = "";
    public string HostName { get; set; } = "";
}

public class MachineDisconnected : PureEvent
{
    public ulong MachineId { get; set; }
    public string ConnectionId { get; set; } = "";
}

/// <summary>Every connection held by a node is gone: the node restarted (it publishes this for itself before accepting
/// daemons) or left the cluster (the leader publishes it).</summary>
public class NodePresenceExpired : PureEvent
{
    public string NodeId { get; set; } = "";
}

public sealed class MachineConnectedHandler(IDomainSignals signals) : IEventHandler<MachineConnected>
{
    public void Validate(MachineConnected ev)
    {
        if (string.IsNullOrEmpty(ev.NodeId) || string.IsNullOrEmpty(ev.ConnectionId))
            throw new ArgumentException("A connection needs a node and a connection id.");
    }

    public void Consume(MachineConnected ev)
    {
        if (Ambient.Relation<IMachineTable>().FindByIdOrDefault(ev.MachineId) == null) return;
        Ambient.Relation<IMachinePresenceTable>().Upsert(new()
        {
            MachineId = ev.MachineId, NodeId = ev.NodeId, ConnectionId = ev.ConnectionId,
            DaemonVersion = ev.DaemonVersion, HostName = ev.HostName, ConnectedAt = ev.When
        });
        var (machineId, connectionId) = (ev.MachineId, ev.ConnectionId);
        Ambient.RegisterPostCommit(() => signals.MachinePresenceChanged(machineId, connectionId));
    }
}

public sealed class MachineDisconnectedHandler(IDomainSignals signals) : IEventHandler<MachineDisconnected>
{
    public void Validate(MachineDisconnected ev) { }

    public void Consume(MachineDisconnected ev)
    {
        var presence = Ambient.Relation<IMachinePresenceTable>();
        if (presence.FindByIdOrDefault(ev.MachineId)?.ConnectionId != ev.ConnectionId) return; // a newer one won
        presence.RemoveById(ev.MachineId);
        var machineId = ev.MachineId;
        Ambient.RegisterPostCommit(() => signals.MachinePresenceChanged(machineId, null));
    }
}

public sealed class NodePresenceExpiredHandler(IDomainSignals signals) : IEventHandler<NodePresenceExpired>
{
    public void Validate(NodePresenceExpired ev)
    {
        if (string.IsNullOrEmpty(ev.NodeId)) throw new ArgumentException("A node id is required.");
    }

    public void Consume(NodePresenceExpired ev)
    {
        var presence = Ambient.Relation<IMachinePresenceTable>();
        foreach (var row in presence.Where(row => row.NodeId == ev.NodeId).ToList())
        {
            presence.RemoveById(row.MachineId);
            var machineId = row.MachineId;
            Ambient.RegisterPostCommit(() => signals.MachinePresenceChanged(machineId, null));
        }
    }
}
