using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace NeoAstraHello;

/// <summary>
/// The desktop's own connection to one BoSoFa server: an HttpClient whose cookie jar holds the server's HttpOnly
/// auth cookie. The page never sees the cookie; both proxies (the app:// scheme and the <c>server</c> RPC service)
/// send their requests through here, so they share one signed-in session.
/// </summary>
sealed class ServerSession : IDisposable
{
    readonly HttpClient _http;

    public ServerSession(Uri origin)
    {
        Origin = origin;
        Cookies = new CookieContainer();
        _http = new HttpClient(new SocketsHttpHandler
        {
            CookieContainer = Cookies, UseCookies = true, AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        }) { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(30) };
    }

    public Uri Origin { get; }
    public CookieContainer Cookies { get; }

    public sealed record Response(int Status, string? ContentType, byte[] Body, string? Location);

    static readonly HashSet<string> ForwardedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "accept", "accept-language", "if-none-match", "if-modified-since", "authorization" };

    static HttpRequestMessage Create(string method, string pathAndQuery, IReadOnlyDictionary<string, string>? headers,
        ReadOnlyMemory<byte> body, string? contentType)
    {
        if (!pathAndQuery.StartsWith('/') || pathAndQuery.StartsWith("//"))
            throw new ArgumentException("Only server-relative paths are proxied.", nameof(pathAndQuery));
        var request = new HttpRequestMessage(new HttpMethod(method), pathAndQuery);
        if (headers is not null)
            foreach (var (name, value) in headers)
                if (ForwardedRequestHeaders.Contains(name)) request.Headers.TryAddWithoutValidation(name, value);
        if (!body.IsEmpty || method is "POST" or "PUT" or "PATCH")
        {
            request.Content = new ByteArrayContent(body.ToArray());
            if (contentType is { Length: > 0 }) request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }
        return request;
    }

    static Response Read(HttpResponseMessage response, byte[] body) => new((int)response.StatusCode,
        response.Content.Headers.ContentType?.ToString(), body, response.Headers.Location?.ToString());

    /// <summary>Synchronous send, for the custom-scheme provider, whose callback NeoAstra calls synchronously.</summary>
    public Response Send(string method, string pathAndQuery, IReadOnlyDictionary<string, string>? headers,
        ReadOnlyMemory<byte> body, string? contentType)
    {
        using var request = Create(method, pathAndQuery, headers, body, contentType);
        using var response = _http.Send(request);
        using var stream = response.Content.ReadAsStream();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Read(response, buffer.ToArray());
    }

    public async Task<Response> SendAsync(string method, string pathAndQuery, IReadOnlyDictionary<string, string>? headers,
        ReadOnlyMemory<byte> body, string? contentType, CancellationToken cancellation)
    {
        using var request = Create(method, pathAndQuery, headers, body, contentType);
        using var response = await _http.SendAsync(request, cancellation);
        return Read(response, await response.Content.ReadAsByteArrayAsync(cancellation));
    }

    public async Task<int> DevLoginAsync(string email)
    {
        var json = $$"""{"email":"{{email}}","name":"Desktop {{email.Split('@')[0]}}"}""";
        var response = await SendAsync("POST", "/auth/dev-login", null, Encoding.UTF8.GetBytes(json), "application/json", default);
        return response.Status;
    }

    public void Dispose() => _http.Dispose();
}
