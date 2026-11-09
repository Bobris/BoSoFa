using NeoAstra;

namespace BoSoFa.Desktop;

/// <summary>Serves the built page on <c>app://bosofa</c>, with an index.html fallback for client routes and a CSP that names
/// the server, which is known only at run time.</summary>
public sealed class SpaProvider(string root, Func<string?> server) : INeoResourceProvider
{
    readonly NeoDirectoryResourceProvider _files = new(root);
    readonly string _index = Path.Combine(root, "index.html");

    public static string ContentSecurityPolicy(string? server)
    {
        var sockets = server == null ? "" : $" {server} ws{server[4..]}"; // http→ws, https→wss
        return $"default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self'; connect-src 'self'{sockets}; " +
               "object-src 'none'; base-uri 'none'; frame-ancestors 'none'";
    }

    public NeoResourceResponse? GetResponse(NeoResourceRequest request)
    {
        var path = request.Uri.AbsolutePath;
        if (path != "/" && path != "/index.html" && Path.HasExtension(path)) return _files.GetResponse(request);
        return NeoResourceResponse.FromBytes(File.ReadAllBytes(_index), "text/html", new Dictionary<string, string>
        {
            ["Content-Security-Policy"] = ContentSecurityPolicy(server()), ["Cache-Control"] = "no-cache", ["X-Content-Type-Options"] = "nosniff"
        });
    }
}
