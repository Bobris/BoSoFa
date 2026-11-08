using System.Collections.Concurrent;
using System.Threading.Channels;
using BoSoFa.Protocol;
using BoSoFa.Server.Domain;
using BoSoFa.Server.Machines;

namespace BoSoFa.Server.Live;

/// <summary>Pod-local fan-out to the UI's live connections. Persisted changes only mark a session dirty: each
/// subscriber then reads what it is missing after its own cursor, so a lost signal costs latency, never data.
/// Deltas are ephemeral and dropped for slow subscribers.</summary>
public sealed class LiveHub
{
    readonly ConcurrentDictionary<Subscriber, byte> _subscribers = new();

    public Subscriber Join(ulong userId)
    {
        var subscriber = new Subscriber(userId);
        _subscribers[subscriber] = 0;
        return subscriber;
    }

    public void Leave(Subscriber subscriber) => _subscribers.TryRemove(subscriber, out _);

    public void SessionChanged(ulong sessionId)
    {
        foreach (var subscriber in _subscribers.Keys) subscriber.MarkDirty(sessionId);
    }

    public void Delta(LiveDelta delta)
    {
        foreach (var subscriber in _subscribers.Keys) subscriber.Delta(delta);
    }

    public sealed class Subscriber(ulong userId)
    {
        readonly ConcurrentDictionary<ulong, long> _cursors = new();
        readonly ConcurrentDictionary<ulong, byte> _dirty = new();
        readonly Channel<LiveDelta> _deltas = Channel.CreateBounded<LiveDelta>(
            new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        readonly SemaphoreSlim _wake = new(0);

        public ulong UserId { get; } = userId;

        public void Subscribe(ulong sessionId, long afterSeq)
        {
            _cursors[sessionId] = afterSeq;
            MarkDirty(sessionId);
        }

        public void Unsubscribe(ulong sessionId) => _cursors.TryRemove(sessionId, out _);

        public void MarkDirty(ulong sessionId)
        {
            if (!_cursors.ContainsKey(sessionId)) return;
            _dirty[sessionId] = 0;
            Wake();
        }

        public void Delta(LiveDelta delta)
        {
            if (!_cursors.ContainsKey(delta.SessionId)) return;
            _deltas.Writer.TryWrite(delta);
            Wake();
        }

        void Wake()
        {
            if (_wake.CurrentCount == 0) _wake.Release();
        }

        public async Task WaitAsync(CancellationToken cancellation)
        {
            await _wake.WaitAsync(cancellation);
            while (_wake.Wait(0)) { }
        }

        public IEnumerable<(ulong SessionId, long AfterSeq)> TakeDirty()
        {
            foreach (var sessionId in _dirty.Keys)
                if (_dirty.TryRemove(sessionId, out _) && _cursors.TryGetValue(sessionId, out var cursor))
                    yield return (sessionId, cursor);
        }

        public void Advance(ulong sessionId, long seq, bool more)
        {
            if (_cursors.TryGetValue(sessionId, out var cursor) && seq > cursor) _cursors[sessionId] = seq;
            if (more) MarkDirty(sessionId);
        }

        public bool TryTakeDelta(out LiveDelta delta)
        {
            while (_deltas.Reader.TryRead(out delta!))
                if (_cursors.ContainsKey(delta.SessionId)) return true;
            return false;
        }
    }
}

/// <summary>Domain post-commit reactions of this node.</summary>
public sealed class DomainSignals(DaemonConnections connections, LiveHub hub, LiveRelay relay) : IDomainSignals
{
    public void MachineCommandsIssued(ulong machineId) => connections.Get(machineId)?.WakeCommands();

    public void SessionChanged(ulong sessionId) => hub.SessionChanged(sessionId);

    public void MachinePresenceChanged(ulong machineId, string? connectionId)
    {
        connections.Supersede(machineId, connectionId);
        relay.PresenceChanged(machineId);
    }
}
