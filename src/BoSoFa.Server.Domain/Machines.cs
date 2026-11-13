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

    /// <summary>The daemon's own identity; empty for a daemon too old to send one.</summary>
    public Guid InstanceId { get; set; }

    /// <summary><see cref="MachineResources"/> as JSON, or empty.</summary>
    public string ResourcesJson { get; set; } = "";

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

    /// <summary>The daemon's own identity (<see cref="EnrollStartRequest.InstanceId"/>); empty until a daemon that has one
    /// enrolls or connects.</summary>
    public Guid InstanceId { get; set; }

    /// <summary>The last <see cref="MachineResources"/> the daemon sent, as JSON: at enrollment and on every connect.</summary>
    public string ResourcesJson { get; set; } = "";
}

public interface IMachineTable : IRelation<Machine>
{
    void Insert(Machine machine);
    void Update(Machine machine);
    Machine? FindByIdOrDefault(ulong id);
    bool RemoveById(ulong id);
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
    public Guid InstanceId { get; set; }
    public string ResourcesJson { get; set; } = "";
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
            DaemonVersion = ev.DaemonVersion, PublicKey = ev.PublicKey, ExpiresAt = expiresAt, InstanceId = ev.InstanceId,
            ResourcesJson = ev.ResourcesJson
        });
        return new() { Accepted = true, ExpiresAt = expiresAt };
    }
}

/// <summary>A signed-in person approved a pending enrollment; the machine is theirs. A daemon they enrolled before (the
/// same <see cref="Machine.InstanceId"/>) gets its machine back, with the new key.</summary>
public class EnrollmentApproved : EventWithResult<EnrollmentApprovedResult>
{
    public string UserCode { get; set; } = "";
    public ulong ApproverId { get; set; }

    /// <summary>The machine's name; null keeps the host name, or the earlier name of a machine enrolled again.</summary>
    public string? Name { get; set; }
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
        var machines = Ambient.Relation<IMachineTable>();
        var machine = enrollment.InstanceId == Guid.Empty
            ? null
            : machines.FirstOrDefault(candidate => candidate.InstanceId == enrollment.InstanceId && candidate.OwnerId == ev.ApproverId);
        var again = machine != null;
        machine ??= new() { Id = Ambient.AllocateId(), OwnerId = ev.ApproverId, Name = enrollment.Name, InstanceId = enrollment.InstanceId };
        machine.Name = MachineNames.Clean(ev.Name) ?? machine.Name;
        (machine.Os, machine.Arch, machine.DaemonVersion, machine.PublicKey, machine.EnrolledAt) =
            (enrollment.Os, enrollment.Arch, enrollment.DaemonVersion, enrollment.PublicKey, ev.When);
        if (enrollment.ResourcesJson.Length > 0) machine.ResourcesJson = enrollment.ResourcesJson;
        if (again) machines.Update(machine);
        else machines.Insert(machine);
        enrollment.MachineId = machine.Id;
        enrollments.Update(enrollment);
        Audit.Record(ev.When, ev.ApproverId, again ? "machine.reenrolled" : "machine.enrolled", machine.Name, $"machine {machine.Id}");
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

    /// <summary>Where other nodes reach the holding node for its live deltas; empty in single-node mode.</summary>
    public string PeerUrl { get; set; } = "";

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
    public string PeerUrl { get; set; } = "";
    public string DaemonVersion { get; set; } = "";
    public string HostName { get; set; } = "";
    public Guid InstanceId { get; set; }
    public string ResourcesJson { get; set; } = "";
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
        var machines = Ambient.Relation<IMachineTable>();
        if (machines.FindByIdOrDefault(ev.MachineId) is not { } machine) return;
        // A machine enrolled by an older daemon learns its identity; one it has keeps it. The measurement stays for
        // while the machine is offline.
        var changed = false;
        if (machine.InstanceId == Guid.Empty && ev.InstanceId != Guid.Empty) (machine.InstanceId, changed) = (ev.InstanceId, true);
        if (ev.ResourcesJson.Length > 0 && ev.ResourcesJson != machine.ResourcesJson) (machine.ResourcesJson, changed) = (ev.ResourcesJson, true);
        if (ev.DaemonVersion.Length > 0 && ev.DaemonVersion != machine.DaemonVersion) (machine.DaemonVersion, changed) = (ev.DaemonVersion, true);
        if (changed) machines.Update(machine);
        Ambient.Relation<IMachinePresenceTable>().Upsert(new()
        {
            MachineId = ev.MachineId, NodeId = ev.NodeId, ConnectionId = ev.ConnectionId, PeerUrl = ev.PeerUrl,
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

/// <summary>The owner renamed a machine.</summary>
public class MachineRenamed : EventWithResult<SessionCommandResult>
{
    public ulong MachineId { get; set; }
    public ulong UserId { get; set; }
    public string Name { get; set; } = "";
}

public sealed class MachineRenamedHandler : IEventHandler<MachineRenamed, SessionCommandResult>
{
    public SessionCommandResult? Validate(MachineRenamed ev) =>
        MachineNames.Clean(ev.Name) == null ? new() { Outcome = CommandOutcome.Invalid } : null;

    public SessionCommandResult Consume(MachineRenamed ev)
    {
        var machines = Ambient.Relation<IMachineTable>();
        if (machines.FindByIdOrDefault(ev.MachineId) is not { } machine) return new() { Outcome = CommandOutcome.NotFound };
        if (machine.OwnerId != ev.UserId) return new() { Outcome = CommandOutcome.Forbidden };
        var previous = machine.Name;
        machine.Name = MachineNames.Clean(ev.Name)!;
        machines.Update(machine);
        Audit.Record(ev.When, ev.UserId, "machine.renamed", machine.Name, $"machine {machine.Id}, was {previous}");
        return new() { Outcome = CommandOutcome.Ok };
    }
}

/// <summary>The owner or an admin removed a machine. Its daemon can no longer sign in, and is disconnected; enrolling it again
/// makes a new machine. Its sessions stay readable and stop, as nothing can run them any more.</summary>
public class MachineRemoved : EventWithResult<SessionCommandResult>
{
    public ulong MachineId { get; set; }
    public ulong UserId { get; set; }
}

public sealed class MachineRemovedHandler(IDomainSignals signals, BTDB.BackendActions.BackendActions actions)
    : IEventHandler<MachineRemoved, SessionCommandResult>
{
    public SessionCommandResult? Validate(MachineRemoved ev) => null;

    public SessionCommandResult Consume(MachineRemoved ev)
    {
        var machines = Ambient.Relation<IMachineTable>();
        if (machines.FindByIdOrDefault(ev.MachineId) is not { } machine) return new() { Outcome = CommandOutcome.NotFound };
        if (!MachineAccess.CanRemove(ev.UserId, machine)) return new() { Outcome = CommandOutcome.Forbidden };
        foreach (var session in Ambient.Relation<ISessionTable>()
                     .Where(session => session.MachineId == machine.Id && session.Status is not (SessionStatus.Stopped or SessionStatus.Failed))
                     .ToList())
        {
            if (session.Harness == Harnesses.Agent) AgentRunners.Release(signals, actions, session);
            Sessions.CancelPending(session, ev.When);
            session.Status = SessionStatus.Stopped;
            Sessions.AppendItem(session, SessionItemKind.Notice, ev.When, $"The machine {machine.Name} was removed, so this session stopped.");
            Sessions.Changed(signals, session, ev.When);
        }
        Secrets.MachineRemoved(machine.Id, machine.Name);
        Ambient.Relation<IMachineCommandTable>().RemoveById(machine.Id);
        Ambient.Relation<IMachineHarnessTable>().RemoveById(machine.Id);
        Ambient.Relation<IMachinePresenceTable>().RemoveById(machine.Id);
        machines.RemoveById(machine.Id);
        Audit.Record(ev.When, ev.UserId, "machine.removed", machine.Name, $"machine {machine.Id}");
        var machineId = machine.Id;
        Ambient.RegisterPostCommit(() => signals.MachinePresenceChanged(machineId, null)); // the node holding its daemon closes it
        return new() { Outcome = CommandOutcome.Ok };
    }
}

public static class MachineAccess
{
    /// <summary>Its owner, or an admin cleaning up.</summary>
    public static bool CanRemove(ulong userId, Machine machine) => userId != 0 && (machine.OwnerId == userId || Catalog.IsAdmin(userId));

    /// <summary>Who may start sessions, answer approvals, and reach the machine's files and terminals.</summary>
    public static bool CanControl(ulong userId, Machine machine) => userId != 0 && machine.OwnerId == userId;
}
