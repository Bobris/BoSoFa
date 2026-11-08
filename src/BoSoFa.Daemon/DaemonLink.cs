using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using BoSoFa.Daemon.Sessions;
using BoSoFa.Protocol;

namespace BoSoFa.Daemon;

/// <summary>Keeps one WebSocket to the server: token, hello, keepalive, and reconnecting with backoff while the server
/// is away. Over it go the server's commands (applied by the <see cref="SessionHost"/>, then acknowledged), the
/// outbox (sent in order, forgotten once acknowledged, sent again after a reconnect) and live deltas.</summary>
public sealed class DaemonLink(DaemonState state, ECDsa key, TextWriter log, Outbox? outbox = null, SessionHost? host = null)
{
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(15);
    static readonly TimeSpan MaximumBackoff = TimeSpan.FromSeconds(30);
    const int BatchCount = 64;
    const int BatchBytes = 256 * 1024;

    bool _welcomed;

    /// <summary>Set after each welcome; for tests and status output.</summary>
    public event Action<WelcomeMessage>? Connected;

    public async Task RunAsync(CancellationToken cancellation)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!cancellation.IsCancellationRequested)
        {
            _welcomed = false;
            try
            {
                await ConnectOnceAsync(cancellation);
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
                log.WriteLine($"Server unreachable ({exception.Message}); retrying in {(_welcomed ? 1 : backoff.TotalSeconds):0} s.");
            }
            if (_welcomed) backoff = TimeSpan.FromSeconds(1); // a working connection dropped: retry soon
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
        _welcomed = true;
        log.WriteLine($"Connected to {state.Server} as machine {welcome.MachineId} (server {welcome.ServerVersion}).");
        await using var connection = new Connection(socket, outbox, host, log, cancellation);
        Connected?.Invoke(welcome);
        while (await DaemonSocket.ReceiveAsync(socket, cancellation) is { } message)
            switch (message)
            {
                case CommandMessage command:
                    connection.Commands.Writer.TryWrite(command);
                    break;
                case OutboxAckMessage ack:
                    outbox?.Ack(ack.Acks);
                    break;
                case RpcRequestMessage call:
                    connection.Serve(call);
                    break;
            }
    }

    /// <summary>The workers of one connection. All sends go through one lock: WebSocket sends must not overlap.</summary>
    sealed class Connection : IAsyncDisposable
    {
        readonly WebSocket _socket;
        readonly Outbox? _outbox;
        readonly SessionHost? _host;
        readonly TextWriter _log;
        readonly SemaphoreSlim _send = new(1);
        readonly SemaphoreSlim _wake = new(0);
        readonly Channel<DeltaMessage> _deltas = Channel.CreateBounded<DeltaMessage>(
            new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest });
        readonly CancellationTokenSource _stop;
        readonly Task[] _workers;

        public Channel<CommandMessage> Commands { get; } = Channel.CreateUnbounded<CommandMessage>();

        public Connection(WebSocket socket, Outbox? outbox, SessionHost? host, TextWriter log, CancellationToken cancellation)
        {
            _socket = socket;
            _outbox = outbox;
            _host = host;
            _log = log;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            if (outbox != null) outbox.Appended += Wake;
            if (host != null) host.Installer.Changed += SendInventory;
            if (host != null) host.Terminals.Sink = message => SendAsync(message, _stop.Token);
            if (host != null)
                host.DeltaSink = delta =>
                {
                    _deltas.Writer.TryWrite(delta);
                    Wake();
                };
            _workers = [PingAsync(_stop.Token), SendOutboxAsync(_stop.Token), ApplyCommandsAsync(_stop.Token)];
            SendInventory();
        }

        /// <summary>Terminal calls take effect here, in arrival order. The rest runs on its own task: a large transfer
        /// must not hold up what comes behind it.</summary>
        public void Serve(RpcRequestMessage call)
        {
            var terminal = call.Request is TerminalRpc request && _host != null ? _host.Terminals.Handle(request) : null;
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = terminal ?? (_host != null
                        ? await _host.Files.HandleAsync(call.Request, _stop.Token)
                        : new RpcFailure("This daemon cannot do that."));
                    await SendAsync(new RpcResponseMessage(call.Id, result), _stop.Token);
                }
                catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or
                                                      ObjectDisposedException or IOException) { }
            });
        }

        /// <summary>The server's picture of this machine's harnesses: after connecting and after every install.</summary>
        void SendInventory()
        {
            if (_host == null) return;
            _ = Task.Run(async () =>
            {
                try { await SendAsync(new InventoryMessage(_host.Installer.Inventory()), _stop.Token); }
                catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or
                                                      ObjectDisposedException or IOException) { }
            });
        }

        void Wake()
        {
            if (_wake.CurrentCount == 0) _wake.Release();
        }

        async Task SendAsync(DaemonMessage message, CancellationToken cancellation)
        {
            await _send.WaitAsync(cancellation);
            try { await DaemonSocket.SendAsync(_socket, message, cancellation); }
            finally { _send.Release(); }
        }

        async Task PingAsync(CancellationToken cancellation)
        {
            for (long sequence = 1; ; sequence++)
            {
                await Task.Delay(PingInterval, cancellation);
                await SendAsync(new PingMessage(sequence), cancellation);
            }
        }

        /// <summary>Everything unacknowledged first, then each new record once, in order; deltas in between.</summary>
        async Task SendOutboxAsync(CancellationToken cancellation)
        {
            long sent = 0;
            while (true)
            {
                while (_deltas.Reader.TryRead(out var delta)) await SendAsync(delta, cancellation);
                if (_outbox?.After(sent, BatchCount, BatchBytes) is { Count: > 0 } batch)
                {
                    await SendAsync(new OutboxMessage(batch.Select(entry => entry.Record).ToArray()), cancellation);
                    sent = batch[^1].Position;
                    continue;
                }
                await _wake.WaitAsync(TimeSpan.FromSeconds(30), cancellation);
            }
        }

        async Task ApplyCommandsAsync(CancellationToken cancellation)
        {
            await foreach (var command in Commands.Reader.ReadAllAsync(cancellation))
            {
                if (_host == null) continue; // a link without sessions (tests) leaves commands in the inbox
                try { await _host.ApplyAsync(command.Id, command.Command); }
                catch (Exception exception) when (exception is IOException or InvalidOperationException or JsonException)
                {
                    _log.WriteLine($"Command {command.Id} failed: {exception.Message}");
                }
                await SendAsync(new CommandAckMessage([command.Id]), cancellation);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_outbox != null) _outbox.Appended -= Wake;
            if (_host != null) _host.Installer.Changed -= SendInventory;
            if (_host != null) _host.Terminals.Sink = null;
            if (_host != null) _host.DeltaSink = null;
            await _stop.CancelAsync();
            foreach (var worker in _workers)
                try { await worker; }
                catch (Exception) { /* the receive loop reports the failure */ }
            _stop.Dispose();
        }
    }
}
