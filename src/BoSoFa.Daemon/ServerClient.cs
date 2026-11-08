using System.Net.Http.Json;
using BoSoFa.Protocol;

namespace BoSoFa.Daemon;

/// <summary>The daemon only ever dials out to the server over HTTP(S). Serialization uses the source-generated
/// <see cref="ProtocolJsonContext"/>, so the daemon stays trimmable and Native AOT compatible.</summary>
public sealed class ServerClient(Uri server, HttpMessageHandler? handler = null) : IDisposable
{
    readonly HttpClient _http = new(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        BaseAddress = server, Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders = { { "User-Agent", $"bosofad/{Cli.Version}" } }
    };

    public async Task<VersionInfo> GetVersionAsync(CancellationToken cancellation) =>
        await _http.GetFromJsonAsync("/api/v1/version", ProtocolJsonContext.Default.VersionInfo, cancellation)
        ?? throw new HttpRequestException("The server returned no version.");

    public void Dispose() => _http.Dispose();
}
