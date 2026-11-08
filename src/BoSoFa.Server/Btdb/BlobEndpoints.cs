using BoSoFa.Protocol;
using BoSoFa.Server.Machines;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;

namespace BoSoFa.Server.Btdb;

/// <summary>Upload and download of content-addressed blobs, for people and for daemons.</summary>
public static class BlobEndpoints
{
    const long MaximumBlobBytes = 1024L * 1024 * 1024;

    public static void MapBlobEndpoints(this WebApplication app)
    {
        var people = app.MapGroup("/api/v1/blobs").RequireAuthorization();
        people.MapPost("", UploadAsync);
        people.MapGet("/{sha256}", DownloadAsync);
        var daemons = app.MapGroup("/api/daemon/v1/blobs").RequireAuthorization(DaemonTokens.Scheme);
        daemons.MapMethods("/{sha256}", ["HEAD"], ExistsAsync);
        daemons.MapPut("/{sha256}", PutAsync);
        daemons.MapGet("/{sha256}", DownloadAsync);
    }

    static void AllowLargeBody(HttpContext http)
    {
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = MaximumBlobBytes;
    }

    static async Task<Results<Ok<BlobRef>, BadRequest<string>>> UploadAsync(IBlobStore blobs, HttpContext http)
    {
        AllowLargeBody(http);
        string file, sha256;
        long size;
        try { (file, sha256, size) = await BlobHash.SpoolAsync(http.Request.Body, MaximumBlobBytes, http.RequestAborted); }
        catch (InvalidDataException problem) { return TypedResults.BadRequest(problem.Message); }
        await blobs.PutAsync(sha256, file, http.RequestAborted);
        return TypedResults.Ok(new BlobRef(sha256, size));
    }

    /// <summary>The client names the hash; the content must match it.</summary>
    static async Task<Results<Ok<BlobRef>, BadRequest<string>>> PutAsync(string sha256, IBlobStore blobs, HttpContext http)
    {
        if (!BlobHash.IsValid(sha256)) return TypedResults.BadRequest("Not a lowercase SHA-256.");
        AllowLargeBody(http);
        string file, actual;
        long size;
        try { (file, actual, size) = await BlobHash.SpoolAsync(http.Request.Body, MaximumBlobBytes, http.RequestAborted); }
        catch (InvalidDataException problem) { return TypedResults.BadRequest(problem.Message); }
        if (actual != sha256)
        {
            File.Delete(file);
            return TypedResults.BadRequest($"The content's SHA-256 is {actual}.");
        }
        await blobs.PutAsync(sha256, file, http.RequestAborted);
        return TypedResults.Ok(new BlobRef(sha256, size));
    }

    static async Task<Results<Ok, NotFound>> ExistsAsync(string sha256, IBlobStore blobs, HttpContext http) =>
        BlobHash.IsValid(sha256) && await blobs.ExistsAsync(sha256, http.RequestAborted) ? TypedResults.Ok() : TypedResults.NotFound();

    static async Task<Results<FileStreamHttpResult, NotFound>> DownloadAsync(string sha256, IBlobStore blobs, HttpContext http) =>
        BlobHash.IsValid(sha256) && await blobs.OpenAsync(sha256, http.RequestAborted) is { } content
            ? TypedResults.Stream(content, "application/octet-stream")
            : TypedResults.NotFound();
}
