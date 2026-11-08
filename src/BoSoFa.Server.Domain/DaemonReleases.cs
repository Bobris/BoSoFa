using System.Text.Json;
using BoSoFa.Protocol;
using BTDB.ODBLayer;
using BTDB.Replication.EventSourcing;

namespace BoSoFa.Server.Domain;

/// <summary>A signed daemon build (plan §5.7). The server only stores it: machines check the signature themselves.</summary>
public class DaemonRelease
{
    [PrimaryKey(1)] public string Version { get; set; } = "";
    public string Channel { get; set; } = "";
    public string Manifest { get; set; } = "";
    public string Signature { get; set; } = "";
    public DateTime PublishedAt { get; set; }
}

public interface IDaemonReleaseTable : IRelation<DaemonRelease>
{
    DaemonRelease? FindByIdOrDefault(string version);
}

/// <summary>Kept apart from <see cref="Machine"/>; absent means the stable channel, not pinned.</summary>
public class MachineUpdatePolicy
{
    [PrimaryKey(1)] public ulong MachineId { get; set; }
    public string Channel { get; set; } = "";
    public string? PinnedVersion { get; set; }
}

public interface IMachineUpdatePolicyTable : IRelation<MachineUpdatePolicy>
{
    MachineUpdatePolicy? FindByIdOrDefault(ulong machineId);
}

public class DaemonRollout
{
    [PrimaryKey(1)] public ulong Id { get; set; }
    public string Version { get; set; } = "";
    public string Channel { get; set; } = "";
    public int WaveSize { get; set; }
    public int FailAfterSeconds { get; set; }
    public DaemonRolloutStatus Status { get; set; }
    public DateTime StartedAt { get; set; }
    public string Detail { get; set; } = "";
}

public interface IDaemonRolloutTable : IRelation<DaemonRollout>
{
    void Insert(DaemonRollout rollout);
    void Update(DaemonRollout rollout);
    DaemonRollout? FindByIdOrDefault(ulong id);
}

/// <summary>A machine that was told to update, by one rollout.</summary>
public class DaemonRolloutMachine
{
    [PrimaryKey(1)] public ulong RolloutId { get; set; }
    [PrimaryKey(2)] public ulong MachineId { get; set; }
    public DateTime IssuedAt { get; set; }
    public bool Updated { get; set; }
}

public interface IDaemonRolloutMachineTable : IRelation<DaemonRolloutMachine>
{
    void Insert(DaemonRolloutMachine machine);
    void Update(DaemonRolloutMachine machine);
    IEnumerable<DaemonRolloutMachine> ListById(ulong rolloutId);
}

[BTDB.Generate]
public class DaemonRolloutResult
{
    public CommandOutcome Outcome { get; set; }
    public ulong RolloutId { get; set; }
}

public class DaemonReleasePublished : EventWithResult<SessionCommandResult>
{
    public ulong UserId { get; set; }
    public string Version { get; set; } = "";
    public string Channel { get; set; } = "";
    public string Manifest { get; set; } = "";
    public string Signature { get; set; } = "";
}

public class MachineUpdatePolicySet : EventWithResult<SessionCommandResult>
{
    public ulong UserId { get; set; }
    public ulong MachineId { get; set; }
    public string Channel { get; set; } = "";
    public string? PinnedVersion { get; set; }
}

public class DaemonRolloutStarted : EventWithResult<DaemonRolloutResult>
{
    public ulong UserId { get; set; }
    public string Version { get; set; } = "";
    public int WaveSize { get; set; }
    public int FailAfterSeconds { get; set; }
}

public class DaemonRolloutHaltRequested : EventWithResult<DaemonRolloutResult>
{
    public ulong UserId { get; set; }
    public ulong RolloutId { get; set; }
}

/// <summary>Time passed. Any node may publish it while a rollout runs; extra ticks change nothing.</summary>
public class DaemonRolloutTicked : PureEvent;

public sealed class DaemonReleasePublishedHandler : IEventHandler<DaemonReleasePublished, SessionCommandResult>
{
    public SessionCommandResult? Validate(DaemonReleasePublished ev) =>
        DaemonReleases.IsValidVersion(ev.Version) && DaemonReleases.IsValidChannel(ev.Channel) ? null : new() { Outcome = CommandOutcome.Invalid };

    public SessionCommandResult Consume(DaemonReleasePublished ev)
    {
        if (!Catalog.IsAdmin(ev.UserId)) return new() { Outcome = CommandOutcome.Forbidden };
        Ambient.Relation<IDaemonReleaseTable>().Upsert(new()
        {
            Version = ev.Version, Channel = ev.Channel, Manifest = ev.Manifest, Signature = ev.Signature, PublishedAt = ev.When
        });
        Audit.Record(ev.When, ev.UserId, "daemon.published", ev.Version, ev.Channel);
        return new() { Outcome = CommandOutcome.Ok };
    }
}

public sealed class MachineUpdatePolicySetHandler : IEventHandler<MachineUpdatePolicySet, SessionCommandResult>
{
    public SessionCommandResult? Validate(MachineUpdatePolicySet ev) =>
        DaemonReleases.IsValidChannel(ev.Channel) && ev.PinnedVersion is null || ev.PinnedVersion is { } pin && DaemonReleases.IsValidVersion(pin) && DaemonReleases.IsValidChannel(ev.Channel)
            ? null
            : new() { Outcome = CommandOutcome.Invalid };

    public SessionCommandResult Consume(MachineUpdatePolicySet ev)
    {
        if (Ambient.Relation<IMachineTable>().FindByIdOrDefault(ev.MachineId) is not { } machine) return new() { Outcome = CommandOutcome.NotFound };
        if (!MachineAccess.CanControl(ev.UserId, machine) && !Catalog.IsAdmin(ev.UserId)) return new() { Outcome = CommandOutcome.Forbidden };
        Ambient.Relation<IMachineUpdatePolicyTable>().Upsert(new() { MachineId = ev.MachineId, Channel = ev.Channel, PinnedVersion = ev.PinnedVersion });
        Audit.Record(ev.When, ev.UserId, "machine.update-policy", machine.Name, ev.PinnedVersion is { } pin ? $"{ev.Channel}, pinned to {pin}" : ev.Channel);
        return new() { Outcome = CommandOutcome.Ok };
    }
}

public sealed class DaemonRolloutStartedHandler : IEventHandler<DaemonRolloutStarted, DaemonRolloutResult>
{
    public DaemonRolloutResult? Validate(DaemonRolloutStarted ev) =>
        ev.WaveSize is > 0 and <= 1000 && ev.FailAfterSeconds is > 0 and <= 86400 ? null : new() { Outcome = CommandOutcome.Invalid };

    public DaemonRolloutResult Consume(DaemonRolloutStarted ev)
    {
        if (!Catalog.IsAdmin(ev.UserId)) return new() { Outcome = CommandOutcome.Forbidden };
        if (Ambient.Relation<IDaemonReleaseTable>().FindByIdOrDefault(ev.Version) is not { } release) return new() { Outcome = CommandOutcome.NotFound };
        var rollouts = Ambient.Relation<IDaemonRolloutTable>();
        // Two at once would tell the same machine different things.
        if (rollouts.Any(rollout => rollout.Status == DaemonRolloutStatus.Running && rollout.Channel == release.Channel))
            return new() { Outcome = CommandOutcome.Invalid };
        var created = new DaemonRollout
        {
            Id = Ambient.AllocateId(), Version = ev.Version, Channel = release.Channel, WaveSize = ev.WaveSize, FailAfterSeconds = ev.FailAfterSeconds,
            Status = DaemonRolloutStatus.Running, StartedAt = ev.When
        };
        rollouts.Insert(created);
        Audit.Record(ev.When, ev.UserId, "daemon.rollout-started", ev.Version, $"{release.Channel}, {ev.WaveSize} at a time");
        return new() { Outcome = CommandOutcome.Ok, RolloutId = created.Id };
    }
}

public sealed class DaemonRolloutHaltRequestedHandler : IEventHandler<DaemonRolloutHaltRequested, DaemonRolloutResult>
{
    public DaemonRolloutResult? Validate(DaemonRolloutHaltRequested ev) => null;

    public DaemonRolloutResult Consume(DaemonRolloutHaltRequested ev)
    {
        if (!Catalog.IsAdmin(ev.UserId)) return new() { Outcome = CommandOutcome.Forbidden };
        var rollouts = Ambient.Relation<IDaemonRolloutTable>();
        if (rollouts.FindByIdOrDefault(ev.RolloutId) is not { } rollout) return new() { Outcome = CommandOutcome.NotFound };
        if (rollout.Status != DaemonRolloutStatus.Running) return new() { Outcome = CommandOutcome.Invalid };
        rollout.Status = DaemonRolloutStatus.Halted;
        rollout.Detail = "Halted by hand.";
        rollouts.Update(rollout);
        Audit.Record(ev.When, ev.UserId, "daemon.rollout-halted", rollout.Version);
        return new() { Outcome = CommandOutcome.Ok, RolloutId = rollout.Id };
    }
}

/// <summary>Moves running rollouts along: at most <c>WaveSize</c> machines between "told" and "back on the new version".</summary>
public sealed class DaemonRolloutTickedHandler(IDomainSignals signals) : IEventHandler<DaemonRolloutTicked>
{
    public void Validate(DaemonRolloutTicked ev) { }

    public void Consume(DaemonRolloutTicked ev)
    {
        foreach (var rollout in Ambient.Relation<IDaemonRolloutTable>().Where(rollout => rollout.Status == DaemonRolloutStatus.Running).ToList())
            Advance(rollout, ev.When);
    }

    static string VersionOf(Machine machine) =>
        Ambient.Relation<IMachinePresenceTable>().FindByIdOrDefault(machine.Id)?.DaemonVersion ?? machine.DaemonVersion;

    static void Finish(DaemonRollout rollout, DaemonRolloutStatus status, string detail, DateTime when)
    {
        rollout.Status = status;
        rollout.Detail = detail;
        Ambient.Relation<IDaemonRolloutTable>().Update(rollout);
        Audit.Record(when, 0, status == DaemonRolloutStatus.Done ? "daemon.rollout-done" : "daemon.rollout-halted", rollout.Version, detail);
    }

    void Advance(DaemonRollout rollout, DateTime when)
    {
        var release = Ambient.Relation<IDaemonReleaseTable>().FindByIdOrDefault(rollout.Version);
        if (release == null)
        {
            Finish(rollout, DaemonRolloutStatus.Halted, "The release is gone.", when);
            return;
        }
        var rids = JsonSerializer.Deserialize(release.Manifest, ProtocolJsonContext.Default.DaemonManifest)!.Artifacts.Select(artifact => artifact.Rid).ToHashSet();
        var policies = Ambient.Relation<IMachineUpdatePolicyTable>();
        var machines = Ambient.Relation<IMachineTable>().Where(machine =>
        {
            var policy = policies.FindByIdOrDefault(machine.Id);
            return (policy?.Channel ?? DaemonReleases.Stable) == rollout.Channel && (policy?.PinnedVersion ?? rollout.Version) == rollout.Version &&
                   rids.Contains(DaemonReleases.RidOf(machine.Os, machine.Arch));
        }).ToList();
        var progress = Ambient.Relation<IDaemonRolloutMachineTable>();
        var told = progress.ListById(rollout.Id).ToDictionary(row => row.MachineId);
        var inFlight = 0;
        foreach (var row in told.Values.Where(row => !row.Updated).ToList())
        {
            var machine = machines.FirstOrDefault(candidate => candidate.Id == row.MachineId);
            if (machine != null && VersionOf(machine) == rollout.Version)
            {
                row.Updated = true;
                progress.Update(row);
            }
            else if (machine != null && (when - row.IssuedAt).TotalSeconds > rollout.FailAfterSeconds)
            {
                Finish(rollout, DaemonRolloutStatus.Halted, $"{machine.Name} did not come back on {rollout.Version}.", when);
                return;
            }
            else if (machine != null) inFlight++;
        }
        var waiting = machines.Where(machine => VersionOf(machine) != rollout.Version && !told.ContainsKey(machine.Id)).ToList();
        if (waiting.Count == 0 && inFlight == 0)
        {
            Finish(rollout, DaemonRolloutStatus.Done, "", when);
            return;
        }
        // Offline machines wait: they get it when their turn comes after they reconnect.
        foreach (var machine in waiting.Where(machine => Ambient.Relation<IMachinePresenceTable>().FindByIdOrDefault(machine.Id) != null)
                     .OrderBy(machine => machine.Id).Take(rollout.WaveSize - inFlight))
        {
            progress.Insert(new() { RolloutId = rollout.Id, MachineId = machine.Id, IssuedAt = when });
            Sessions.IssueCommand(signals, machine.Id, new UpdateDaemonCommand(rollout.Version, release.Manifest, release.Signature), when);
        }
    }
}
