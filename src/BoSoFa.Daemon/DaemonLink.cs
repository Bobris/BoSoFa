using System.Net.WebSockets;
using System.Security.Cryptography;
using BoSoFa.Protocol;

namespace BoSoFa.Daemon;

/// <summary>Keeps one WebSocket to the server: token, hello, keepalive, and reconnecting with backoff while the server
/// is away. Session commands and the outbox will run over this link.</summary>
public sealed class DaemonLink(DaemonState state, ECDsa key, TextWriter log)
{
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(15);
    static readonly TimeSpan MaximumBackoff = TimeSpan.FromSeconds(30);

    /// <summary>Set after each welcome; for tests and status output.</summary>
    public event Action<WelcomeMessage>? Connected;

    public async Task RunAsync(CancellationToken cancellation)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await ConnectOnceAsync(cancellation);
                backoff = TimeSpan.FromSeconds(1);
                log.WriteLine("Disconnected from the server.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                throw; // the machine was removed or its key changed: retrying cannot help
            }
            catch (Exception exception) when (exception is HttpRequestException or WebSocketException or IOException)
            {
                log.WriteLine($"Server unreachable ({exception.Message}); retrying in {backoff.TotalSeconds:0} s.");
            }
            try { await Task.Delay(backoff, cancellation); }
            catch (OperationCanceledException) { return; }
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaximumBackoff.Ticks));
        }
    }

    async Task ConnectOnceAsync(CancellationToken cancellation)
    {
        using var client = new ServerClient(state.Server);
        var token = await client.GetTokenAsync(state.MachineId, key, cancellation);
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + token.AccessToken);
        socket.Options.SetRequestHeader("User-Agent", $"bosofad/{Cli.Version}");
        await socket.ConnectAsync(DaemonSocket.ConnectUri(state.Server), cancellation);
        await DaemonSocket.SendAsync(socket, new HelloMessage(Cli.Version, ProtocolVersion.Current, DaemonStore.OsName(),
            DaemonStore.ArchName(), Environment.MachineName), cancellation);
        if (await DaemonSocket.ReceiveAsync(socket, cancellation) is not WelcomeMessage welcome)
            throw new WebSocketException(WebSocketError.Faulted,
                socket.CloseStatusDescription ?? "The server did not welcome this daemon.");
        log.WriteLine($"Connected to {state.Server} as machine {welcome.MachineId} (server {welcome.ServerVersion}).");
        Connected?.Invoke(welcome);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var pinging = PingAsync(socket, stop.Token);
        try
        {
            while (await DaemonSocket.ReceiveAsync(socket, cancellation) is not null) { }
        }
        finally
        {
            await stop.CancelAsync();
            try { await pinging; }
            catch (Exception) { /* the receive loop reports the failure */ }
        }
    }

    static async Task PingAsync(WebSocket socket, CancellationToken cancellation)
    {
        for (long sequence = 1; ; sequence++)
        {
            await Task.Delay(PingInterval, cancellation);
            await DaemonSocket.SendAsync(socket, new PingMessage(sequence), cancellation);
        }
    }
}
