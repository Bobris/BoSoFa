using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BoSoFa.Protocol;
using Xunit;

namespace BoSoFa.Server.Tests;

/// <summary>What the AKS deployment relies on from the server itself.</summary>
public class ProductionHostingTest
{
    static string TempDirectory() => Path.Combine(Path.GetTempPath(), "bosofa-hosting-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ProbesAnswer()
    {
        await using var server = await TestServer.StartAsync();
        using var client = server.Client();
        await server.Node.Ready;
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task ForwardedSchemeMakesTheCookieSecureOnlyWhenTrusted()
    {
        async Task<string> CookieAsync(params string[] args)
        {
            await using var server = await TestServer.StartAsync(extraArgs: args);
            using var client = server.Client();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/dev-login")
            {
                Content = JsonContent.Create(new DevLoginRequest("a@example.com", "A"))
            };
            request.Headers.Add("X-Forwarded-Proto", "https");
            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return string.Join(";", response.Headers.GetValues("Set-Cookie")).ToLowerInvariant();
        }

        Assert.DoesNotContain("secure", await CookieAsync());
        Assert.Contains("secure", await CookieAsync("--BoSoFa:TrustForwardedHeaders", "true"));
    }

    [Fact]
    public async Task PeerRoutesIgnoreAPeerPortInTheHostHeaderOnThePublicPort()
    {
        var root = TempDirectory();
        await using var node = await TestServer.StartClusterNodeAsync(root, "node-1");
        await node.Node.Ready;
        using var client = node.Client();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/machines/1/rpc");
        request.Headers.Host = $"localhost:{node.PeerPort}";
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(request)).StatusCode);

        using var peer = new HttpClient { BaseAddress = new($"http://127.0.0.1:{node.PeerPort}") };
        Assert.Equal(HttpStatusCode.Unauthorized, (await peer.PostAsync("/internal/machines/1/rpc", null)).StatusCode);
    }

    [Fact]
    public async Task AnEndpointFromConfigurationServesHttps()
    {
        var directory = TempDirectory();
        Directory.CreateDirectory(directory);
        using var key = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var certificatePath = Path.Combine(directory, "tls.crt");
        var keyPath = Path.Combine(directory, "tls.key");
        await File.WriteAllTextAsync(certificatePath, certificate.ExportCertificatePem());
        await File.WriteAllTextAsync(keyPath, key.ExportPkcs8PrivateKeyPem());
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        await using var server = await TestServer.StartAsync(extraArgs:
        [
            "--Kestrel:Endpoints:Peer:Url", $"https://127.0.0.1:{port}",
            "--Kestrel:Endpoints:Peer:Certificate:Path", certificatePath,
            "--Kestrel:Endpoints:Peer:Certificate:KeyPath", keyPath
        ]);
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, presented, _, _) => presented!.Thumbprint == certificate.Thumbprint
        };
        using var client = new HttpClient(handler);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"https://127.0.0.1:{port}/health/live")).StatusCode);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("UseDevelopmentStorage=true", "https://account.blob.core.windows.net")]
    public void AzureStorageNeedsExactlyOneWayToConnect(string connectionString, string serviceUri)
    {
        var options = new ServerOptions
        {
            Cluster = new() { Azure = new() { ConnectionString = connectionString, ServiceUri = serviceUri } }
        };
        Assert.Throws<InvalidOperationException>(() => Btdb.NodeStorage.Create(options, TempDirectory(), TempDirectory()));
    }

    [Fact]
    public async Task TelemetryIsExportedOverOtlpWhenConfigured()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var collector = new HttpListener();
        collector.Prefixes.Add($"http://127.0.0.1:{port}/");
        collector.Start();
        var paths = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new HashSet<string>();
        _ = Task.Run(async () =>
        {
            while (collector.IsListening)
            {
                HttpListenerContext context;
                try { context = await collector.GetContextAsync(); }
                catch (HttpListenerException) { return; }
                catch (ObjectDisposedException) { return; }
                lock (received)
                {
                    received.Add(context.Request.Url!.AbsolutePath);
                    if (received.Contains("/v1/traces") && received.Contains("/v1/metrics")) paths.TrySetResult("both");
                }
                context.Response.StatusCode = 200;
                context.Response.Close();
            }
        });

        await using var server = await TestServer.StartAsync(extraArgs:
        [
            "--OTEL_EXPORTER_OTLP_ENDPOINT", $"http://127.0.0.1:{port}",
            "--OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf",
            "--OTEL_BSP_SCHEDULE_DELAY", "200",
            "--OTEL_METRIC_EXPORT_INTERVAL", "200"
        ]);
        using var client = server.Client();
        for (var attempt = 0; attempt < 100 && !paths.Task.IsCompleted; attempt++)
        {
            await client.GetAsync("/api/v1/nothing-here");
            await Task.Delay(100);
        }
        Assert.Equal("both", await paths.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
