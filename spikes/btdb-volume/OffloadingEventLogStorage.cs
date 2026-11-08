using BTDB.Replication.EventLog;

namespace BtdbVolume;

/// <summary>Spike experiment: FileSystemEventLogStorage completes every write synchronously (a flushed file write plus
/// a flushed metadata write under its lock). This wrapper runs writes on the thread pool so the EventLog caller that
/// triggered the owner's commit loop is not blocked for the duration of the write.</summary>
public sealed class OffloadingEventLogStorage(IEventLogStorage inner) : IEventLogStorage
{
    public ValueTask<EventLogBlob?> ReadAsync(string key, CancellationToken cancellation) =>
        inner.ReadAsync(key, cancellation);

    public ValueTask<EventLogBlobProperties?> GetPropertiesAsync(string key, CancellationToken cancellation) =>
        inner.GetPropertiesAsync(key, cancellation);

    public ValueTask ReadRangeAsync(string key, string version, long offset, Memory<byte> destination,
        CancellationToken cancellation) => inner.ReadRangeAsync(key, version, offset, destination, cancellation);

    public async ValueTask<EventLogWriteResult> WriteAsync(string key, string? expectedVersion,
        ReadOnlyMemory<byte> content, EventLogOwner? owner, string? sha256, CancellationToken cancellation) =>
        await Task.Run(() => inner.WriteAsync(key, expectedVersion, content, owner, sha256, cancellation).AsTask(),
            cancellation);

    public async ValueTask<EventLogWriteResult> SetOwnerAsync(string key, string expectedVersion, EventLogOwner owner,
        CancellationToken cancellation) =>
        await Task.Run(() => inner.SetOwnerAsync(key, expectedVersion, owner, cancellation).AsTask(), cancellation);

    public IAsyncEnumerable<EventLogObjectInfo> ListAsync(string prefix, CancellationToken cancellation) =>
        inner.ListAsync(prefix, cancellation);

    public ValueTask<string?> ScheduleDeletionAsync(string key, string version, TimeSpan delay,
        CancellationToken cancellation) => inner.ScheduleDeletionAsync(key, version, delay, cancellation);

    public ValueTask<bool> DeleteAsync(string key, string version, CancellationToken cancellation) =>
        inner.DeleteAsync(key, version, cancellation);
}
