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
