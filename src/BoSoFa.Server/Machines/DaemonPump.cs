using System.Net.WebSockets;
using System.Text.Json;
using BoSoFa.Protocol;
using BoSoFa.Server.Btdb;
using BoSoFa.Server.Domain;
using BoSoFa.Server.Live;
using BTDB.Replication.EventSourcing;

namespace BoSoFa.Server.Machines;

/// <summary>Serves one connected daemon, over whichever transport it came: registers its presence, feeds it commands,
/// stores its reports and relays what is live.</summary>
public sealed class DaemonPump(DaemonConnections connections, BtdbNode node, LiveHub hub, MachinePresenceService presence, LiveRelay relay,
    TimeProvider time, ILoggerFactory loggers)
{
    /// <summary>Null when this daemon may connect, else why not.</summary>
    public static string? RefusalOf(HelloMessage hello) => hello.ProtocolVersion == ProtocolVersion.Current
        ? null
        : $"Protocol {hello.ProtocolVersion} is not supported; the server speaks {ProtocolVersion.Current}.";

    /// <summary>Runs until the channel ends.</summary>
    public async Task RunAsync(IDaemonChannel channel, string connectionId, ulong machineId, HelloMessage hello, CancellationToken cancellation)
    {
        var logger = loggers.CreateLogger("BoSoFa.Server.Daemon");
        var publisher = await node.Ready.WaitAsync(cancellation);
        await presence.Ready.WaitAsync(cancellation); // this node's older connections are expired first
        var connection = new DaemonConnections.Connection(machineId, connectionId, hello.DaemonVersion, hello.HostName,
            time.GetUtcNow().UtcDateTime, channel);
        connections.Add(connection);
        // Recorded as data, so every node knows the machine is online here; a newer connection elsewhere closes this one.
        await publisher.PublishAsync(new MachineConnected
        {
            MachineId = machineId, NodeId = presence.NodeId, ConnectionId = connection.ConnectionId, PeerUrl = presence.PeerUrl,
            DaemonVersion = hello.DaemonVersion, HostName = hello.HostName, InstanceId = hello.InstanceId ?? Guid.Empty,
            ResourcesJson = MachineEndpoints.ResourcesJson(hello.Resources)
        }, cancellation);
        connection.Registered = true;
        logger.LogInformation("Machine {MachineId} connected ({HostName}, daemon {Version})", machineId, hello.HostName,
            hello.DaemonVersion);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task? commands = null;
        try
        {
            await connection.SendAsync(new WelcomeMessage(machineId, Api.Version), cancellation);
            commands = MachineEndpoints.SendCommandsAsync(connection, publisher, stop.Token);
            while (await channel.ReceiveAsync(cancellation) is { } message)
                switch (message)
                {
                    case PingMessage ping:
                        await connection.SendAsync(new PongMessage(ping.Sequence), cancellation);
                        break;
                    case CommandAckMessage ack:
                        await publisher.PublishAsync(new MachineCommandsAcked { MachineId = machineId, CommandIds = [..ack.Ids] },
                            cancellation);
                        break;
                    case OutboxMessage outbox:
                        await connection.SendAsync(await MachineEndpoints.StoreReportsAsync(publisher, machineId, outbox), cancellation);
                        break;
                    case InventoryMessage inventory:
                        await publisher.PublishAsync(new MachineInventoryReported
                        {
                            MachineId = machineId,
                            Harnesses = JsonSerializer.Serialize(inventory.Harnesses, ProtocolJsonContext.Default.InstalledHarnessArray)
                        }, cancellation);
                        break;
                    case RpcResponseMessage response:
                        connection.CompleteCall(response);
                        break;
                    case TerminalOutputMessage output:
                        var liveOutput = new LiveTerminalOutput(output.TerminalId, output.Offset, output.Data);
                        hub.Terminal(machineId, output.TerminalId, liveOutput);
                        relay.Publish(machineId, liveOutput);
                        break;
                    case TerminalExitedMessage exited:
                        var liveExited = new LiveTerminalExited(exited.TerminalId, exited.ExitCode, null);
                        hub.Terminal(machineId, exited.TerminalId, liveExited);
                        relay.Publish(machineId, liveExited);
                        break;
                    case DeltaMessage delta:
                        if (MachineEndpoints.SessionBelongsTo(publisher, delta.SessionId, machineId))
                        {
                            var live = new LiveDelta(delta.SessionId, delta.Ref, delta.Text);
                            hub.Delta(live);
                            relay.Publish(machineId, live); // to nodes whose clients watch this machine's sessions
                        }
                        break;
                }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException) { }
        finally
        {
            connections.Remove(connection);
            connection.Close();
            try
            {
                await publisher.PublishAsync(
                    new MachineDisconnected { MachineId = machineId, ConnectionId = connection.ConnectionId });
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or IOException)
            {
                // The node is stopping; it expires its connections when it starts again (MachinePresenceService).
            }
            await stop.CancelAsync();
            if (commands != null)
                try { await commands; }
                catch (Exception) { /* the connection is gone */ }
            logger.LogInformation("Machine {MachineId} disconnected", machineId);
        }
    }
}
