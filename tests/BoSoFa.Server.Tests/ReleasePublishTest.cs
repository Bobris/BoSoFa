using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using BoSoFa.Daemon.Service;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Tests;

/// <summary><c>bosofad release-publish</c> as the release workflow runs it: the key's PEM text in an environment variable.
/// The variables are process-wide, so every test that sets them is in this class and runs one at a time.</summary>
public class ReleasePublishTest
{
    static async Task<(TestServer Server, string File)> StartAsync()
    {
        var server = await TestServer.StartAsync();
        using var admin = await ReleaseFixture.AdminAsync(server);
        var token = (await (await admin.PostAsJsonAsync("/api/v1/tokens", new CreateApiTokenRequest("release", ApiTokenScope.Control)))
            .Content.ReadFromJsonAsync<CreatedApiToken>())!.Token;
        Environment.SetEnvironmentVariable("BOSOFA_TOKEN", token);
        var file = Path.Combine(server.DataDirectory, "bosofad");
        await File.WriteAllTextAsync(file, "a daemon build");
        return (server, file);
    }

    static async Task<(int Code, string Error)> PublishAsync(TestServer server, string file, string? keyFile, string? trustedKey)
    {
        var error = new StringWriter();
        var code = await ReleaseCommands.PublishAsync(server.BaseAddress, keyFile, trustedKey, Releases.Daemon, "1.0.0", Releases.Stable,
            $"linux-x64={file}", TextWriter.Null, error, CancellationToken.None);
        return (code, error.ToString());
    }

    static async Task<ProductDownload[]> DownloadsAsync(TestServer server)
    {
        using var anonymous = server.Client();
        return (await anonymous.GetFromJsonAsync<ProductDownload[]>("/api/v1/downloads"))!;
    }

    [Fact]
    public async Task TheKeyComesFromTheEnvironmentAndMustBeTheTrustedOne()
    {
        var (server, file) = await StartAsync();
        await using var _ = server;
        try
        {
            using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Environment.SetEnvironmentVariable(ReleaseCommands.KeyVariable, ReleaseFixture.Key.ExportECPrivateKeyPem());
            var (code, error) = await PublishAsync(server, file, null, Convert.ToBase64String(stranger.ExportSubjectPublicKeyInfo()));
            Assert.Equal(2, code);
            Assert.Contains("does not match", error);
            Assert.Empty(await DownloadsAsync(server));

            (code, error) = await PublishAsync(server, file, null, ReleaseFixture.PublicKey);
            Assert.True(code == 0, error);
            var daemon = Assert.Single(await DownloadsAsync(server));
            Assert.Equal(("1.0.0", "linux-x64"), (daemon.Version, Assert.Single(daemon.Files).Rid));
            using var anonymous = server.Client();
            Assert.Equal("a daemon build", await anonymous.GetStringAsync("/api/v1/downloads/daemon/latest/linux-x64"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ReleaseCommands.KeyVariable, null);
            Environment.SetEnvironmentVariable("BOSOFA_TOKEN", null);
        }
    }

    [Fact]
    public async Task AMissingOrBrokenKeyPublishesNothing()
    {
        var (server, file) = await StartAsync();
        await using var _ = server;
        try
        {
            Environment.SetEnvironmentVariable(ReleaseCommands.KeyVariable, null);
            var (code, error) = await PublishAsync(server, file, null, null);
            Assert.Equal(2, code);
            Assert.Contains(ReleaseCommands.KeyVariable, error);

            Environment.SetEnvironmentVariable(ReleaseCommands.KeyVariable, "-----BEGIN EC PRIVATE KEY-----\nnot a key\n-----END EC PRIVATE KEY-----");
            (code, error) = await PublishAsync(server, file, null, null);
            Assert.Equal(2, code);
            Assert.Contains("not an EC P-256 private key", error);

            using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
            var keyFile = Path.Combine(server.DataDirectory, "p384.pem");
            await File.WriteAllTextAsync(keyFile, p384.ExportECPrivateKeyPem());
            (code, error) = await PublishAsync(server, file, keyFile, null);
            Assert.Equal(2, code);
            Assert.Contains("not an EC P-256 private key", error);
            Assert.Empty(await DownloadsAsync(server));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ReleaseCommands.KeyVariable, null);
            Environment.SetEnvironmentVariable("BOSOFA_TOKEN", null);
        }
    }
}
