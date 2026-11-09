using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Agents;

/// <summary>Which addresses the server's own <c>web_fetch</c> may reach: the public internet only (docs/server-agent.md §6.3).</summary>
public static class WebAddresses
{
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return !(bytes[0] is 0 or 10 or 127 or >= 224 ||
                     (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) ||
                     (bytes[0] == 169 && bytes[1] == 254) ||
                     (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                     (bytes[0] == 192 && bytes[1] == 0 && bytes[2] is 0 or 2) ||
                     (bytes[0] == 192 && bytes[1] == 168) ||
                     (bytes[0] == 198 && bytes[1] is 18 or 19) ||
                     (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) ||
                     (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113));
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6Loopback)) return false;
        if ((bytes[0] & 0xFE) == 0xFC || bytes[0] == 0xFF || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)) return false; // unique local, multicast, link-local
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8) return false; // documentation
        if (bytes[0] == 0x20 && bytes[1] == 0x02) return false; // 6to4 can carry a private IPv4 address
        return !(bytes[0] == 0 && bytes[1] == 0x64 && bytes[2] == 0xFF && bytes[3] == 0x9B); // NAT64, the same
    }
}

/// <summary>A fetch that the address rules refuse.</summary>
sealed class WebAddressRefusedException(string message) : Exception(message);

/// <summary>
/// The agent's <c>web_fetch</c>. Addresses are checked where the connection is made, so that redirects and names that resolve differently the second
/// time pass the same check, and the server connects to the very address it checked.
/// </summary>
public sealed partial class WebFetcher : IDisposable
{
    const int MaximumBytes = 5 * 1024 * 1024;
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    readonly HttpClient _http;
    readonly bool _allowPrivateNetworks;

    public WebFetcher(bool allowPrivateNetworks)
    {
        _allowPrivateNetworks = allowPrivateNetworks;
        _http = new(new SocketsHttpHandler
        {
            AllowAutoRedirect = true, MaxAutomaticRedirections = 5, UseCookies = false, UseProxy = false, AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(1), ConnectCallback = ConnectAsync
        }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellation)
    {
        var (host, port) = (context.DnsEndPoint.Host, context.DnsEndPoint.Port);
        if (!_allowPrivateNetworks && port is not (80 or 443)) throw new WebAddressRefusedException("Only the usual web ports can be fetched.");
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, cancellation);
        // All of them: a name that also resolves to a private address is not one to trust.
        if (addresses.Length == 0 || (!_allowPrivateNetworks && !addresses.All(WebAddresses.IsPublic)))
            throw new WebAddressRefusedException($"{host} is not on the public internet, so it cannot be fetched.");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, port, cancellation);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>The address as far as it is safe to keep in the audit log: a query can hold secrets.</summary>
    public static string ForAudit(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Path) : "(not an address)";

    public async Task<McpToolResult> FetchAsync(string url, int offset, CancellationToken cancellation)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return new("Only http and https addresses can be fetched.", true);
        if (uri.UserInfo.Length > 0) return new("An address with a user name or password cannot be fetched.", true);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(Timeout);
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, uri);
            message.Headers.TryAddWithoutValidation("User-Agent", ProviderHttp.UserAgent);
            message.Headers.TryAddWithoutValidation("Accept", "text/html, text/plain, application/json, */*;q=0.5");
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return new($"The server answered {(int)response.StatusCode}.", true);
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!IsText(mediaType)) return new($"This is {(mediaType.Length == 0 ? "not text" : mediaType)}, which this tool cannot show.", true);
            var (bytes, complete) = await ReadCappedAsync(response, timeout.Token);
            var text = Decode(bytes, response.Content.Headers.ContentType?.CharSet);
            string? title = null;
            if (mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                title = TitleOf(text);
                text = ToMarkdown(text);
            }
            return new(Page(response.RequestMessage?.RequestUri ?? uri, title, text, offset, complete), false);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return new($"The server did not answer within {Timeout.TotalSeconds:0} seconds.", true); }
        catch (HttpRequestException problem) when (FindRefusal(problem) is { } refusal) { return new(refusal.Message, true); }
        catch (HttpRequestException problem) { return new($"The page could not be fetched: {problem.Message}", true); }
    }

    static WebAddressRefusedException? FindRefusal(Exception? problem)
    {
        for (; problem != null; problem = problem.InnerException)
            if (problem is WebAddressRefusedException refusal) return refusal;
        return null;
    }

    static bool IsText(string mediaType) => mediaType.Length == 0 || mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
                                            mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase);

    static async Task<(byte[] Bytes, bool Complete)> ReadCappedAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        var buffer = new byte[MaximumBytes];
        var count = await stream.ReadAtLeastAsync(buffer, buffer.Length, false, cancellation);
        return (buffer[..count], count < buffer.Length);
    }

    static string Decode(byte[] bytes, string? charset)
    {
        var encoding = Encoding.UTF8;
        if (!string.IsNullOrEmpty(charset))
            try { encoding = Encoding.GetEncoding(charset.Trim('"')); }
            catch (ArgumentException) { /* an unknown name: UTF-8 is the best guess */ }
        return encoding.GetString(bytes);
    }

    static string? TitleOf(string html) =>
        Title().Match(html) is { Success: true } match && Collapse(WebUtility.HtmlDecode(match.Groups[1].Value)) is { Length: > 0 } title ? title : null;

    static string Page(Uri address, string? title, string text, int offset, bool complete)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        var header = title == null ? $"Fetched {address}\n\n" : $"Fetched {address}\nTitle: {title}\n\n";
        var room = MachineTools.OutputLimit - header.Length - 100;
        var part = text.Length - offset > room ? text.Substring(offset, room) : text[offset..];
        var next = offset + part.Length;
        var footer = next < text.Length ? $"\n\n[{text.Length - next} more characters. Continue with offset {next}.]" : complete ? "" : "\n\n[The page was larger than this tool reads.]";
        return header + part + footer;
    }

    /// <summary>Enough of HTML for a model to read: the structure stays, the markup goes.</summary>
    public static string ToMarkdown(string html)
    {
        var text = Hidden().Replace(html, "");
        text = PreBlock().Replace(text, match => $"\n\n```\n{Tags().Replace(match.Groups[1].Value, "")}\n```\n\n");
        text = Heading().Replace(text, match => $"\n\n{new string('#', match.Groups[1].Value[0] - '0')} {Collapse(Tags().Replace(match.Groups[2].Value, ""))}\n\n");
        text = Link().Replace(text, match =>
        {
            var label = Collapse(Tags().Replace(match.Groups["label"].Value, ""));
            return label.Length == 0 ? "" : $"[{label}]({match.Groups["href"].Value})";
        });
        text = Emphasis().Replace(text, match => $"**{match.Groups[2].Value}**");
        text = InlineCode().Replace(text, match => $"`{match.Groups[1].Value}`");
        text = ListItem().Replace(text, "\n- ");
        text = LineBreak().Replace(text, "\n");
        text = Block().Replace(text, "\n\n");
        text = Tags().Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        text = SpacesInLine().Replace(text, " ");
        text = SpaceAroundBreaks().Replace(text, "\n");
        return BlankLines().Replace(text, "\n\n").Trim();
    }

    static string Collapse(string text) => SpacesAll().Replace(text, " ").Trim();

    [GeneratedRegex(@"<!--.*?-->|<(script|style|noscript|svg|head|template|iframe)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Hidden();
    [GeneratedRegex(@"<pre\b[^>]*>(.*?)</pre\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex PreBlock();
    [GeneratedRegex(@"<h([1-6])\b[^>]*>(.*?)</h\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Heading();
    // Minified pages leave the quotes out.
    [GeneratedRegex(@"<a\b[^>]*?href\s*=\s*(?:""(?<href>[^""]*)""|'(?<href>[^']*)'|(?<href>[^\s>]+))[^>]*>(?<label>.*?)</a\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Link();
    [GeneratedRegex(@"<title\b[^>]*>(.*?)</title\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Title();
    [GeneratedRegex(@"<(strong|b)\b[^>]*>(.*?)</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Emphasis();
    [GeneratedRegex(@"<code\b[^>]*>(.*?)</code\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex InlineCode();
    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItem();
    [GeneratedRegex(@"<br\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreak();
    [GeneratedRegex(@"</?(p|div|section|article|main|header|footer|aside|ul|ol|table|tr|blockquote|hr)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Block();
    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();
    [GeneratedRegex(@"[ \t\f\v\r]+")]
    private static partial Regex SpacesInLine();
    [GeneratedRegex(@" ?\n ?")]
    private static partial Regex SpaceAroundBreaks();
    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();
    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacesAll();

    public void Dispose() => _http.Dispose();
}
