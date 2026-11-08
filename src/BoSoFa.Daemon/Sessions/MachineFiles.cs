using System.Buffers;
using System.Text;
using BoSoFa.Protocol;

namespace BoSoFa.Daemon.Sessions;

/// <summary>The file browser and transfers of a machine (plan §5.6), always under the allowed roots.</summary>
public sealed class MachineFiles(Func<IReadOnlyCollection<string>> roots, IBlobUploader? uploader, IBlobDownloader? downloader)
{
    public const int TextChunkBytes = 256 * 1024;
    public const int MaximumEntries = 5000;
    static readonly RpcFailure Outside = new("That path is outside the folders this machine shares.");
    static readonly RpcFailure NoTransfers = new("File transfers are not available on this daemon.");

    public async Task<MachineRpcResult> HandleAsync(MachineRpc request, CancellationToken cancellation)
    {
        try
        {
            return request switch
            {
                FileRootsRequest => new FileRootsResult(roots().ToArray()),
                FileListRequest list => List(list.Path),
                FileReadRequest read => await ReadAsync(read, cancellation),
                FileFetchRequest fetch => await FetchAsync(fetch.Path, cancellation),
                FileStoreRequest store => await StoreAsync(store, cancellation),
                _ => new RpcFailure("This daemon does not know that request.")
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or HttpRequestException or
                                              InvalidDataException or ArgumentException)
        {
            return new RpcFailure(exception.Message);
        }
    }

    string? Guard(string path) => Path.IsPathFullyQualified(path) ? PathGuard.Resolve(roots(), path) : null;

    static FileEntry EntryOf(FileSystemInfo info)
    {
        var isDirectory = Directory.Exists(info.FullName);
        return new(info.Name, info.FullName, isDirectory, info.LinkTarget != null, info is FileInfo { Exists: true } file ? file.Length : 0,
            info.LastWriteTimeUtc);
    }

    MachineRpcResult List(string path)
    {
        if (Guard(path) is not { } directory) return Outside;
        if (!Directory.Exists(directory)) return new RpcFailure("No such folder.");
        var entries = new DirectoryInfo(directory).EnumerateFileSystemInfos().Take(MaximumEntries + 1).Select(EntryOf).ToList();
        var truncated = entries.Count > MaximumEntries;
        if (truncated) entries.RemoveAt(MaximumEntries);
        var isRoot = roots().Any(root => Path.TrimEndingDirectorySeparator(root) == directory);
        return new FileListResult(directory, isRoot ? null : Path.GetDirectoryName(directory),
            entries.OrderByDescending(entry => entry.IsDirectory).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            truncated);
    }

    async Task<MachineRpcResult> ReadAsync(FileReadRequest request, CancellationToken cancellation)
    {
        if (Guard(request.Path) is not { } file) return Outside;
        if (!File.Exists(file)) return new RpcFailure("No such file.");
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var offset = Math.Clamp(request.Offset, 0, stream.Length);
        stream.Position = offset;
        var buffer = new byte[TextChunkBytes];
        var count = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellation);
        var read = buffer.AsSpan(0, count);
        if (read.Contains((byte)0)) return new FileTextResult(file, offset, offset, stream.Length, true, "");
        // Not to split a character at the end of the chunk.
        if (Rune.DecodeLastFromUtf8(read, out _, out var tail) == OperationStatus.NeedMoreData) count -= tail;
        return new FileTextResult(file, offset, offset + count, stream.Length, false, Encoding.UTF8.GetString(buffer, 0, count));
    }

    async Task<MachineRpcResult> FetchAsync(string path, CancellationToken cancellation)
    {
        if (uploader == null) return NoTransfers;
        if (Guard(path) is not { } file) return Outside;
        if (!File.Exists(file)) return new RpcFailure("No such file.");
        return new FileFetchResult(Path.GetFileName(file), await uploader.UploadAsync(file, cancellation));
    }

    async Task<MachineRpcResult> StoreAsync(FileStoreRequest request, CancellationToken cancellation)
    {
        if (downloader == null) return NoTransfers;
        if (request.Name is "." or ".." || request.Name.Length == 0 || Path.GetFileName(request.Name) != request.Name)
            return new RpcFailure("Not a file name.");
        if (Guard(request.Directory) is not { } directory || Guard(Path.Combine(directory, request.Name)) is not { } target) return Outside;
        if (!Directory.Exists(directory)) return new RpcFailure("No such folder.");
        if (Directory.Exists(target)) return new RpcFailure($"{request.Name} is a folder.");
        if (File.Exists(target) && !request.Overwrite) return new RpcFailure($"{request.Name} already exists.");
        var temporary = Path.Combine(directory, $".bosofa-{Guid.NewGuid():N}.tmp");
        try
        {
            await downloader.DownloadBlobAsync(request.Sha256, temporary, cancellation);
            File.Move(temporary, target, request.Overwrite);
        }
        finally { File.Delete(temporary); }
        return new FileStoreResult(EntryOf(new FileInfo(target)));
    }
}
