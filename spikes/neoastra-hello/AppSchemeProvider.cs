using System.Diagnostics;
using NeoAstra;

namespace NeoAstraHello;

/// <summary>
/// The provider of the app:// scheme (prototype c, request interception): the server's routes (<c>/api</c>,
/// <c>/auth</c>, <c>/health</c>, <c>/openapi</c>) are forwarded to the BoSoFa server through <see cref="ServerSession"/>,
/// so the unmodified SPA, which calls them same-origin, works inside the desktop. Everything else is the page.
/// </summary>
/// <remarks>
/// Limits found in NeoAstra 1.12.0: <see cref="INeoResourceProvider.GetResponse"/> is synchronous and runs on the
/// native UI thread (the request blocks the UI while it waits for the server); the public
/// <see cref="NeoResourceResponse"/> factories make a body only with status 200 (other statuses are empty); responses
/// are buffered (no streaming, no server-sent events) and WebSockets never reach a custom scheme.
/// </remarks>
sealed class AppSchemeProvider(INeoResourceProvider content, ServerSession session) : INeoResourceProvider
{
    static readonly string[] ServerPrefixes = ["/api/", "/auth/", "/health", "/openapi"];

    public NeoResourceResponse? GetResponse(NeoResourceRequest request)
    {
        var path = request.Uri.AbsolutePath;
        if (!ServerPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal)))
            return content.GetResponse(request);

        var clock = Stopwatch.StartNew();
        try
        {
            request.Headers.TryGetValue("Content-Type", out var contentType);
            var response = session.Send(request.Method, request.Uri.PathAndQuery, request.Headers, request.Body, contentType);
            Log($"{request.Method} {request.Uri.PathAndQuery} body={request.Body.Length}B → {response.Status} " +
                $"({response.Body.Length}B, {clock.ElapsedMilliseconds} ms, thread {Environment.CurrentManagedThreadId})");
            var headers = new Dictionary<string, string> { ["Cache-Control"] = "no-store" };
            return response.Status == 200
                ? NeoResourceResponse.FromBytes(response.Body, response.ContentType ?? "application/octet-stream", headers)
                : NeoResourceResponse.Empty(response.Status, ReasonPhrase(response.Status));
        }
        catch (Exception exception)
        {
            Log($"{request.Method} {request.Uri.PathAndQuery} failed: {exception.Message}");
            return NeoResourceResponse.Empty(502, "Bad Gateway");
        }
    }

    static string ReasonPhrase(int status) => status switch
    {
        204 => "No Content", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found",
        _ => "Status",
    };

    static void Log(string message) => Console.WriteLine($"[scheme {DateTime.Now:HH:mm:ss.fff}] {message}");
}

/// <summary>
/// Serves a Vite build directory (such as web/apps/web/dist) with an index.html fallback for client routes, and gives
/// the document a CSP whose connect-src names the server origin known only at run time.
/// </summary>
sealed class SpaDirectoryProvider(string root, Uri server) : INeoResourceProvider
{
    readonly NeoDirectoryResourceProvider _files = new(root);
    readonly string _index = Path.Combine(root, "index.html");
    readonly string _csp = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        $"font-src 'self'; connect-src 'self' {server.GetLeftPart(UriPartial.Authority)} " +
        $"ws{server.GetLeftPart(UriPartial.Authority)[4..]}; object-src 'none'; base-uri 'none'; frame-ancestors 'none'";

    public NeoResourceResponse? GetResponse(NeoResourceRequest request)
    {
        var path = request.Uri.AbsolutePath;
        var isDocument = path == "/" || path == "/index.html" || !Path.HasExtension(path);
        if (!isDocument) return _files.GetResponse(request);
        return NeoResourceResponse.FromBytes(File.ReadAllBytes(_index), "text/html", new Dictionary<string, string>
        {
            ["Content-Security-Policy"] = _csp, ["Cache-Control"] = "no-cache", ["X-Content-Type-Options"] = "nosniff",
        });
    }
}
