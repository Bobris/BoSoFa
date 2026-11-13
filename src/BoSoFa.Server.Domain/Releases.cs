using BTDB.Replication.EventSourcing;

namespace BoSoFa.Server.Domain;

// Retired events of the signed releases, channels and rollouts that came before the server carried the daemon and the
// desktop app itself (plan §5.7). Older histories hold them, and every node must still read them, so they stay with
// handlers that change nothing. Never publish them.

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

public class DesktopReleasePublished : EventWithResult<SessionCommandResult>
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

public class DaemonRolloutTicked : PureEvent;

public sealed class RetiredReleaseHandlers : IEventHandler<DaemonReleasePublished, SessionCommandResult>,
    IEventHandler<DesktopReleasePublished, SessionCommandResult>, IEventHandler<MachineUpdatePolicySet, SessionCommandResult>,
    IEventHandler<DaemonRolloutStarted, DaemonRolloutResult>, IEventHandler<DaemonRolloutHaltRequested, DaemonRolloutResult>,
    IEventHandler<DaemonRolloutTicked>
{
    static SessionCommandResult Gone => new() { Outcome = CommandOutcome.Invalid };

    public SessionCommandResult? Validate(DaemonReleasePublished ev) => Gone;
    public SessionCommandResult Consume(DaemonReleasePublished ev) => Gone;
    public SessionCommandResult? Validate(DesktopReleasePublished ev) => Gone;
    public SessionCommandResult Consume(DesktopReleasePublished ev) => Gone;
    public SessionCommandResult? Validate(MachineUpdatePolicySet ev) => Gone;
    public SessionCommandResult Consume(MachineUpdatePolicySet ev) => Gone;
    public DaemonRolloutResult? Validate(DaemonRolloutStarted ev) => new() { Outcome = CommandOutcome.Invalid };
    public DaemonRolloutResult Consume(DaemonRolloutStarted ev) => new() { Outcome = CommandOutcome.Invalid };
    public DaemonRolloutResult? Validate(DaemonRolloutHaltRequested ev) => new() { Outcome = CommandOutcome.Invalid };
    public DaemonRolloutResult Consume(DaemonRolloutHaltRequested ev) => new() { Outcome = CommandOutcome.Invalid };
    public void Validate(DaemonRolloutTicked ev) { }
    public void Consume(DaemonRolloutTicked ev) { }
}
