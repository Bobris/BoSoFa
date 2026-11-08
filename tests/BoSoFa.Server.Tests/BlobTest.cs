using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Tests;

public class BlobTest(Azurite azurite) : IClassFixture<Azurite>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlobsAreStoredByTheirHash(bool onAzure)
    {
        var root = Path.Combine(Path.GetTempPath(), "bosofa-blob-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var server = onAzure
                ? await TestServer.StartClusterNodeAsync(root, "node-1", azurite.ConnectionString, "blobs-" + Guid.NewGuid().ToString("N")[..12])
                : await TestServer.StartClusterNodeAsync(root, "node-1");
            using var boris = server.Client();
            (await boris.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("boris@example.com", null))).EnsureSuccessStatusCode();

            var content = RandomNumberGenerator.GetBytes(300_000);
            var expected = Convert.ToHexStringLower(SHA256.HashData(content));
            var uploaded = await boris.PostAsync("/api/v1/blobs", new ByteArrayContent(content));
            Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
            Assert.Equal(new BlobRef(expected, content.Length), await uploaded.Content.ReadFromJsonAsync<BlobRef>());
            Assert.Equal(content, await boris.GetByteArrayAsync($"/api/v1/blobs/{expected}"));

            Assert.Equal(HttpStatusCode.NotFound, (await boris.GetAsync($"/api/v1/blobs/{new string('0', 64)}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await boris.GetAsync("/api/v1/blobs/not-a-hash")).StatusCode);
            using var anonymous = server.Client();
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/blobs/{expected}")).StatusCode);
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch (IOException) { }
        }
    }
}
