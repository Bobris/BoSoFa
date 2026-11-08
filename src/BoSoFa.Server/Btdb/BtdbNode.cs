using BoSoFa.Server.Domain;
using BTDB.KVDBLayer;
using BTDB.ODBLayer;
using BTDB.Replication;
using BTDB.Replication.EventLog;
using BTDB.Replication.EventSourcing;

namespace BoSoFa.Server.Btdb;

/// <summary>
/// The server's replicated database "main": restored (or created empty) from canonical storage, then driven by one
/// <see cref="EventPublisher"/> over the EventLog topics "data" and "metadata". Every node runs that application and
/// consumes every event into its own database; the leader also publishes it. A follower initializes this build's
/// schema itself and starts the application. If that writes anything, BTDB detaches its database from comparison and
/// requests a restart once the leader published the schema, or lets a node of a newer generation take over and publish
/// it. Schema changes need a new application generation (plan §8).
/// </summary>
public sealed class BtdbNode(BtdbNodeOptions options, ILogger<BtdbNode> logger, IHostApplicationLifetime lifetime)
    : IReplicationNodeHost, IHostedService, IAsyncDisposable
{
    public const string Database = "main";
    static readonly TrlSuccessor Genesis = new("1.trl", 1);

    readonly OnDiskReplicationFileStorage _cache = new(options.CacheDirectory, options.FlushToDisk);
    readonly TransactionLogCapture _capture = new();
    readonly SemaphoreSlim _schema = new(1);
    readonly object _progressLock = new();
    readonly CancellationTokenSource _stopping = new();
    readonly TaskCompletionSource<IServiceProvider> _services = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource<EventPublisher> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    ReplicationFileSet? _files;
    BTreeKeyValueDB? _db;
    ObjectDB? _objects;
    LeaderTrlProgress? _progress;
    EventPublisher? _publisher;
    Task? _application;
    int _started;
    bool _schemaReady;

    public IBindableReplicationStorage Canonical { get; } = options.Canonical;
    public ReplicationNodeRole Role { get; private set; } = ReplicationNodeRole.Restoring;

    /// <summary>The running publisher; completes once the startup replay is visible.</summary>
    public Task<EventPublisher> Ready => _ready.Task;

    public EventPublisher? Publisher => Volatile.Read(ref _publisher);

    /// <summary>Call once the host built its service provider; the application needs the EventLog from it.</summary>
    public void Attach(IServiceProvider services) => _services.TrySetResult(services);

    public async ValueTask<IReadOnlyList<ActivationDatabase>> RestoreAsync(CancellationToken cancellation)
    {
        _objects?.Dispose();
        _objects = null;
        _db?.Dispose();
        _db = null;
        if (_files != null)
        {
            await _files.DisposeAsync();
            _files = null;
        }
        _files = await ReplicationFileSet.RestoreAsync(_cache, Canonical, Genesis, cancellation);
        var restored = _files.HasPublishedHistory;
        _db = await BTreeKeyValueDB.OpenAsync(new KeyValueDBOptions
        {
            FileCollection = _files, TransactionLogCapture = _capture, CompactorScheduler = null,
            Compression = new NoCompressionStrategy(), RequireExplicitTransactions = true
        }, cancellation);
        _objects = new ObjectDB();
        _objects.Open(_db, false, new DBOptions().WithoutAutoRegistrationOfRelations());
        _schemaReady = false;
        logger.LogInformation(restored ? "Restored database {Database}" : "Starting new database {Database}", Database);
        if (restored)
        {
            var position = _db.ReplicationRestoredPosition;
            using (var read = _db.StartReadOnlyTransaction())
                lock (_progressLock) _progress = new(read.GetCommitUlong(), position.FileId, position.Offset);
        }
        return [new(Database, _db, _capture, Canonical, Genesis, restored ? _db.ReplicationRestoredPosition : default,
            id => $"{id}.trl")];
    }

    public async ValueTask PrepareSchemaAsync(ActivationDatabase database, LeaseAuthority authority,
        CancellationToken cancellation)
    {
        if (!authority.IsValid) throw new IOException("Leader authority ended before schema preparation.");
        await EnsureSchemaAsync(true, cancellation);
        var end = _capture.Completed;
        if (end.FileId != 0 && Publisher == null)
            using (var read = _db!.StartReadOnlyTransaction())
                lock (_progressLock) _progress = new(read.GetCommitUlong(), end.FileId, end.Offset);
        StartApplication();
    }

    public ReplicationMaintenance? CreateMaintenance(ActivationDatabase database, CanonicalTrlPublisher publisher,
        LeaseAuthority authority)
    {
        var services = _services.Task.Result; // attached before the host starts replication
        var inventory = CanonicalTrlInventory.DiscoverAsync(Canonical, Genesis).AsTask().GetAwaiter().GetResult();
        return new(_db!, _files!, publisher, Canonical.Bind(inventory, authority), authority,
            services.GetRequiredService<IReplicationScheduler>(), options.CheckpointInterval, options.DeletionDelay, _objects);
    }

    /// <summary>Registers this build's relations. A leader writes them under its authority in
    /// <see cref="PrepareSchemaAsync"/> and a follower may write them too (BTDB then detaches it); a node still
    /// activating only checks its history, because it is about to write them with authority.</summary>
    async Task<bool> EnsureSchemaAsync(bool mayWrite, CancellationToken cancellation)
    {
        await _schema.WaitAsync(cancellation);
        try
        {
            if (_schemaReady) return true;
            if (mayWrite) await _objects!.InitializeRelations(Domain.Domain.Relations);
            else if (!await _objects!.TryInitializeRelationsWithoutWriting(Domain.Domain.Relations)) return false;
            _schemaReady = true;
            return true;
        }
        finally { _schema.Release(); }
    }

    /// <summary>A restored node runs the application as soon as it has this build's schema: every node consumes the
    /// same events into its own database.</summary>
    async Task StartWithSchemaAsync(bool mayWrite)
    {
        try
        {
            if (await EnsureSchemaAsync(mayWrite, _stopping.Token)) StartApplication();
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        catch (Exception error)
        {
            logger.LogCritical(error, "Checking the schema failed; restarting the process");
            lifetime.StopApplication();
        }
    }

    void StartApplication()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0) _application = RunApplicationAsync();
    }

    async Task RunApplicationAsync()
    {
        try
        {
            var services = await _services.Task;
            var log = services.GetRequiredService<IEventLog>();
            var publisher = new EventPublisher(_objects!, log.GetTopic("data"), log.GetTopic("metadata"),
                Domain.Domain.CreateDispatcher(services.GetRequiredService<IDomainSignals>()),
                new() { Cluster = new EventCluster(options.NodeId, services.GetRequiredService<ReplicationPresence>()) },
                _capture);
            await publisher.StartAsync(_stopping.Token);
            Volatile.Write(ref _publisher, publisher);
            _ready.TrySetResult(publisher);
            logger.LogInformation("Application started at event {EventId}", publisher.LastAppliedEventId);
            await publisher.Completion;
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            _ready.TrySetCanceled();
        }
        catch (Exception error)
        {
            logger.LogCritical(error, "The application stopped; restarting the process");
            _ready.TrySetException(error);
            lifetime.StopApplication();
        }
    }

    public LeaderCandidate CreateCandidate() => new(options.ClusterId, options.NodeId, Guid.NewGuid().ToString("N"),
        options.ApplicationGeneration, [Database], options.Endpoint, Guid.NewGuid().ToString("N"));

    public LeaderTrlProgress? GetProgress(string database)
    {
        if (Publisher?.Progress is { } progress) return progress;
        lock (_progressLock) return _progress;
    }

    public void RequestRestart(string reason)
    {
        logger.LogWarning("Replication requested a restart: {Reason}", reason);
        lifetime.StopApplication();
    }

    public void RequestFatalRestart(string reason)
    {
        logger.LogCritical("Replication requested a fatal restart: {Reason}", reason);
        Environment.Exit(75);
    }

    public void ReportStatus(ReplicationNodeRole role)
    {
        Role = role;
        if (role is ReplicationNodeRole.Follower or ReplicationNodeRole.Activating or ReplicationNodeRole.Leader &&
            Volatile.Read(ref _started) == 0)
            _ = StartWithSchemaAsync(role == ReplicationNodeRole.Follower);
    }

    public void DatabaseRemoved(string database) { }

    public ValueTask<ulong> CaptureInitializationCursorAsync(string database, CancellationToken cancellation) =>
        ValueTask.FromResult(0ul);

    public Task StartAsync(CancellationToken cancellation) => Task.CompletedTask;

    /// <summary>Stops the application while the EventLog and replication still run. Registered as a hosted service
    /// after them, so it stops first: the container disposes singletons in reverse creation order, and this node is
    /// created before the EventLog it reads, so waiting for its disposal would read from disposed storage.</summary>
    public async Task StopAsync(CancellationToken cancellation)
    {
        await _stopping.CancelAsync();
        if (_publisher != null) await _publisher.DisposeAsync();
        if (_application != null)
            try { await _application; }
            catch (Exception) { /* logged by the application loop */ }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _objects?.Dispose();
        _db?.Dispose();
        if (_files != null) await _files.DisposeAsync();
        _cache.Dispose();
        if (Canonical is IDisposable canonical) canonical.Dispose();
    }
}

/// <summary>Ready once the event loop runs: replication's own check passes earlier, when the API could not yet serve.</summary>
public sealed class ApplicationHealthCheck(BtdbNode node) : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    public Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(node.Publisher != null
            ? Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy()
            : Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("The application has not started."));
}

public sealed record BtdbNodeOptions(string ClusterId, string NodeId, string Endpoint, ulong ApplicationGeneration,
    string CacheDirectory, IBindableReplicationStorage Canonical)
{
    public TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromMinutes(5);
    public bool FlushToDisk { get; init; } = true;
    public TimeSpan DeletionDelay { get; init; } = TimeSpan.FromDays(1);
}
