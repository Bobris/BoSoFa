using BoSoFa.Daemon.Sessions;
using BoSoFa.Protocol;

namespace BoSoFa.Daemon.Tools;

/// <summary>Puts the images a tool gives the model in the server's blob store; the transcript keeps only their hashes.</summary>
sealed class ToolImages(IBlobUploader? uploader)
{
    public bool CanUpload => uploader != null;

    public async Task<PromptImage> UploadFileAsync(string file, string mediaType, CancellationToken cancellation)
    {
        var blob = await uploader!.UploadAsync(file, cancellation);
        return new(blob.Sha256, mediaType, blob.Size);
    }

    /// <summary>The images as blobs; where they cannot be uploaded, the text says they were left out.</summary>
    public async Task<ToolRunResult> ResultAsync(string text, bool isError, IReadOnlyList<byte[]> images, CancellationToken cancellation)
    {
        if (images.Count == 0) return new(text, isError);
        if (uploader == null) return new(text + (text.Length > 0 ? "\n" : "") + $"[{images.Count} image(s) left out: this machine cannot upload them]", isError);
        var uploaded = new List<PromptImage>();
        foreach (var image in images)
        {
            var file = Path.GetTempFileName();
            try
            {
                await File.WriteAllBytesAsync(file, image, cancellation);
                uploaded.Add(await UploadFileAsync(file, PromptImages.MediaTypeOf(image.AsSpan(0, Math.Min(image.Length, PromptImages.HeaderLength)))!, cancellation));
            }
            finally { File.Delete(file); }
        }
        return new(text, isError, uploaded.ToArray());
    }
}
