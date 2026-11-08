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

    public void Terminal(ulong machineId, string terminalId, LiveServerMessage message)
    {
        foreach (var subscriber in _subscribers.Keys) subscriber.Terminal(machineId, terminalId, message);
    }

    public sealed class Subscriber(ulong userId)
    {
        readonly ConcurrentDictionary<string, ulong> _terminals = new();
        readonly Channel<LiveServerMessage> _terminalMessages = Channel.CreateBounded<LiveServerMessage>(
            new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
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

        public void WatchTerminal(ulong machineId, string terminalId) => _terminals[terminalId] = machineId;

        public void UnwatchTerminal(string terminalId) => _terminals.TryRemove(terminalId, out _);

        /// <summary>The machine of a terminal this connection watches.</summary>
        public ulong? MachineOfTerminal(string terminalId) => _terminals.TryGetValue(terminalId, out var machineId) ? machineId : null;

        public void Terminal(ulong machineId, string terminalId, LiveServerMessage message)
        {
            if (_terminals.TryGetValue(terminalId, out var watched) && watched == machineId) Enqueue(message);
        }

        /// <summary>For this connection only: the answer to its own subscribe.</summary>
        public void Enqueue(LiveServerMessage message)
        {
            _terminalMessages.Writer.TryWrite(message);
            Wake();
        }

        public bool TryTakeTerminalMessage(out LiveServerMessage message) => _terminalMessages.Reader.TryRead(out message!);

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
