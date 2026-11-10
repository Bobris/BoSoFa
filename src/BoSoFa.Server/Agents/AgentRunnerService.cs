using System.Collections.Concurrent;
using BoSoFa.Protocol;
using BoSoFa.Server.Accounts;
using BoSoFa.Server.Btdb;
using BoSoFa.Server.Domain;
using BoSoFa.Server.Live;
using BoSoFa.Server.Machines;
using BoSoFa.Server.Tokens;
using BTDB.BackendActions;
using BTDB.Replication;
using BTDB.Replication.EventSourcing;
using Microsoft.AspNetCore.DataProtection;

namespace BoSoFa.Server.Agents;

/// <summary>What a loop needs from its node, and which attempt of the session's backend action it is.</summary>
sealed class AgentLoopContext(EventPublisher publisher, string nodeId, ulong actionId, ulong attempt, bool cleanStart, IBlobStore blobs, ILogger logger,
    Action<LiveDelta> publishDelta, Func<Session, CancellationToken, Task<(AgentEnvironment, List<string>)>> openEnvironment, Func<Session, AgentMachine?> machineOf)
{
    public EventPublisher Publisher => publisher;
    public string NodeId => nodeId;
    public ulong ActionId => actionId;
    public ulong Attempt => attempt;

    /// <summary>The previous loop stopped by itself, so no call it left open had started.</summary>
    public bool CleanStart => cleanStart;
    public IBlobStore Blobs => blobs;
    public ILogger Logger => logger;
    public void PublishDelta(LiveDelta delta) => publishDelta(delta);
    public AgentMachine? MachineOf(Session session) => machineOf(session);
    public Task<(AgentEnvironment Environment, List<string> Problems)> OpenEnvironmentAsync(Session session, CancellationToken cancellation) =>
        openEnvironment(session, cancellation);
}

/// <summary>
/// Runs agent session loops (docs/server-agent.md §4.2). Each runs as an attempt of the session's <see cref="AgentRun"/> backend
/// action, which the leader starts on some node and starts again elsewhere when that node goes. The event log is the bus: a
/// message handled by any node wakes the loop through <see cref="Wake"/> on every node.
/// </summary>
public sealed class AgentRunnerService(BtdbNode node, ServerOptions options, MachinePresenceService presence, ReplicationLeadership leadership,
    LiveHub hub, LiveRelay relay, MachineRpcClient rpc, IDataProtectionProvider protection, IBlobStore blobs, TimeProvider time, ILogger<AgentRunnerService> logger)
    : BackgroundService
{
    /// <summary>Anthropic's default cache lifetime; OpenAI and OpenCode do not promise one, so nothing is counted down for them.</summary>
    const int AnthropicCacheSeconds = 300;

    static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(2);

    // Connections to model providers and MCP servers; a long streamed answer must not be cut by a client timeout.
    static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(15), PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    readonly WebFetcher _webFetcher = new(options.Agent.WebFetchAllowPrivateNetworks);
    readonly ConcurrentDictionary<ulong, AgentLoop> _loops = new();

    /// <summary>A session changed: its loop, if it runs here, looks again. Called after commit on the event thread.</summary>
    public void Wake(ulong sessionId)
    {
        if (_loops.TryGetValue(sessionId, out var loop)) loop.Pulse();
    }

    /// <summary>Each new leader gives a backend action to every run that has work but none, such as runs from before backend
    /// actions ran the loops.</summary>
    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        try
        {
            var publisher = await node.Ready.WaitAsync(stopping);
            await leadership.RunWhileLeaderAsync(async leading =>
            {
                await publisher.PublishAsync(new AgentRunsResumed(), leading);
                await Task.Delay(Timeout.Infinite, leading);
            }, stopping);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    /// <summary>One attempt of a session's <see cref="AgentRun"/>: attaches to the run, so older attempts can no longer write, and runs
    /// the loop until the run ends or waits only for a person. The attempt's cancellation stops it; the next attempt goes on from the
    /// transcript.</summary>
    internal async Task<AgentRunResult> RunAsync(AgentRun input, IActionContext action)
    {
        var stopping = action.Cancellation;
        var publisher = await node.Ready.WaitAsync(stopping);
        await presence.Ready.WaitAsync(stopping);
        var attached = await publisher.PublishAndWaitAsync(new AgentRunAttached
        {
            SessionId = input.SessionId, ActionId = action.Id.ActionId, Attempt = action.Attempt, NodeId = presence.NodeId, PeerUrl = presence.PeerUrl
        }, stopping);
        if (attached is not { Attached: true })
        {
            logger.LogInformation("Attempt {Attempt} of agent session {SessionId} found the run held by another or ended", action.Attempt, input.SessionId);
            return new();
        }
        logger.LogInformation("Running agent session {SessionId}, attempt {Attempt}", input.SessionId, action.Attempt);
        var context = new AgentLoopContext(publisher, presence.NodeId, action.Id.ActionId, action.Attempt, attached.Clean, blobs, logger, PublishDelta,
            OpenEnvironmentAsync, MachineOf);
        var loop = new AgentLoop(context, input.SessionId);
        _loops[input.SessionId] = loop; // before the loop's first read, so no change is missed
        try
        {
            while (true)
                try
                {
                    return new() { Waiting = await loop.RunAsync(stopping) };
                }
                catch (Exception problem) when (!stopping.IsCancellationRequested)
                {
                    // Storage or leadership trouble: nothing is lost, the transcript says where to go on.
                    logger.LogWarning(problem, "The loop of agent session {SessionId} failed; trying again", input.SessionId);
                    await Task.Delay(RetryAfterFailure, time, stopping);
                }
        }
        finally
        {
            _loops.TryRemove(new KeyValuePair<ulong, AgentLoop>(input.SessionId, loop));
        }
    }

    void PublishDelta(LiveDelta delta)
    {
        hub.Delta(delta);
        relay.PublishSession(delta.SessionId, delta);
    }

    /// <summary>Sessions from before tool groups have none recorded.</summary>
    static string ToolGroupOf(Session session) => session.ToolGroup.Length == 0 ? AgentToolGroups.Full : session.ToolGroup;

    AgentMachine? MachineOf(Session session) =>
        session.MachineId == 0 ? null : new AgentMachine(rpc, session.MachineId, session.WorkingDirectory, session.Id, time);

    async Task<(AgentEnvironment, List<string>)> OpenEnvironmentAsync(Session session, CancellationToken cancellation)
    {
        var publisher = await node.Ready.WaitAsync(cancellation);
        string secret;
        AccountKind kind;
        ResolvedMcpServer[] servers;
        string? os = null, profilePrompt = null, profileMode = null;
        using (publisher.StartQuery())
        {
            if (Ambient.Relation<IHarnessAccountTable>().FindByIdOrDefault(session.AccountId) is not { } account || account.OwnerId != session.CreatedById)
                throw new AgentSetupException("The provider account of this session was removed.");
            kind = account.Kind;
            if (session.MachineId != 0) os = Ambient.Relation<IMachineTable>().FindByIdOrDefault(session.MachineId)?.Os ?? "an unknown operating system";
            secret = AccountEndpoints.SecretOf(account, protection);
            var profile = session.ProfileName == null ? null : Ambient.Relation<IProfileTable>().FindByIdOrDefault(session.ProfileName);
            servers = profile != null ? CatalogEndpoints.ResolveServers(profile, protection) : [];
            (profilePrompt, profileMode) = (profile?.SystemPrompt, profile?.SystemPromptMode);
        }
        IModelTurnExecutor executor = kind switch
        {
            AccountKind.OpenAIApiKey => OpenAIResponsesExecutor.WithApiKey(Http, secret,
                options.Agent.OpenAIEndpoint is { Length: > 0 } endpoint ? new Uri(endpoint) : null),
            AccountKind.AnthropicApiKey => AnthropicMessagesExecutor.WithApiKey(Http, secret,
                options.Agent.AnthropicEndpoint is { Length: > 0 } endpoint ? new Uri(endpoint) : null),
            AccountKind.OpenCodeGoApiKey => OpenCodeGo.ExecutorFor(Http, secret, session.Model!, options.Agent.OpenCodeGoBaseUrl),
            _ => throw new AgentSetupException($"The server agent cannot use a {kind} account yet.")
        };
        var (tools, problems) = await AgentToolbox.LoadAsync(Http, servers, MachineOf(session), 
            offersPatch: kind == AccountKind.OpenAIApiKey || (kind == AccountKind.OpenCodeGoApiKey && OpenCodeGo.OffersPatch(session.Model!)), ToolGroupOf(session),
            async (url, offset, stop) =>
            {
                var result = await _webFetcher.FetchAsync(url, offset, stop);
                await AuditEndpoints.RecordAsync(publisher, session.CreatedById, "web.fetched", WebFetcher.ForAudit(url), result.IsError ? "failed" : $"session {session.Id}", logger);
                return result;
            }, cancellation);
        var model = ModelCatalog.Find(kind, session.Model!);
        // The Messages API needs a limit, and an older model's is lower than the usual one; the Responses API has no need of one.
        var speaksMessages = kind == AccountKind.AnthropicApiKey || (kind == AccountKind.OpenCodeGoApiKey && OpenCodeGo.UsesMessages(session.Model!));
        var maxOutput = speaksMessages && model?.OutputLimit is { } limit ? (int)Math.Min(limit, AnthropicMessagesExecutor.DefaultMaxOutputTokens) : (int?)null;
        return (new AgentEnvironment(new RetryingTurnExecutor(executor, time), tools, AgentInstructions.Shared(profilePrompt, profileMode),
            AgentInstructions.Runtime(time.GetUtcNow(), os, session.WorkingDirectory, ToolGroupOf(session)), model, maxOutput, kind == AccountKind.AnthropicApiKey ? AnthropicCacheSeconds : 0), problems);
    }
}
