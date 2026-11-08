using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using BoSoFa.Protocol;
using BoSoFa.Server.Btdb;
using BoSoFa.Server.Domain;
using BTDB.Replication.EventSourcing;

namespace BoSoFa.Server.Live;

/// <summary>
/// Ephemeral deltas between nodes (plan §4.2). Only the node holding a daemon's WebSocket receives its deltas. Another
/// node whose live clients watch one of that machine's sessions opens one streaming request to the owner,
/// <c>GET /internal/live/machines/{id}</c> on the peer port, and fans what arrives out to its own subscribers. Deltas
/// lost while switching owners are not recovered; the final items come through the event log anyway.
/// </summary>
public sealed class LiveRelay(LiveHub hub, BtdbNode node, ServerOptions options, ILogger<LiveRelay> logger)
    : IAsyncDisposable
{
    public const string PathPrefix = "/internal/live/machines/";
    static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1) })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    readonly LiveHub _hub = hub;
    readonly BtdbNode _node = node;
    readonly ILogger _logger = logger;
    readonly ConcurrentDictionary<ulong, ConcurrentDictionary<Channel<LiveDelta>, byte>> _outgoing = new();
    readonly ConcurrentDictionary<ulong, Follower> _following = new();
    readonly CancellationTokenSource _stopping = new();

    public string? PeerKey => options.Cluster?.PeerKey;
    string NodeId => options.Cluster?.NodeId ?? "single-node";

    /// <summary>Owner side: a delta of a daemon connected here, for every node that follows this machine.</summary>
    public void Publish(ulong machineId, LiveDelta delta)
    {
        if (_outgoing.TryGetValue(machineId, out var streams))
            foreach (var stream in streams.Keys) stream.Writer.TryWrite(delta);
    }

    /// <summary>Owner side: the internal endpoint. Streams this machine's deltas as JSON lines until the caller leaves.</summary>
    public async Task ServeAsync(HttpContext http, ulong machineId)
    {
        if (PeerKey is not { } key || !Authorized(http, key))
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        var channel = Channel.CreateBounded<LiveDelta>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true
        });
        var streams = _outgoing.GetOrAdd(machineId, _ => new());
        streams[channel] = 0;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, _stopping.Token);
        try
        {
            http.Response.ContentType = "application/x-ndjson";
            await http.Response.StartAsync(stop.Token);
            await foreach (var delta in channel.Reader.ReadAllAsync(stop.Token))
            {
                await http.Response.Body.WriteAsync(
                    JsonSerializer.SerializeToUtf8Bytes<LiveServerMessage>(delta, ProtocolJsonContext.Default.LiveServerMessage),
                    stop.Token);
                await http.Response.Body.WriteAsync("\n"u8.ToArray(), stop.Token);
                await http.Response.Body.FlushAsync(stop.Token);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException) { }
        finally { streams.TryRemove(channel, out _); }
    }

    public static bool Authorized(HttpContext http, string key) =>
        AuthenticationHeaderValue.TryParse(http.Request.Headers.Authorization, out var header) &&
        header.Scheme == "Bearer" && header.Parameter is { } presented &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(key));

    /// <summary>The peer URL of the node holding this machine's daemon, unless that is this node.</summary>
    public string? OwnerPeerUrl(ulong machineId)
    {
        if (_node.Publisher is not { } publisher) return null;
        using var query = publisher.StartQuery();
        return Ambient.Relation<IMachinePresenceTable>().FindByIdOrDefault(machineId) is { } presence &&
               presence.NodeId != NodeId && presence.PeerUrl.Length > 0
            ? presence.PeerUrl
            : null;
    }

    /// <summary>Following side: while the lease is held, this node receives the machine's deltas from whichever node
    /// holds its daemon (nothing to do when that is this node, or in single-node mode).</summary>
    public IDisposable Follow(ulong machineId)
    {
        if (options.Cluster == null) return NoLease.Instance;
        while (true)
        {
            var follower = _following.GetOrAdd(machineId, id => new Follower(this, id));
            if (follower.TryAcquire()) return new Lease(follower);
        }
    }

    /// <summary>The machine's presence changed: a follower reconnects to the new owner (or stops relaying).</summary>
    public void PresenceChanged(ulong machineId)
    {
        if (_following.TryGetValue(machineId, out var follower)) follower.Wake();
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        foreach (var follower in _following.Values) await follower.Done;
    }

    sealed class NoLease : IDisposable
    {
        public static readonly NoLease Instance = new();
        public void Dispose() { }
    }

    sealed class Lease(Follower follower) : IDisposable
    {
        int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) follower.Release();
        }
    }

    /// <summary>One relay loop per followed machine, alive while any lease is held.</summary>
    sealed class Follower
    {
        readonly LiveRelay _relay;
        readonly ulong _machineId;
        readonly object _lock = new();
        readonly CancellationTokenSource _stop;
        CancellationTokenSource _current = new();
        int _leases;
        bool _closed;

        public Follower(LiveRelay relay, ulong machineId)
        {
            _relay = relay;
            _machineId = machineId;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(relay._stopping.Token);
            Done = Task.Run(RunAsync);
        }

        public Task Done { get; }

        public bool TryAcquire()
        {
            lock (_lock)
            {
                if (_closed) return false;
                _leases++;
                return true;
            }
        }

        public void Release()
        {
            lock (_lock)
            {
                if (--_leases > 0) return;
                _closed = true;
            }
            _relay._following.TryRemove(new KeyValuePair<ulong, Follower>(_machineId, this));
            _stop.Cancel();
        }

        /// <summary>Drops the current stream so the loop looks up the owner again.</summary>
        public void Wake()
        {
            lock (_lock) _current.Cancel();
        }

        async Task RunAsync()
        {
            var cancellation = _stop.Token;
            while (!cancellation.IsCancellationRequested)
            {
                CancellationTokenSource attempt;
                lock (_lock)
                {
                    if (_current.IsCancellationRequested)
                    {
                        _current.Dispose();
                        _current = new();
                    }
                    attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _current.Token);
                }
                using (attempt)
                    try
                    {
                        if (_relay.OwnerPeerUrl(_machineId) is { } owner) await RelayAsync(owner, attempt.Token);
                        await Task.Delay(TimeSpan.FromSeconds(1), attempt.Token); // retry, or wait for a presence change
                    }
                    catch (Exception exception) when (exception is OperationCanceledException or IOException or
                                                          HttpRequestException)
                    {
                        if (!cancellation.IsCancellationRequested && exception is not OperationCanceledException)
                            _relay._logger.LogDebug("Relay of machine {MachineId} failed: {Error}", _machineId,
                                exception.Message);
                    }
            }
        }

        async Task RelayAsync(string owner, CancellationToken cancellation)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, owner.TrimEnd('/') + PathPrefix + _machineId);
            request.Headers.Authorization = new("Bearer", _relay.PeerKey);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            response.EnsureSuccessStatusCode();
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellation));
            while (await reader.ReadLineAsync(cancellation) is { } line)
                if (line.Length > 0 &&
                    JsonSerializer.Deserialize(line, ProtocolJsonContext.Default.LiveServerMessage) is LiveDelta delta)
                    _relay._hub.Delta(delta);
        }
    }
}
