using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace BoSoFa.Server.Machines;

/// <summary>Daemons connected to this node. Single node for now; the cluster keeps presence per pod and routes to the
/// owning pod (docs/plan.md §4.2).</summary>
public sealed class DaemonConnections
{
    public sealed record Connection(ulong MachineId, string DaemonVersion, string HostName, DateTime ConnectedAt, WebSocket Socket);

    readonly ConcurrentDictionary<ulong, Connection> _connections = new();

    public Connection? Get(ulong machineId) => _connections.GetValueOrDefault(machineId);

    /// <summary>Registers a connection, replacing (and closing) an older one of the same machine.</summary>
    public void Add(Connection connection)
    {
        if (_connections.TryGetValue(connection.MachineId, out var previous) && previous.Socket != connection.Socket)
            previous.Socket.Abort();
        _connections[connection.MachineId] = connection;
    }

    public void Remove(Connection connection) =>
        _connections.TryRemove(new KeyValuePair<ulong, Connection>(connection.MachineId, connection));
}
