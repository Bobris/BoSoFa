using System.Text.Json;
using BoSoFa.Protocol;
using BTDB.ODBLayer;
using BTDB.Replication.EventSourcing;

namespace BoSoFa.Server.Domain;

/// <summary>A harness version machines may install (plan §5.4); its artifacts are protocol JSON.</summary>
public class HarnessRelease
{
    [PrimaryKey(1)] public string Harness { get; set; } = "";
    [PrimaryKey(2)] public string Version { get; set; } = "";
    public string Artifacts { get; set; } = "";
    public DateTime PublishedAt { get; set; }
}

public interface IHarnessReleaseTable : IRelation<HarnessRelease>
{
    HarnessRelease? FindByIdOrDefault(string harness, string version);
}

/// <summary>What a machine reports it has installed.</summary>
public class MachineHarness
{
    [PrimaryKey(1)] public ulong MachineId { get; set; }
    [PrimaryKey(2)] public string Harness { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Error { get; set; }
}

public interface IMachineHarnessTable : IRelation<MachineHarness>
{
    IEnumerable<MachineHarness> ListById(ulong machineId);
    int RemoveById(ulong machineId);
}

public class HarnessReleasePublished : EventWithResult<SessionCommandResult>
{
    public ulong UserId { get; set; }
    public string Harness { get; set; } = "";
    public string Version { get; set; } = "";
    public string Artifacts { get; set; } = "";
}

public class HarnessInstallRequested : EventWithResult<SessionCommandResult>
{
    public ulong UserId { get; set; }
    public ulong MachineId { get; set; }
    public string Harness { get; set; } = "";
    public string Version { get; set; } = "";
}

public class MachineInventoryReported : PureEvent
{
    public ulong MachineId { get; set; }
    public string Harnesses { get; set; } = "";
}

public sealed class HarnessReleasePublishedHandler : IEventHandler<HarnessReleasePublished, SessionCommandResult>
{
    public SessionCommandResult? Validate(HarnessReleasePublished ev) =>
        string.IsNullOrWhiteSpace(ev.Harness) || string.IsNullOrWhiteSpace(ev.Version) ? new() { Outcome = CommandOutcome.Invalid } : null;

    public SessionCommandResult Consume(HarnessReleasePublished ev)
    {
        if (Ambient.Relation<IUserTable>().FindByIdOrDefault(ev.UserId)?.Role != UserRole.Admin)
            return new() { Outcome = CommandOutcome.Forbidden };
        Ambient.Relation<IHarnessReleaseTable>().Upsert(new()
        {
            Harness = ev.Harness, Version = ev.Version, Artifacts = ev.Artifacts, PublishedAt = ev.When
        });
        Audit.Record(ev.When, ev.UserId, "harness.published", ev.Harness, ev.Version);
        return new() { Outcome = CommandOutcome.Ok };
    }
}

public sealed class HarnessInstallRequestedHandler(IDomainSignals signals) : IEventHandler<HarnessInstallRequested, SessionCommandResult>
{
    public SessionCommandResult? Validate(HarnessInstallRequested ev) => null;

    public SessionCommandResult Consume(HarnessInstallRequested ev)
    {
        var machine = Ambient.Relation<IMachineTable>().FindByIdOrDefault(ev.MachineId);
        var release = Ambient.Relation<IHarnessReleaseTable>().FindByIdOrDefault(ev.Harness, ev.Version);
        if (machine == null || release == null) return new() { Outcome = CommandOutcome.NotFound };
        var isAdmin = Ambient.Relation<IUserTable>().FindByIdOrDefault(ev.UserId)?.Role == UserRole.Admin;
        if (machine.OwnerId != ev.UserId && !isAdmin) return new() { Outcome = CommandOutcome.Forbidden };
        var platform = $"{machine.Os}-{machine.Arch}";
        var artifact = JsonSerializer.Deserialize(release.Artifacts, ProtocolJsonContext.Default.HarnessArtifactArray)!
            .FirstOrDefault(candidate => candidate.Platform == platform);
        if (artifact == null) return new() { Outcome = CommandOutcome.Invalid }; // no build for this platform
        Sessions.IssueCommand(signals, machine.Id, new InstallHarnessCommand(ev.Harness, ev.Version, artifact), ev.When);
        Audit.Record(ev.When, ev.UserId, "harness.install", ev.Harness, $"{ev.Version} on machine {ev.MachineId}");
        return new() { Outcome = CommandOutcome.Ok };
    }
}

public sealed class MachineInventoryReportedHandler : IEventHandler<MachineInventoryReported>
{
    public void Validate(MachineInventoryReported ev) => Parse(ev);

    static InstalledHarness[] Parse(MachineInventoryReported ev) =>
        JsonSerializer.Deserialize(ev.Harnesses, ProtocolJsonContext.Default.InstalledHarnessArray) ?? [];

    public void Consume(MachineInventoryReported ev)
    {
        var table = Ambient.Relation<IMachineHarnessTable>();
        table.RemoveById(ev.MachineId);
        foreach (var harness in Parse(ev))
            table.Upsert(new() { MachineId = ev.MachineId, Harness = harness.Harness, Version = harness.Version, Error = harness.Error });
    }
}
