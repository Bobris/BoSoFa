using System.Collections.Concurrent;
using System.Net.WebSockets;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Machines;

/// <summary>Daemons connected to this node. Single node for now; the cluster keeps presence per pod and routes to the
/// owning pod (docs/plan.md §4.2).</summary>
public sealed class DaemonConnections
{
    public sealed class Connection(ulong machineId, string daemonVersion, string hostName, DateTime connectedAt,
        WebSocket socket)
    {
        readonly SemaphoreSlim _send = new(1);
        readonly SemaphoreSlim _commands = new(0);

        public ulong MachineId { get; } = machineId;
        public string DaemonVersion { get; } = daemonVersion;
        public string HostName { get; } = hostName;
        public DateTime ConnectedAt { get; } = connectedAt;
        public WebSocket Socket { get; } = socket;

        /// <summary>WebSocket sends must not overlap; every sender of this connection goes through here.</summary>
        public async Task SendAsync(DaemonMessage message, CancellationToken cancellation)
        {
            await _send.WaitAsync(cancellation);
            try { await DaemonSocket.SendAsync(Socket, message, cancellation); }
            finally { _send.Release(); }
        }

        /// <summary>New commands are in the inbox.</summary>
        public void WakeCommands()
        {
            if (_commands.CurrentCount == 0) _commands.Release();
        }

        public async Task WaitForCommandsAsync(TimeSpan timeout, CancellationToken cancellation)
        {
            await _commands.WaitAsync(timeout, cancellation);
            while (_commands.Wait(0)) { }
        }
    }

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
