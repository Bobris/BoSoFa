using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;

namespace BoSoFa.Protocol;

/// <summary>Framing of <see cref="DaemonMessage"/>s on a WebSocket, shared by the server and the daemon.</summary>
public static class DaemonSocket
{
    public const string Path = "/api/daemon/v1/connect";
    public const int MaximumMessageBytes = 16 * 1024 * 1024;

    /// <summary>The next message, or null when the peer closed the connection.</summary>
    public static async Task<DaemonMessage?> ReceiveAsync(WebSocket socket, CancellationToken cancellation)
    {
        var buffer = new ArrayBufferWriter<byte>();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.GetMemory(64 * 1024), cancellation);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (socket.State == WebSocketState.CloseReceived)
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cancellation);
                return null;
            }
            buffer.Advance(result.Count);
            if (buffer.WrittenCount > MaximumMessageBytes)
                throw new WebSocketException(WebSocketError.Faulted, "Message too large.");
            if (result.EndOfMessage)
                return JsonSerializer.Deserialize(buffer.WrittenSpan, ProtocolJsonContext.Default.DaemonMessage);
        }
    }

    /// <summary>Sends one message. WebSocket sends must not overlap: callers serialize them.</summary>
    public static Task SendAsync(WebSocket socket, DaemonMessage message, CancellationToken cancellation) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, ProtocolJsonContext.Default.DaemonMessage),
            WebSocketMessageType.Text, true, cancellation);

    /// <summary>The WebSocket address of a server's daemon endpoint.</summary>
    public static Uri ConnectUri(Uri server) => new UriBuilder(server)
    {
        Scheme = server.Scheme == Uri.UriSchemeHttps ? "wss" : "ws", Path = Path, Query = ""
    }.Uri;
}
