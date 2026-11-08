using Azure.Storage.Blobs;
using BTDB.Replication;
using BTDB.Replication.Azure;
using BTDB.Replication.EventLog;
using Microsoft.AspNetCore.DataProtection;

namespace BoSoFa.Server.Btdb;

/// <summary>
/// Where the replicated state lives: the leader document, the EventLog, the canonical database files, and the Data
/// Protection key ring (which must be shared by every node). Local directories for a single node or several nodes of
/// one process; one Azure Blob container for a real cluster, laid out as <c>cluster/leader.json</c>,
/// <c>eventlog/</c>, <c>db/main/</c>, <c>blobs/</c> and <c>dataprotection/keys.xml</c>.
/// </summary>
public sealed record NodeStorage(
    Func<IServiceProvider, IReplicationLeaderStorage> Leader,
    Func<IServiceProvider, IEventLogStorage> EventLog,
    IBindableReplicationStorage Canonical,
    Action<IDataProtectionBuilder> PersistKeys,
    IBlobStore Blobs)
{
    public const string InitialLeaderJson =
        """{"format":1,"clusterId":"bosofa","term":0,"revision":0,"applicationGeneration":0,"databaseNames":[]} """;

    public static NodeStorage Create(ServerOptions options, string dataDirectory, string contentRoot)
    {
        if (options.Cluster?.Azure is { } azure) return Azure(azure);
        // Single node, or a test cluster whose nodes share one directory in this process.
        var shared = options.Cluster?.SharedDirectory is { } sharedDirectory
            ? Path.GetFullPath(sharedDirectory, contentRoot)
            : Path.Combine(dataDirectory, "shared");
        if (options.Cluster != null && options.Cluster.SharedDirectory == null)
            throw new InvalidOperationException("A cluster needs BoSoFa:Cluster:Azure (or SharedDirectory for tests).");
        var flush = options.FlushToDisk;
        // Factories, so the container disposes the storages with the host: a file-system store keeps its directory
        // lock and the in-memory leader lease until its last instance is disposed.
        return new(
            _ => new FileSystemLeaderStorage(Path.Combine(shared, "leader"), TimeSpan.FromSeconds(15), InitialLeaderJson,
                flushToDisk: flush),
            _ => new FileSystemEventLogStorage(Path.Combine(shared, "eventlog"), flushToDisk: flush),
            new FileSystemReplicationStorage(Path.Combine(shared, "db", BtdbNode.Database), flushToDisk: flush),
            keys => keys.PersistKeysToFileSystem(new DirectoryInfo(options.Cluster != null
                ? Path.Combine(shared, "keys")
                : Path.Combine(dataDirectory, "keys"))),
            new FileBlobStore(Path.Combine(shared, "blobs")));
    }

    static NodeStorage Azure(AzureStorageOptions azure)
    {
        if (string.IsNullOrWhiteSpace(azure.ConnectionString))
            throw new InvalidOperationException("BoSoFa:Cluster:Azure:ConnectionString is required.");
        // BTDB reconciles ambiguous conditional writes itself, so its clients never retry; authority (the lease) and
        // data transfers use separate clients, so a slow upload cannot delay a renewal.
        BlobContainerClient Container(int retries)
        {
            var clientOptions = new BlobClientOptions();
            clientOptions.Retry.MaxRetries = retries;
            return new BlobServiceClient(azure.ConnectionString, clientOptions).GetBlobContainerClient(azure.Container);
        }
        var data = Container(0);
        data.CreateIfNotExists();
        var authority = Container(0);
        var keys = Container(3); // Data Protection expects an ordinary client
        return new(
            _ => new AzureLeaderStorage(authority.GetBlobClient("cluster/leader.json"), TimeSpan.FromSeconds(15),
                InitialLeaderJson),
            _ => new AzureEventLogStorage(data, "eventlog"),
            new AzureReplicationStorage(data, "db/" + BtdbNode.Database),
            dataProtection => dataProtection.PersistKeysToAzureBlobStorage(keys.GetBlobClient("dataprotection/keys.xml")),
            new AzureBlobStore(keys)); // ordinary retries: blobs are immutable
    }
}
