using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BoSoFa.Daemon.Harnesses;
using BoSoFa.Daemon.Harnesses.Acp;
using BoSoFa.Daemon.Harnesses.Claude;
using BoSoFa.Daemon.Harnesses.Codex;
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

    /// <summary>The harness account whose secret is leased for each start of the harness; null: the machine's own sign-in.</summary>
    public ulong? AccountId { get; set; }

    /// <summary>Runs started so far, numbering the checkpoints.</summary>
    public int Runs { get; set; }

    /// <summary>The session has a profile; its MCP servers and skills are fetched at each launch.</summary>
    public bool HasProfile { get; set; }
}

/// <summary>Fetches a harness account's secret for a session, just in time (plan §5.4.1).</summary>
public interface IAccountLeaser
{
    Task<AccountLeaseResponse> LeaseAsync(ulong accountId, ulong sessionId, CancellationToken cancellation);
}

public sealed record HarnessOptions(string? ClaudeExecutable = null, string? ReplayFixture = null,
    IAccountLeaser? Accounts = null, string? CodexExecutable = null, IProfileSource? Profiles = null,
    IBlobUploader? Uploader = null, string[]? Roots = null);

/// <summary>
/// Applies the server's commands to the sessions of this machine. Commands are idempotent per ID (the journal), and
/// session state survives restarts; a turn cut short by a restart is reported as interrupted, and the next message
/// resumes the conversation.
/// </summary>
public sealed class SessionHost : ISessionSink, IAsyncDisposable
{
    readonly string _directory;
    readonly string _dataDirectory;
    readonly SessionProfiles _profiles;
    readonly Outbox _outbox;
    readonly HarnessOptions _options;
    readonly TextWriter _log;
    readonly CommandJournal _journal;
    readonly Dictionary<ulong, IHarnessSession> _sessions = new();
    readonly object _stateLock = new();
    readonly ConcurrentDictionary<ulong, string?> _runs = new();

    /// <summary>Set by the link while it is connected; deltas are dropped otherwise.</summary>
    public Action<DeltaMessage>? DeltaSink { get; set; }

    public SessionHost(string dataDirectory, Outbox outbox, HarnessOptions options, TextWriter log)
    {
        // Absolute: harnesses run in the session's working directory, so a relative profile path would point nowhere.
        _dataDirectory = Path.GetFullPath(dataDirectory);
        Installer = new HarnessInstaller(_dataDirectory);
        _profiles = new SessionProfiles(_dataDirectory, options.Profiles);
        Mcp = new McpEndpoint(id =>
        {
            lock (_sessions) return _sessions.GetValueOrDefault(id)?.State;
        }, this, options.Uploader);
        Files = new MachineFiles(FileRoots, options.Uploader, options.Profiles);
        _configuredRoots = options.Roots ?? [];
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

    /// <summary>The harness implementation of a session; Claude Code and its replay share the Claude adapter.</summary>
    IHarnessSession Create(SessionState state) => state.Harness switch
    {
        HarnessNames.Codex => new CodexSession(state, this, () => LaunchCodexAsync(state), _log),
        HarnessNames.OpenCode => new AcpSession(state, this, () => LaunchAcp(state, "opencode", ["acp"]), _log),
        HarnessNames.Gemini => new AcpSession(state, this, () => LaunchAcp(state, "gemini", ["--experimental-acp"]), _log),
        _ => new ClaudeSession(state, this, Launch(state), _log)
    };

    /// <summary>An ACP agent uses the machine's own sign-in of that tool (accounts for these come with the catalog).</summary>
    async Task<AcpLaunch> LaunchAcp(SessionState state, string name, string[] arguments)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var executable = HarnessEnvironment.FindExecutable(Installer.Find(name), OperatingSystem.IsWindows() ? [name + ".exe"] : [name],
                             Path.Combine(home, $".{name}", "bin"), "/opt/homebrew/bin", "/usr/local/bin") ??
                         throw new IOException($"The {name} executable was not found on this machine.");
        var profile = await _profiles.PrepareAsync(state);
        return new(JsonRpcProcess.Start(executable, arguments, state.WorkingDirectory, HarnessEnvironment.Clean(), includeVersion: true),
            McpConfig.ForAcp(ServersOf(state, profile)));
    }

    /// <summary>A profile folder per account (or one for the machine's own sign-in), never the user's ~/.codex. Tokens
    /// are kept in a file there, so a background service does not depend on the keychain.</summary>
    async Task<CodexLaunch> LaunchCodexAsync(SessionState state)
    {
        var executable = HarnessEnvironment.FindExecutable(_options.CodexExecutable ?? Installer.Find(HarnessNames.Codex),
                             OperatingSystem.IsWindows() ? ["codex.exe"] : ["codex"], "/opt/homebrew/bin", "/usr/local/bin") ??
                         throw new FileNotFoundException("The codex executable was not found on this machine.");
        string? apiKey = null;
        if (state.AccountId is { } accountId)
        {
            if (_options.Accounts is not { } accounts) throw new InvalidOperationException("This daemon cannot lease harness accounts.");
            apiKey = (await accounts.LeaseAsync(accountId, state.SessionId, CancellationToken.None)).Secret;
        }
        var home = Path.Combine(_dataDirectory, "profiles", state.AccountId is { } id ? $"account-{id}" : "machine", "codex");
        Directory.CreateDirectory(home);
        await File.WriteAllTextAsync(Path.Combine(home, "config.toml"), CodexConfig);
        var environment = HarnessEnvironment.Clean();
        environment["CODEX_HOME"] = home;
        var profile = await _profiles.PrepareAsync(state);
        if (profile?.PluginDirectory is { } plugin) SessionProfiles.CopyDirectory(Path.Combine(plugin, "skills"), Path.Combine(home, "skills"));
        return new(JsonRpcProcess.Start(executable, ["app-server"], state.WorkingDirectory,
            environment, includeVersion: false), apiKey, McpConfig.ForCodex(ServersOf(state, profile)));
    }

    const string CodexConfig = """
        cli_auth_credentials_store = "file"
        mcp_oauth_credentials_store = "file"
        check_for_update_on_startup = false
        analytics = { enabled = false }
        feedback = { enabled = false }

        """;

    Func<ClaudeLaunch, Task<IClaudeTransport>> Launch(SessionState state) => state.Harness switch
    {
        HarnessNames.Replay when _options.ReplayFixture != null => async launch =>
        {
            await PrepareClaudeAsync(state, launch); // leased like Claude Code, so tests cover the lease
            return new ReplayTransport(_options.ReplayFixture);
        },
        HarnessNames.Claude => async launch =>
        {
            var executable = ClaudeProcess.FindExecutable(_options.ClaudeExecutable ?? Installer.Find(HarnessNames.Claude)) ??
                             throw new FileNotFoundException("The claude executable was not found on this machine.");
            return ClaudeProcess.Start(executable, await PrepareClaudeAsync(state, launch));
        },
        _ => _ => throw new InvalidOperationException($"This daemon cannot run the {state.Harness} harness.")
    };

    /// <summary>With an account: its secret only in the child's environment, and a config folder of its own, so
    /// neither the machine's own login nor another account's state mixes in.</summary>
    async Task<ClaudeLaunch> PrepareClaudeAsync(SessionState state, ClaudeLaunch launch)
    {
        var profile = await _profiles.PrepareAsync(state);
        launch = launch with
        {
            McpConfigJson = McpConfig.ForClaude(ServersOf(state, profile)), PluginDirectory = profile?.PluginDirectory,
            AllowedTools = McpEndpoint.PreApprovedClaudeTools
        };
        if (state.AccountId is not { } accountId) return launch;
        if (_options.Accounts is not { } accounts)
            throw new InvalidOperationException("This daemon cannot lease harness accounts.");
        var lease = await accounts.LeaseAsync(accountId, state.SessionId, CancellationToken.None);
        var configDirectory = Path.Combine(_dataDirectory, "profiles", $"account-{accountId}", "claude");
        Directory.CreateDirectory(configDirectory);
        var environment = new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = configDirectory };
        environment[lease.Kind == AccountKind.ClaudeOAuthToken ? "CLAUDE_CODE_OAUTH_TOKEN" : "ANTHROPIC_API_KEY"] = lease.Secret;
        return launch with { Environment = environment };
    }

    readonly string[] _configuredRoots;

    /// <summary>The file browser and transfers of this machine.</summary>
    public MachineFiles Files { get; }

    /// <summary>What people may browse: configured folders and the working directories of this machine's sessions.</summary>
    string[] FileRoots()
    {
        string[] working;
        lock (_sessions) working = _sessions.Values.Select(session => session.State.WorkingDirectory).ToArray();
        return _configuredRoots.Concat(working).Select(Path.GetFullPath).Select(Path.TrimEndingDirectorySeparator).Distinct()
            .Where(Directory.Exists).ToArray();
    }

    /// <summary>The BoSoFa MCP server every session of this machine gets.</summary>
    public McpEndpoint Mcp { get; }

    ResolvedMcpServer[] ServersOf(SessionState state, PreparedProfile? profile) =>
        [Mcp.ServerFor(state.SessionId), ..profile?.McpServers ?? []];

    /// <summary>Installed harnesses and failed installs, for the server's inventory.</summary>
    public HarnessInstaller Installer { get; }

    /// <summary>Applies one command. True when it was new (the caller acknowledges both cases).</summary>
    public async Task<bool> ApplyAsync(ulong commandId, MachineCommand command)
    {
        if (_journal.Contains(commandId)) return false;
        if (command is InstallHarnessCommand install)
        {
            // In the background: a download can take minutes, and other commands must not wait for it.
            _ = Task.Run(() => Installer.InstallAsync(install, CancellationToken.None));
            _journal.Add(commandId);
            return true;
        }
        IHarnessSession? session;
        lock (_sessions) session = _sessions.GetValueOrDefault(command.SessionId);
        switch (command)
        {
            case StartSessionCommand start:
                if (session != null) break; // started before; the journal was lost
                var state = new SessionState
                {
                    SessionId = start.SessionId, Harness = start.Harness, WorkingDirectory = start.WorkingDirectory,
                    PermissionMode = start.PermissionMode, Model = start.Model, Status = SessionStatus.Starting,
                    AccountId = start.AccountId, HasProfile = start.HasProfile
                };
                if (start.UseWorktree && !PrepareWorktree(state, start.Prompt)) break;
                Save(state);
                session = Create(state);
                lock (_sessions) _sessions[state.SessionId] = session;
                _log.WriteLine($"Session {state.SessionId}: starting {state.Harness} in {state.WorkingDirectory}");
                await session.SendAsync(start.Prompt, DispatchMode.Queue);
                break;
            case SendMessageCommand send:
                if (session == null) Report(send.SessionId, new StatusReport(SessionStatus.Failed, Error: "Unknown session."));
                else await session.SendAsync(send.Text, send.Mode);
                break;
            case InterruptCommand:
                if (session != null) await session.InterruptAsync();
                break;
            case AnswerApprovalCommand answer:
                if (Mcp.TryAnswer(answer.Key, answer.Allow, answer.Message)) break;
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
        // Before the status goes out, so the agent cannot have changed anything yet.
        if (report is StatusReport { Status: SessionStatus.Running } && state != null) BeginRun(state);
        Report(sessionId, report, state);
        if (report is RunEndedReport && state != null)
        {
            Mcp.CancelQuestions(sessionId);
            EndRun(state);
        }
    }

    /// <summary>Moves a new session into its own worktree. False when that is impossible (reported as a failure).</summary>
    bool PrepareWorktree(SessionState state, string prompt)
    {
        var repository = new GitWorkspace(state.WorkingDirectory);
        var path = Path.Combine(_dataDirectory, "worktrees", $"session-{state.SessionId}");
        var branch = GitWorkspace.BranchName(state.SessionId, prompt);
        var error = !Directory.Exists(state.WorkingDirectory)
            ? $"The working directory {state.WorkingDirectory} does not exist on this machine."
            : !repository.IsRepository
                ? $"{state.WorkingDirectory} is not a git repository, so the session cannot run in a worktree."
                : repository.AddWorktree(path, branch);
        if (error != null)
        {
            _outbox.Append(state.SessionId, new StatusReport(SessionStatus.Failed, Error: error));
            return false;
        }
        state.WorkingDirectory = path;
        _outbox.Append(state.SessionId, new WorkspaceReport(path, branch));
        return true;
    }

    /// <summary>Takes the "before" checkpoint of a run; repeated running statuses within a run are ignored.</summary>
    void BeginRun(SessionState state)
    {
        if (_runs.ContainsKey(state.SessionId)) return;
        lock (_stateLock)
        {
            state.Runs++;
            Save(state);
        }
        var workspace = new GitWorkspace(state.WorkingDirectory);
        // Marks the run as begun even outside a repository, so later statuses of this run skip the work.
        _runs[state.SessionId] = workspace.IsRepository
            ? workspace.Checkpoint($"refs/bosofa/{state.SessionId}/{state.Runs}-before")
            : null;
    }

    /// <summary>Takes the "after" checkpoint and reports what the run changed.</summary>
    void EndRun(SessionState state)
    {
        if (!_runs.TryRemove(state.SessionId, out var before) || before == null) return;
        var workspace = new GitWorkspace(state.WorkingDirectory);
        if (workspace.Checkpoint($"refs/bosofa/{state.SessionId}/{state.Runs}-after") is not { } after ||
            workspace.Diff(before, after) is not { } diff)
            return;
        _outbox.Append(state.SessionId, new ItemReport(SessionItemKind.Diff, DateTime.UtcNow, diff.Patch,
            Data: new JsonObject { ["files"] = diff.Files, ["added"] = diff.Added, ["removed"] = diff.Removed }.ToJsonString()));
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
        await Mcp.DisposeAsync();
        IHarnessSession[] sessions;
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
