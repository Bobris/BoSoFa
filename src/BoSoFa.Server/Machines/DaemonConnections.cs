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

        /// <summary>Unique per connection; recorded in <see cref="Domain.MachinePresence"/>.</summary>
        public string ConnectionId { get; } = Guid.NewGuid().ToString("N");

        /// <summary>Its <c>MachineConnected</c> is stored; before that, presence cannot name it yet.</summary>
        public bool Registered { get; set; }
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

    /// <summary>The machine's presence now names <paramref name="connectionId"/> (or nothing): a connection of it held
    /// here under another id was superseded, by a newer connection elsewhere or by expiry, so it is closed and the
    /// daemon reconnects and registers again.</summary>
    public void Supersede(ulong machineId, string? connectionId)
    {
        // Without a row, only a registered connection is stale: a new one's MachineConnected may still be on its way
        // (the old connection's disconnect can commit after the new one connected here).
        if (Get(machineId) is { } connection && connection.ConnectionId != connectionId &&
            (connectionId != null || connection.Registered))
            connection.Socket.Abort();
    }
}
