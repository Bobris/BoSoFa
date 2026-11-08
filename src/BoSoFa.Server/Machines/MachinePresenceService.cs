using BoSoFa.Server.Btdb;
using BoSoFa.Server.Domain;
using BTDB.Replication;
using BTDB.Replication.EventSourcing;

namespace BoSoFa.Server.Machines;

/// <summary>
/// Keeps <see cref="MachinePresence"/> honest. When this node starts, connections it held before (a crash leaves them
/// behind) are expired before any daemon may connect. While this node leads, connections of nodes that left the
/// cluster are expired too, so a machine whose node died shows offline until its daemon reconnects elsewhere.
/// </summary>
public sealed class MachinePresenceService(BtdbNode node, BtdbNodeOptions options, ReplicationLeadership leadership,
    ILogger<MachinePresenceService> logger) : BackgroundService
{
    static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);
    readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once this node's earlier connections are expired; daemons connect only after it.</summary>
    public Task Ready => _ready.Task;

    public string NodeId => options.NodeId;

    /// <summary>This node's peer URL as other nodes reach it; empty in single-node mode.</summary>
    public string PeerUrl => Uri.TryCreate(options.Endpoint, UriKind.Absolute, out _) ? options.Endpoint : "";

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        try
        {
            var publisher = await node.Ready.WaitAsync(stopping);
            await publisher.PublishAsync(new NodePresenceExpired { NodeId = options.NodeId }, stopping);
            _ready.TrySetResult();
            await leadership.RunWhileLeaderAsync(cancellation => SweepAsync(publisher, cancellation), stopping);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            _ready.TrySetCanceled(stopping);
        }
    }

    async Task SweepAsync(EventPublisher publisher, CancellationToken cancellation)
    {
        while (true)
        {
            await Task.Delay(SweepInterval, cancellation);
            var cluster = publisher.Cluster;
            if (cluster.Settling) continue; // the view may miss nodes that are alive
            List<string> gone;
            using (publisher.StartQuery())
                gone = Ambient.Relation<IMachinePresenceTable>().Select(row => row.NodeId).Distinct()
                    .Where(id => id != options.NodeId && !cluster.LiveNodeIds.Contains(id)).ToList();
            foreach (var nodeId in gone)
            {
                logger.LogInformation("Node {NodeId} left the cluster; its daemon connections are gone", nodeId);
                await publisher.PublishAsync(new NodePresenceExpired { NodeId = nodeId }, cancellation);
            }
        }
    }
}
