using System.Security.Cryptography;
using Azure;
using Azure.Storage.Blobs;

namespace BoSoFa.Server.Btdb;

/// <summary>Large content addressed by its SHA-256 (plan §4.2): skill bundles, files, attachments. Events carry only the
/// hash. Content is immutable, so concurrent uploads of the same hash are harmless.</summary>
public interface IBlobStore
{
    Task<bool> ExistsAsync(string sha256, CancellationToken cancellation);

    /// <summary>Opens the content for reading, or null when there is none.</summary>
    Task<Stream?> OpenAsync(string sha256, CancellationToken cancellation);

    /// <summary>Stores a file that was already verified to have this hash; the store may move it.</summary>
    Task PutAsync(string sha256, string verifiedFile, CancellationToken cancellation);
}

public static class BlobHash
{
    public static bool IsValid(string sha256) => sha256.Length == 64 && sha256.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    /// <summary>Copies a stream to a temporary file, returning it with its hash and size, at most <paramref name="limit"/> bytes.</summary>
    public static async Task<(string File, string Sha256, long Size)> SpoolAsync(Stream content, long limit, CancellationToken cancellation)
    {
        var file = Path.Combine(Path.GetTempPath(), "bosofa-blob-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long size = 0;
            await using (var output = File.Create(file))
                while (await content.ReadAsync(buffer, cancellation) is var read and > 0)
                {
                    size += read;
                    if (size > limit) throw new InvalidDataException($"The content is larger than {limit} bytes.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
                }
            return (file, Convert.ToHexStringLower(hash.GetHashAndReset()), size);
        }
        catch
        {
            File.Delete(file);
            throw;
        }
    }
}

public sealed class FileBlobStore(string directory) : IBlobStore
{
    string PathOf(string sha256) => Path.Combine(directory, sha256[..2], sha256);

    public Task<bool> ExistsAsync(string sha256, CancellationToken cancellation) => Task.FromResult(File.Exists(PathOf(sha256)));

    public Task<Stream?> OpenAsync(string sha256, CancellationToken cancellation) =>
        Task.FromResult<Stream?>(File.Exists(PathOf(sha256)) ? File.OpenRead(PathOf(sha256)) : null);

    public Task PutAsync(string sha256, string verifiedFile, CancellationToken cancellation)
    {
        var target = PathOf(sha256);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(verifiedFile, target, overwrite: true);
        return Task.CompletedTask;
    }
}

public sealed class AzureBlobStore(BlobContainerClient container) : IBlobStore
{
    BlobClient Blob(string sha256) => container.GetBlobClient($"blobs/{sha256[..2]}/{sha256}");

    public async Task<bool> ExistsAsync(string sha256, CancellationToken cancellation) =>
        await Blob(sha256).ExistsAsync(cancellation);

    public async Task<Stream?> OpenAsync(string sha256, CancellationToken cancellation)
    {
        try { return await Blob(sha256).OpenReadAsync(cancellationToken: cancellation); }
        catch (RequestFailedException error) when (error.Status == 404) { return null; }
    }

    public async Task PutAsync(string sha256, string verifiedFile, CancellationToken cancellation)
    {
        await Blob(sha256).UploadAsync(verifiedFile, overwrite: true, cancellation);
        File.Delete(verifiedFile);
    }
}
