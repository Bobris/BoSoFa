using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace BoSoFa.Server.Tests;

/// <summary>A real server on a loopback port over its own data directory, which outlives restarts of the server.</summary>
sealed class TestServer : IAsyncDisposable
{
    WebApplication _app = null!;

    readonly string[] _extraArgs;
    readonly Uri? _peerUrl;

    TestServer(string dataDirectory, string[]? extraArgs = null, Uri? peerUrl = null)
    {
        DataDirectory = dataDirectory;
        _extraArgs = extraArgs ?? [];
        _peerUrl = peerUrl;
    }

    public string DataDirectory { get; }
    public int PeerPort => _peerUrl!.Port;
    public Uri BaseAddress { get; private set; } = null!;
    public BoSoFa.Server.Btdb.BtdbNode Node => _app.Services.GetRequiredService<BoSoFa.Server.Btdb.BtdbNode>();
    bool _running;

    public static async Task<TestServer> StartAsync(string? dataDirectory = null, string[]? extraArgs = null)
    {
        var server = new TestServer(dataDirectory ?? Path.Combine(Path.GetTempPath(), "bosofa-test-" + Guid.NewGuid().ToString("N")), extraArgs);
        await server.StartAppAsync();
        return server;
    }

    /// <summary>One node of a cluster below <paramref name="root"/>: its data in its own directory, the shared state in
    /// Azure Blob when <paramref name="azure"/> is a connection string, else in a directory all nodes of this process
    /// share.</summary>
    public static async Task<TestServer> StartClusterNodeAsync(string root, string nodeId, string? azure = null,
        string container = "bosofa")
    {
        var peerUrl = new Uri($"http://127.0.0.1:{FreePort()}");
        string[] storage = azure != null
            ? ["--BoSoFa:Cluster:Azure:ConnectionString", azure, "--BoSoFa:Cluster:Azure:Container", container]
            : ["--BoSoFa:Cluster:SharedDirectory", Path.Combine(root, "shared")];
        var server = new TestServer(Path.Combine(root, nodeId),
        [
            "--BoSoFa:Cluster:NodeId", nodeId, "--BoSoFa:Cluster:PeerUrl", peerUrl.ToString().TrimEnd('/'),
            "--BoSoFa:Cluster:PeerKey", "test-peer-key", ..storage
        ], peerUrl);
        await server.StartAppAsync();
        return server;
    }

    static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Starts again on the same port after <see cref="StopAsync"/>, so daemons find it where they left it.</summary>
    public async Task StartAppAsync()
    {
        var main = BaseAddress == null ? "http://127.0.0.1:0" : BaseAddress.GetLeftPart(UriPartial.Authority);
        var urls = _peerUrl == null ? main : $"{main};{_peerUrl.GetLeftPart(UriPartial.Authority)}";
        _app = ServerApp.Build(["--urls", urls, "--environment", "Development",
            "--BoSoFa:DataDirectory", DataDirectory, "--BoSoFa:DevLogin", "true", "--BoSoFa:FlushToDisk", "false",
            "--BoSoFa:WebRoot", Path.Combine(DataDirectory, "no-web-root"), .._extraArgs]);
        await _app.StartAsync();
        var peerAuthority = _peerUrl?.GetLeftPart(UriPartial.Authority);
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => peerAuthority == null || !a.StartsWith(peerAuthority, StringComparison.Ordinal));
        BaseAddress = new(address);
        _running = true;
    }

    public async Task StopAsync()
    {
        _running = false;
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        await StartAppAsync();
    }

    /// <summary>A browser-like client: its own cookie jar, no redirects followed.</summary>
    public HttpClient Client(CookieContainer? cookies = null) =>
        new(new HttpClientHandler { CookieContainer = cookies ?? new CookieContainer(), AllowAutoRedirect = false })
    {
        BaseAddress = BaseAddress, Timeout = TimeSpan.FromSeconds(30)
    };

    public async ValueTask DisposeAsync()
    {
        if (_running) await StopAsync();
        try { Directory.Delete(DataDirectory, true); }
        catch (IOException) { }
    }
}
