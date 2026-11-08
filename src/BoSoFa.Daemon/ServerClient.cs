using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
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

    public Uri Server => server;

    public async Task<VersionInfo> GetVersionAsync(CancellationToken cancellation) =>
        await _http.GetFromJsonAsync("/api/v1/version", ProtocolJsonContext.Default.VersionInfo, cancellation)
        ?? throw new HttpRequestException("The server returned no version.");

    public async Task<EnrollStartResponse> StartEnrollmentAsync(EnrollStartRequest request, CancellationToken cancellation)
    {
        using var response = await _http.PostAsJsonAsync("/api/daemon/v1/enroll/start", request,
            ProtocolJsonContext.Default.EnrollStartRequest, cancellation);
        await EnsureSuccessAsync(response, cancellation);
        return (await response.Content.ReadFromJsonAsync(ProtocolJsonContext.Default.EnrollStartResponse, cancellation))!;
    }

    public async Task<EnrollPollResponse> PollEnrollmentAsync(string enrollmentId, CancellationToken cancellation)
    {
        using var response = await _http.PostAsJsonAsync("/api/daemon/v1/enroll/poll", new EnrollPollRequest(enrollmentId),
            ProtocolJsonContext.Default.EnrollPollRequest, cancellation);
        if (response.StatusCode == HttpStatusCode.NotFound) return new(EnrollStatus.Expired, null);
        await EnsureSuccessAsync(response, cancellation);
        return (await response.Content.ReadFromJsonAsync(ProtocolJsonContext.Default.EnrollPollResponse, cancellation))!;
    }

    /// <summary>A short-lived bearer token, proving the machine identity with a signature by its key.</summary>
    public async Task<DaemonTokenResponse> GetTokenAsync(ulong machineId, ECDsa key, CancellationToken cancellation)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var signature = key.SignData(Encoding.UTF8.GetBytes(DaemonToken.SignedText(machineId, timestamp, nonce)),
            HashAlgorithmName.SHA256);
        using var response = await _http.PostAsJsonAsync("/api/daemon/v1/token",
            new DaemonTokenRequest(machineId, timestamp, nonce, Convert.ToBase64String(signature)),
            ProtocolJsonContext.Default.DaemonTokenRequest, cancellation);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException("The server does not accept this machine's key; enroll it again.");
        await EnsureSuccessAsync(response, cancellation);
        return (await response.Content.ReadFromJsonAsync(ProtocolJsonContext.Default.DaemonTokenResponse, cancellation))!;
    }

    static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellation);
        throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}: {body}", null, response.StatusCode);
    }

    public void Dispose() => _http.Dispose();
}
