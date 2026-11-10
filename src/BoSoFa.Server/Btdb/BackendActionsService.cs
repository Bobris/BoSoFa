using BTDB.BackendActions;
using BTDB.Replication;

namespace BoSoFa.Server.Btdb;

/// <summary>
/// BTDB backend actions on this node: its worker and, while the node leads, the coordinator. Long work on the server runs as an
/// action, so the leader spreads it over the nodes and starts it again elsewhere when a node goes. Registered after the node, so it
/// stops first: the worker interrupts its attempts while the publisher still runs, and they go on on another node.
/// </summary>
public sealed class BackendActionsService(BtdbNode node, BackendActions actions, ReplicationLeadership leadership, ReplicationLeaderChannel channel,
    ServerOptions options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        try
        {
            var publisher = await node.Ready.WaitAsync(stopping);
            var host = new BackendActionsHost(actions, publisher, leadership, channel, new BackendActionsOptions
            {
                WorkerCpu = options.ActionWorkerCpu ?? Math.Max(Environment.ProcessorCount * 100, 1000),
                // A node that goes takes its agent runs with it: another node starts them again after the worker timeout, or after the
                // lease when the leader went too, since a new leader never heard from that worker. Workers report every poll.
                Planner = { WorkerTimeout = TimeSpan.FromSeconds(10), Lease = TimeSpan.FromSeconds(15) },
                PollTimeout = TimeSpan.FromSeconds(3)
            });
            await host.RunAsync(stopping);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }
}
