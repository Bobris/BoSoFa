using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Machines;

/// <summary>The server's end of a long-polling daemon: looks like a socket to <see cref="DaemonPump"/>.</summary>
public sealed class LongPollChannel(ulong machineId, TimeProvider time) : IDaemonChannel
{
    readonly Channel<DaemonMessage> _incoming = Channel.CreateUnbounded<DaemonMessage>(new() { SingleReader = true });
    readonly object _lock = new();
    readonly Queue<(long Sequence, DaemonMessage Message)> _outgoing = new();
    readonly CancellationTokenSource _aborted = new();
    TaskCompletionSource _more = new(TaskCreationOptions.RunContinuationsAsynchronously);
    long _lastSequence;
    long _lastSeenTicks = time.GetUtcNow().UtcTicks;
    bool _closed;

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public ulong MachineId { get; } = machineId;
    public string? CloseReason { get; private set; }
    public bool IsClosed { get { lock (_lock) return _closed; } }

    /// <summary>Nothing was heard for a while: the daemon is gone.</summary>
    public bool IsIdle(TimeSpan limit) => time.GetUtcNow().UtcTicks - Interlocked.Read(ref _lastSeenTicks) > limit.Ticks;

    void Seen() => Interlocked.Exchange(ref _lastSeenTicks, time.GetUtcNow().UtcTicks);

    public async Task<DaemonMessage?> ReceiveAsync(CancellationToken cancellation)
    {
        using var both = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _aborted.Token);
        try { return await _incoming.Reader.ReadAsync(both.Token); }
        catch (ChannelClosedException) { return null; }
        catch (OperationCanceledException) when (_aborted.IsCancellationRequested && !cancellation.IsCancellationRequested)
        {
            throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "The daemon went away.");
        }
    }

    public Task SendAsync(DaemonMessage message, CancellationToken cancellation)
    {
        lock (_lock)
        {
            if (_closed) throw new WebSocketException(WebSocketError.InvalidState, "The channel is closed.");
            _outgoing.Enqueue((++_lastSequence, message));
            _more.TrySetResult();
        }
        return Task.CompletedTask;
    }

    public Task CloseAsync(WebSocketCloseStatus status, string reason, CancellationToken cancellation)
    {
        Close(reason);
        return Task.CompletedTask;
    }

    public void Abort()
    {
        Close(null);
        _aborted.Cancel();
    }

    void Close(string? reason)
    {
        lock (_lock)
        {
            _closed = true;
            CloseReason ??= reason;
            _more.TrySetResult();
        }
    }

    /// <summary>Messages the daemon sent.</summary>
    public void Accept(DaemonMessage[] messages)
    {
        Seen();
        foreach (var message in messages) _incoming.Writer.TryWrite(message);
    }

    /// <summary>Everything after <paramref name="after"/>, waiting for the first message; null once closed and drained.</summary>
    public async Task<PollBatch?> PollAsync(long after, TimeSpan wait, CancellationToken cancellation)
    {
        Seen();
        var deadline = Task.Delay(wait, cancellation);
        while (true)
        {
            Task more;
            lock (_lock)
            {
                while (_outgoing.TryPeek(out var head) && head.Sequence <= after) _outgoing.Dequeue();
                if (_outgoing.Count > 0) return new(_outgoing.Last().Sequence, _outgoing.Select(entry => entry.Message).ToArray());
                if (_closed) return null;
                if (_more.Task.IsCompleted) _more = new(TaskCreationOptions.RunContinuationsAsynchronously);
                more = _more.Task;
            }
            if (await Task.WhenAny(more, deadline) == deadline)
            {
                cancellation.ThrowIfCancellationRequested();
                Seen();
                return new(after, []);
            }
        }
    }
}

/// <summary>The long-polling channels held by this node.</summary>
public sealed class LongPollChannels
{
    readonly ConcurrentDictionary<string, LongPollChannel> _channels = new();

    public void Add(LongPollChannel channel) => _channels[channel.Id] = channel;

    public void Remove(LongPollChannel channel) => _channels.TryRemove(channel.Id, out _);

    public LongPollChannel? Get(string id) => _channels.GetValueOrDefault(id);

    public IEnumerable<LongPollChannel> All => _channels.Values;
}
