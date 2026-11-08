using System.Net.WebSockets;

namespace BoSoFa.Protocol;

/// <summary>Ordered daemon messages in both directions, whatever carries them: a WebSocket, or HTTP long-polling.</summary>
public interface IDaemonChannel
{
    /// <summary>The next message, or null when the other side closed the channel.</summary>
    Task<DaemonMessage?> ReceiveAsync(CancellationToken cancellation);

    /// <summary>Sends must not overlap: callers serialize them.</summary>
    Task SendAsync(DaemonMessage message, CancellationToken cancellation);

    /// <summary>Why the other side closed the channel, if it said.</summary>
    string? CloseReason { get; }

    Task CloseAsync(WebSocketCloseStatus status, string reason, CancellationToken cancellation);

    /// <summary>Drops the channel at once; the other side reconnects.</summary>
    void Abort();
}

public sealed class WebSocketChannel(WebSocket socket) : IDaemonChannel
{
    public Task<DaemonMessage?> ReceiveAsync(CancellationToken cancellation) => DaemonSocket.ReceiveAsync(socket, cancellation);

    public Task SendAsync(DaemonMessage message, CancellationToken cancellation) => DaemonSocket.SendAsync(socket, message, cancellation);

    public string? CloseReason => socket.CloseStatusDescription;

    public Task CloseAsync(WebSocketCloseStatus status, string reason, CancellationToken cancellation) =>
        socket.CloseAsync(status, reason, cancellation);

    public void Abort() => socket.Abort();
}

/// <summary>The long-poll transport (plan §5.3), for networks that do not let a WebSocket through.</summary>
public static class DaemonPoll
{
    public const string Path = "/api/daemon/v1/poll";
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(25);

    /// <summary>A channel nobody polled for this long is gone.</summary>
    public static readonly TimeSpan Idle = TimeSpan.FromSeconds(75);
}

public sealed record PollOpened(string ConnectionId);

/// <summary>Server → daemon messages after the cursor; the next poll passes <c>LastSequence</c> as <c>after</c>.</summary>
public sealed record PollBatch(long LastSequence, DaemonMessage[] Messages);
