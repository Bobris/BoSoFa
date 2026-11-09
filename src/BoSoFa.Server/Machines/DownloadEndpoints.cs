using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using BoSoFa.Protocol;
using BoSoFa.Server.Btdb;
using BoSoFa.Server.Domain;
using BTDB.Replication.EventSourcing;
using Microsoft.AspNetCore.Http.HttpResults;

namespace BoSoFa.Server.Machines;

/// <summary>
/// How the daemon and the desktop app reach a computer (plan §5.7): the install page, its scripts, and the files they fetch.
/// None of it needs a sign-in, because a computer has no identity yet when it starts here. The builds hold no secrets, and
/// a machine only joins when a person approves its code.
/// </summary>
public static partial class DownloadEndpoints
{
    public const string ShaHeader = "X-BoSoFa-Sha256";
    public const string VersionHeader = "X-BoSoFa-Version";
    const string Latest = "latest";

    public static void MapDownloadEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/downloads", ListAsync);
        app.MapGet("/api/v1/downloads/{product}/latest", LatestReleaseAsync);
        app.MapGet("/api/v1/downloads/{product}/{version}/{rid}", FileAsync);
        app.MapGet("/install/{script}", Script);
    }

    sealed record Row(string Version, string Channel, string Manifest, string Signature, DateTime PublishedAt);

    /// <summary>Newest first. Must run inside a query.</summary>
    static IEnumerable<Row> Rows(string product) => (product == Releases.Daemon
            ? Ambient.Relation<IDaemonReleaseTable>().Select(release => new Row(release.Version, release.Channel, release.Manifest, release.Signature, release.PublishedAt))
            : Ambient.Relation<IDesktopReleaseTable>().Select(release => new Row(release.Version, release.Channel, release.Manifest, release.Signature, release.PublishedAt)))
        .OrderByDescending(row => row.PublishedAt);

    static ReleaseManifest? Parse(string manifest)
    {
        try { return JsonSerializer.Deserialize(manifest, ProtocolJsonContext.Default.ReleaseManifest); }
        catch (JsonException) { return null; }
    }

    static Row? NewestStable(string product, string? rid) => Rows(product).FirstOrDefault(row => row.Channel == Releases.Stable &&
        (rid == null || Parse(row.Manifest)?.Artifacts.Any(artifact => artifact.Rid == rid) == true));

    static async Task<ProductDownload[]> ListAsync(BtdbNode node, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using var query = publisher.StartQuery();
        var result = new List<ProductDownload>();
        foreach (var product in new[] { Releases.Daemon, Releases.Desktop })
        {
            if (NewestStable(product, null) is not { } row || Parse(row.Manifest) is not { } manifest) continue;
            result.Add(new(product, row.Version, row.PublishedAt, manifest.Artifacts.Select(artifact => new DownloadFile(artifact.Rid,
                Releases.FileName(product, artifact.Rid), artifact.Sha256, artifact.Size, $"/api/v1/downloads/{product}/{row.Version}/{artifact.Rid}")).ToArray()));
        }
        return result.ToArray();
    }

    /// <summary>What an app checks before updating itself; the signature is its proof, so the server need not be believed.</summary>
    static async Task<Results<Ok<SignedRelease>, NotFound>> LatestReleaseAsync(string product, string rid, BtdbNode node, HttpContext http)
    {
        if (!Releases.IsValidProduct(product)) return TypedResults.NotFound();
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using var query = publisher.StartQuery();
        return NewestStable(product, rid) is { } row ? TypedResults.Ok(new SignedRelease(row.Version, row.Manifest, row.Signature)) : TypedResults.NotFound();
    }

    /// <summary>Only files of published releases, never other blobs: those stay behind a sign-in.</summary>
    static async Task<Results<FileStreamHttpResult, NotFound>> FileAsync(string product, string version, string rid, IBlobStore blobs, BtdbNode node, HttpContext http)
    {
        if (!Releases.IsValidProduct(product)) return TypedResults.NotFound();
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        ReleaseArtifact? artifact;
        string? found;
        using (publisher.StartQuery())
        {
            var row = version == Latest ? NewestStable(product, rid) : Rows(product).FirstOrDefault(candidate => candidate.Version == version);
            found = row?.Version;
            artifact = row == null ? null : Parse(row.Manifest)?.Artifacts.FirstOrDefault(candidate => candidate.Rid == rid);
        }
        if (artifact == null || await blobs.OpenAsync(artifact.Sha256, http.RequestAborted) is not { } content) return TypedResults.NotFound();
        http.Response.Headers[ShaHeader] = artifact.Sha256;
        http.Response.Headers[VersionHeader] = found;
        http.Response.ContentLength = artifact.Size;
        return TypedResults.Stream(content, "application/octet-stream", Releases.FileName(product, rid));
    }

    /// <summary>The script names this server, so that what a person copies needs no editing. The origin ends up in a shell
    /// script, so a Host header that is not a plain host name is refused.</summary>
    static Results<ContentHttpResult, NotFound, BadRequest<string>> Script(string script, HttpContext http)
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("install/" + script);
        if (resource == null) return TypedResults.NotFound();
        var origin = $"{http.Request.Scheme}://{http.Request.Host}";
        if (!PlainOrigin().IsMatch(origin)) return TypedResults.BadRequest("That is not a plain host name.");
        using var reader = new StreamReader(resource);
        return TypedResults.Text(reader.ReadToEnd().Replace("\r\n", "\n").Replace("__BOSOFA_SERVER__", origin), "text/plain; charset=utf-8");
    }

    [GeneratedRegex(@"^https?://[A-Za-z0-9.\-]+(:[0-9]+)?$|^https?://\[[0-9A-Fa-f:]+\](:[0-9]+)?$")]
    private static partial Regex PlainOrigin();
}
