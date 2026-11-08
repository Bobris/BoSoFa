using System.Text.Json;
using System.Text.Json.Serialization;
using BoSoFa.Daemon.Harnesses.Claude;
using BoSoFa.Protocol;
using HarnessNames = BoSoFa.Protocol.Harnesses;

namespace BoSoFa.Daemon.Sessions;

/// <summary>Where a session's harness output goes: durable reports to the outbox, deltas to the live link.</summary>
public interface ISessionSink
{
    void Report(ulong sessionId, SessionReport report);
    void Delta(ulong sessionId, string itemRef, string text);
}

/// <summary>What the daemon remembers about a session across restarts (sessions/&lt;id&gt;.json).</summary>
public sealed class SessionState
{
    public ulong SessionId { get; set; }
    public string Harness { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string PermissionMode { get; set; } = "default";
    public string? Model { get; set; }
    public string? HarnessSessionId { get; set; }
    public SessionStatus Status { get; set; }
}

public sealed record HarnessOptions(string? ClaudeExecutable = null, string? ReplayFixture = null);

/// <summary>
/// Applies the server's commands to the sessions of this machine. Commands are idempotent per ID (the journal), and
/// session state survives restarts; a turn cut short by a restart is reported as interrupted, and the next message
/// resumes the conversation.
/// </summary>
public sealed class SessionHost : ISessionSink, IAsyncDisposable
{
    readonly string _directory;
    readonly Outbox _outbox;
    readonly HarnessOptions _options;
    readonly TextWriter _log;
    readonly CommandJournal _journal;
    readonly Dictionary<ulong, ClaudeSession> _sessions = new();
    readonly object _stateLock = new();

    /// <summary>Set by the link while it is connected; deltas are dropped otherwise.</summary>
    public Action<DeltaMessage>? DeltaSink { get; set; }

    public SessionHost(string dataDirectory, Outbox outbox, HarnessOptions options, TextWriter log)
    {
        _directory = Path.Combine(dataDirectory, "sessions");
        Directory.CreateDirectory(_directory);
        _outbox = outbox;
        _options = options;
        _log = log;
        _journal = new CommandJournal(Path.Combine(dataDirectory, "commands.json"));
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            var state = JsonSerializer.Deserialize(File.ReadAllText(path), SessionJsonContext.Default.SessionState);
            if (state == null) continue;
            if (state.Status is SessionStatus.Starting or SessionStatus.Running or SessionStatus.WaitingForApproval)
                Report(state.SessionId, new StatusReport(SessionStatus.Idle,
                    Error: "The daemon restarted, so the running turn ended. Send a message to continue."), state);
            _sessions[state.SessionId] = Create(state);
        }
    }

    public IReadOnlyCollection<ulong> Sessions
    {
        get { lock (_sessions) return _sessions.Keys.ToArray(); }
    }

    ClaudeSession Create(SessionState state) => new(state, this, Launch(state), _log);

    Func<ClaudeLaunch, IClaudeTransport> Launch(SessionState state) => state.Harness switch
    {
        HarnessNames.Replay when _options.ReplayFixture != null => _ => new ReplayTransport(_options.ReplayFixture),
        HarnessNames.Claude => launch => ClaudeProcess.Start(ClaudeProcess.FindExecutable(_options.ClaudeExecutable) ??
            throw new FileNotFoundException("The claude executable was not found on this machine."), launch),
        _ => _ => throw new InvalidOperationException($"This daemon cannot run the {state.Harness} harness.")
    };

    /// <summary>Applies one command. True when it was new (the caller acknowledges both cases).</summary>
    public async Task<bool> ApplyAsync(ulong commandId, MachineCommand command)
    {
        if (_journal.Contains(commandId)) return false;
        ClaudeSession? session;
        lock (_sessions) session = _sessions.GetValueOrDefault(command.SessionId);
        switch (command)
        {
            case StartSessionCommand start:
                if (session != null) break; // started before; the journal was lost
                var state = new SessionState
                {
                    SessionId = start.SessionId, Harness = start.Harness, WorkingDirectory = start.WorkingDirectory,
                    PermissionMode = start.PermissionMode, Model = start.Model, Status = SessionStatus.Starting
                };
                Save(state);
                session = Create(state);
                lock (_sessions) _sessions[state.SessionId] = session;
                _log.WriteLine($"Session {state.SessionId}: starting {state.Harness} in {state.WorkingDirectory}");
                await session.SendAsync(start.Prompt);
                break;
            case SendMessageCommand send:
                if (session == null) Report(send.SessionId, new StatusReport(SessionStatus.Failed, Error: "Unknown session."));
                else await session.SendAsync(send.Text);
                break;
            case InterruptCommand:
                if (session != null) await session.InterruptAsync();
                break;
            case AnswerApprovalCommand answer:
                if (session != null) await session.AnswerAsync(answer.Key, answer.Allow, answer.Message);
                break;
            case StopSessionCommand:
                if (session != null) await session.StopAsync();
                break;
        }
        _journal.Add(commandId);
        return true;
    }

    public void Report(ulong sessionId, SessionReport report)
    {
        SessionState? state;
        lock (_sessions) state = _sessions.GetValueOrDefault(sessionId)?.State;
        Report(sessionId, report, state);
    }

    void Report(ulong sessionId, SessionReport report, SessionState? state)
    {
        if (report is StatusReport status && state != null)
            lock (_stateLock)
            {
                state.Status = status.Status;
                if (status.HarnessSessionId != null) state.HarnessSessionId = status.HarnessSessionId;
                Save(state);
            }
        _outbox.Append(sessionId, report);
    }

    public void Delta(ulong sessionId, string itemRef, string text) =>
        DeltaSink?.Invoke(new DeltaMessage(sessionId, itemRef, text));

    void Save(SessionState state)
    {
        var path = Path.Combine(_directory, $"{state.SessionId}.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(state, SessionJsonContext.Default.SessionState));
        File.Move(path + ".tmp", path, true);
    }

    public async ValueTask DisposeAsync()
    {
        ClaudeSession[] sessions;
        lock (_sessions) sessions = _sessions.Values.ToArray();
        await Task.WhenAll(sessions.Select(session => session.DisposeAsync().AsTask()));
    }
}

/// <summary>IDs of applied commands, kept for 7 days (plan §5.3) so a command resent after a lost ack is skipped.</summary>
sealed class CommandJournal(string path)
{
    static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    readonly Dictionary<ulong, DateTime> _applied = File.Exists(path)
        ? JsonSerializer.Deserialize(File.ReadAllText(path), OutboxJsonContext.Default.DictionaryUInt64DateTime) ?? []
        : [];

    public bool Contains(ulong id)
    {
        lock (_applied) return _applied.ContainsKey(id);
    }

    public void Add(ulong id)
    {
        lock (_applied)
        {
            var now = DateTime.UtcNow;
            _applied[id] = now;
            foreach (var old in _applied.Where(entry => now - entry.Value > Retention).Select(entry => entry.Key).ToList())
                _applied.Remove(old);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(_applied, OutboxJsonContext.Default.DictionaryUInt64DateTime));
            File.Move(path + ".tmp", path, true);
        }
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(SessionState))]
internal sealed partial class SessionJsonContext : JsonSerializerContext;
