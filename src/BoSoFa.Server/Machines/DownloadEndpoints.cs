using System.Reflection;
using System.Text.RegularExpressions;
using BoSoFa.Protocol;
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

    public static void MapDownloadEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/downloads", (ClientBundle bundle) => bundle.Downloads());
        app.MapGet("/api/v1/downloads/{product}/{rid}", File);
        app.MapGet("/install/{script}", Script);
    }

    /// <summary>Only the files of this server's bundle, never blobs: those stay behind a sign-in.</summary>
    static Results<PhysicalFileHttpResult, NotFound> File(string product, string rid, ClientBundle bundle, HttpContext http)
    {
        if (bundle.Find(product, rid) is not { } file) return TypedResults.NotFound();
        http.Response.Headers[ShaHeader] = file.Sha256;
        http.Response.Headers[VersionHeader] = bundle.Version;
        return TypedResults.PhysicalFile(file.FullPath, "application/octet-stream", Releases.FileName(product, rid), enableRangeProcessing: true);
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
        return TypedResults.Text(reader.ReadToEnd().Replace("\r\n", "\n").Replace("__BOSOFA_SERVER__", origin)
            .Replace("__BRAND_NAME__", Brand.Name).Replace("__BRAND_ID__", Brand.Id).Replace("__BRAND_DAEMON__", Brand.Daemon), "text/plain; charset=utf-8");
    }

    [GeneratedRegex(@"^https?://[A-Za-z0-9.\-]+(:[0-9]+)?$|^https?://\[[0-9A-Fa-f:]+\](:[0-9]+)?$")]
    private static partial Regex PlainOrigin();
}
