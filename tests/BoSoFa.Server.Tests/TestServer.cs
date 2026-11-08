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

    TestServer(string dataDirectory) => DataDirectory = dataDirectory;

    public string DataDirectory { get; }
    public Uri BaseAddress { get; private set; } = null!;

    public static async Task<TestServer> StartAsync(string? dataDirectory = null)
    {
        var server = new TestServer(dataDirectory ?? Path.Combine(Path.GetTempPath(), "bosofa-test-" + Guid.NewGuid().ToString("N")));
        await server.StartAppAsync();
        return server;
    }

    async Task StartAppAsync()
    {
        _app = ServerApp.Build(["--urls", "http://127.0.0.1:0", "--environment", "Development",
            "--BoSoFa:DataDirectory", DataDirectory, "--BoSoFa:DevLogin", "true",
            "--BoSoFa:WebRoot", Path.Combine(DataDirectory, "no-web-root")]);
        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        BaseAddress = new(address);
    }

    public async Task RestartAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        await StartAppAsync();
    }

    /// <summary>A browser-like client: its own cookie jar, no redirects followed.</summary>
    public HttpClient Client() => new(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false })
    {
        BaseAddress = BaseAddress, Timeout = TimeSpan.FromSeconds(30)
    };

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        try { Directory.Delete(DataDirectory, true); }
        catch (IOException) { }
    }
}
