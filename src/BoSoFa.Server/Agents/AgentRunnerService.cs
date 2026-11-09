using System.Collections.Concurrent;
using System.Threading.Channels;
using BoSoFa.Protocol;
using BoSoFa.Server.Accounts;
using BoSoFa.Server.Btdb;
using BoSoFa.Server.Domain;
using BoSoFa.Server.Live;
using BoSoFa.Server.Machines;
using BTDB.Replication;
using BTDB.Replication.EventSourcing;
using Microsoft.AspNetCore.DataProtection;

namespace BoSoFa.Server.Agents;

/// <summary>What a loop needs from its node.</summary>
sealed class AgentLoopContext(EventPublisher publisher, string nodeId, IBlobStore blobs, ILogger logger, Action<LiveDelta> publishDelta,
    Func<Session, CancellationToken, Task<(AgentEnvironment, List<string>)>> openEnvironment, Func<Session, AgentMachine?> machineOf)
{
    public EventPublisher Publisher => publisher;
    public string NodeId => nodeId;
    public IBlobStore Blobs => blobs;
    public ILogger Logger => logger;
    public void PublishDelta(LiveDelta delta) => publishDelta(delta);
    public AgentMachine? MachineOf(Session session) => machineOf(session);
    public Task<(AgentEnvironment Environment, List<string> Problems)> OpenEnvironmentAsync(Session session, CancellationToken cancellation) =>
        openEnvironment(session, cancellation);
}

/// <summary>
/// Runs the agent sessions claimed by this node, one <see cref="AgentLoop"/> each (docs/server-agent.md §4.2). The event log is the
/// bus: a message handled by any node shows up here as a changed session. While this node leads, it also takes over the sessions
/// of nodes that left the cluster.
/// </summary>
public sealed class AgentRunnerService(BtdbNode node, ServerOptions options, MachinePresenceService presence, ReplicationLeadership leadership,
    LiveHub hub, LiveRelay relay, MachineRpcClient rpc, IDataProtectionProvider protection, IBlobStore blobs, TimeProvider time, ILogger<AgentRunnerService> logger)
    : BackgroundService
{
    static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(2);
    static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(5);
    static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(2);

    // Connections to model providers and MCP servers; a long streamed answer must not be cut by a client timeout.
    static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(15), PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    readonly ConcurrentDictionary<ulong, RunningLoop> _loops = new();
    readonly Channel<ulong> _changed = Channel.CreateUnbounded<ulong>(new() { SingleReader = true });

    sealed record RunningLoop(AgentLoop Loop)
    {
        public Task Completion { get; set; } = Task.CompletedTask;
    }

    /// <summary>A session changed. Called after commit on the event thread, so the work happens on the dispatcher.</summary>
    public void Wake(ulong sessionId) => _changed.Writer.TryWrite(sessionId);

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        try
        {
            var publisher = await node.Ready.WaitAsync(stopping);
            await presence.Ready.WaitAsync(stopping);
            await Task.WhenAll(DispatchAsync(publisher, stopping), ReconcileAsync(publisher, stopping),
                leadership.RunWhileLeaderAsync(cancellation => SweepAsync(publisher, cancellation), stopping));
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        await Task.WhenAll(_loops.Values.Select(loop => loop.Completion));
    }

    async Task DispatchAsync(EventPublisher publisher, CancellationToken stopping)
    {
        await foreach (var sessionId in _changed.Reader.ReadAllAsync(stopping))
            if (_loops.TryGetValue(sessionId, out var running)) running.Loop.Pulse();
            else if (IsOurs(publisher, sessionId)) Start(publisher, sessionId, stopping);
    }

    /// <summary>A node that restarts finds its sessions here, and no lost signal can leave a session stuck.</summary>
    async Task ReconcileAsync(EventPublisher publisher, CancellationToken stopping)
    {
        while (true)
        {
            List<ulong> ours;
            using (publisher.StartQuery())
                ours = Ambient.Relation<IAgentRunnerTable>().Where(row => row.NodeId == presence.NodeId).Select(row => row.SessionId).ToList();
            foreach (var sessionId in ours) Wake(sessionId);
            await Task.Delay(ReconcileInterval, time, stopping);
        }
    }

    async Task SweepAsync(EventPublisher publisher, CancellationToken cancellation)
    {
        while (true)
        {
            await Task.Delay(SweepInterval, time, cancellation);
            var cluster = publisher.Cluster;
            if (cluster.Settling) continue; // the view may miss nodes that are alive
            List<(ulong SessionId, string NodeId)> orphaned;
            using (publisher.StartQuery())
                orphaned = Ambient.Relation<IAgentRunnerTable>().Where(row => row.NodeId != presence.NodeId && !cluster.LiveNodeIds.Contains(row.NodeId))
                    .Select(row => (row.SessionId, row.NodeId)).ToList();
            foreach (var (sessionId, previous) in orphaned)
            {
                logger.LogInformation("Node {NodeId} left the cluster; taking over agent session {SessionId}", previous, sessionId);
                await publisher.PublishAndWaitAsync(new AgentRunClaimed
                {
                    SessionId = sessionId, NodeId = presence.NodeId, PeerUrl = presence.PeerUrl, PreviousNodeId = previous
                }, cancellation);
            }
        }
    }

    bool IsOurs(EventPublisher publisher, ulong sessionId)
    {
        using var query = publisher.StartQuery();
        return Ambient.Relation<IAgentRunnerTable>().FindByIdOrDefault(sessionId)?.NodeId == presence.NodeId;
    }

    void Start(EventPublisher publisher, ulong sessionId, CancellationToken stopping)
    {
        var context = new AgentLoopContext(publisher, presence.NodeId, blobs, logger, PublishDelta, OpenEnvironmentAsync, MachineOf);
        var running = new RunningLoop(new AgentLoop(context, sessionId));
        if (!_loops.TryAdd(sessionId, running)) return;
        running.Completion = Task.Run(() => RunLoopAsync(running, sessionId, stopping), CancellationToken.None);
    }

    async Task RunLoopAsync(RunningLoop running, ulong sessionId, CancellationToken stopping)
    {
        try
        {
            while (true)
                try
                {
                    await running.Loop.RunAsync(stopping);
                    return;
                }
                catch (Exception problem) when (!stopping.IsCancellationRequested)
                {
                    // Storage or leadership trouble: nothing is lost, the transcript says where to go on.
                    logger.LogWarning(problem, "The loop of agent session {SessionId} failed; trying again", sessionId);
                    await Task.Delay(RetryAfterFailure, time, stopping);
                }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        finally
        {
            _loops.TryRemove(new KeyValuePair<ulong, RunningLoop>(sessionId, running));
            Wake(sessionId); // a message may have come in between the loop's last look and now
        }
    }

    void PublishDelta(LiveDelta delta)
    {
        hub.Delta(delta);
        relay.PublishSession(delta.SessionId, delta);
    }

    AgentMachine? MachineOf(Session session) =>
        session.MachineId == 0 ? null : new AgentMachine(rpc, session.MachineId, session.WorkingDirectory, session.Id, time);

    async Task<(AgentEnvironment, List<string>)> OpenEnvironmentAsync(Session session, CancellationToken cancellation)
    {
        var publisher = await node.Ready.WaitAsync(cancellation);
        string secret;
        AccountKind kind;
        ResolvedMcpServer[] servers;
        string? os = null;
        using (publisher.StartQuery())
        {
            if (Ambient.Relation<IHarnessAccountTable>().FindByIdOrDefault(session.AccountId) is not { } account || account.OwnerId != session.CreatedById)
                throw new AgentSetupException("The provider account of this session was removed.");
            kind = account.Kind;
            if (session.MachineId != 0) os = Ambient.Relation<IMachineTable>().FindByIdOrDefault(session.MachineId)?.Os ?? "an unknown operating system";
            secret = AccountEndpoints.SecretOf(account, protection);
            servers = session.ProfileName != null && Ambient.Relation<IProfileTable>().FindByIdOrDefault(session.ProfileName) is { } profile
                ? CatalogEndpoints.ResolveServers(profile, protection)
                : [];
        }
        IModelTurnExecutor executor = kind switch
        {
            AccountKind.OpenAIApiKey => OpenAIResponsesExecutor.WithApiKey(Http, secret,
                options.Agent.OpenAIEndpoint is { Length: > 0 } endpoint ? new Uri(endpoint) : null),
            AccountKind.AnthropicApiKey => AnthropicMessagesExecutor.WithApiKey(Http, secret,
                options.Agent.AnthropicEndpoint is { Length: > 0 } endpoint ? new Uri(endpoint) : null),
            _ => throw new AgentSetupException($"The server agent cannot use a {kind} account yet.")
        };
        var (tools, problems) = await AgentToolbox.LoadAsync(Http, servers, MachineOf(session), offersPatch: kind == AccountKind.OpenAIApiKey, cancellation);
        return (new AgentEnvironment(new RetryingTurnExecutor(executor, time), tools, AgentInstructions.For(os, session.WorkingDirectory)), problems);
    }
}
