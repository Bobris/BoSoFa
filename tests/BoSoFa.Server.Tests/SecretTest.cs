using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using BoSoFa.Protocol;
using BoSoFa.Server.Domain;
using BoSoFa.Server.Secrets;
using BTDB.Replication.EventSourcing;
using Microsoft.AspNetCore.DataProtection;

namespace BoSoFa.Server.Tests;

public class SecretTest
{
    const string Grafana = "glsa_FirstGrafanaToken_0123456789", Rotated = "glsa_RotatedGrafanaToken_9876543210",
        AlicesOwn = "glsa_AlicesOwnGrafanaToken_ABCDEF", Team = "glsa_TheTeamsGrafanaToken_GHIJKL";

    static async Task<HttpClient> SignIn(TestServer server, string email)
    {
        var client = server.Client();
        (await client.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest(email, null))).EnsureSuccessStatusCode();
        return client;
    }

    static Task<HttpResponseMessage> Set(HttpClient client, SecretScope scope, string name, SecretKind kind, string value) =>
        client.PutAsJsonAsync($"/api/v1/secrets/{scope}/{name}", new SetSecretRequest(kind, value));

    static async Task<SecretDto> SetOk(HttpClient client, SecretScope scope, string name, SecretKind kind, string value)
    {
        var response = await Set(client, scope, name, kind, value);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<SecretDto>())!;
    }

    static async Task<SecretDto[]> List(HttpClient client) => (await client.GetFromJsonAsync<SecretDto[]>("/api/v1/secrets"))!;

    static SecretEntry Row(TestServer server, ulong ownerId, string name)
    {
        using var query = server.Node.Publisher!.StartQuery();
        return Ambient.Relation<ISecretTable>().FindByIdOrDefault(ownerId, name)!;
    }

    [Fact]
    public async Task SecretsAreWriteOnlyScopedAndBoundToTheirVersion()
    {
        await using var server = await TestServer.StartAsync();
        using var boris = await SignIn(server, "boris@example.com"); // the first person is the admin
        using var alice = await SignIn(server, "alice@example.com");
        var borisId = (await boris.GetFromJsonAsync<UserDto>("/api/v1/me"))!.Id;

        var set = await SetOk(boris, SecretScope.Personal, "GRAFANA_TOKEN", SecretKind.Secret, "  " + Grafana + "\n");
        Assert.Equal((SecretKind.Secret, SecretScope.Personal, 1, "…6789", null), (set.Kind, set.Scope, set.Version, set.Hint, set.Value));
        Assert.Equal("boris@example.com", set.SetBy); // a dev login is named by its email
        var rotated = await SetOk(boris, SecretScope.Personal, "GRAFANA_TOKEN", SecretKind.Secret, Rotated);
        Assert.Equal(2, rotated.Version);
        var url = await SetOk(boris, SecretScope.Personal, "GRAFANA_URL", SecretKind.Variable, "https://grafana.example");
        Assert.Equal(("https://grafana.example", null), (url.Value, url.Hint)); // a variable is shown

        // A name keeps its kind; names are like environment variables; a value is required.
        Assert.Equal(HttpStatusCode.BadRequest, (await Set(boris, SecretScope.Personal, "GRAFANA_TOKEN", SecretKind.Variable, "x")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Set(boris, SecretScope.Personal, "1BAD", SecretKind.Secret, "x")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Set(boris, SecretScope.Personal, "EMPTY", SecretKind.Secret, "   ")).StatusCode);

        // Personal values are their owner's; shared ones are set by admins and seen by everyone.
        Assert.Equal(HttpStatusCode.Forbidden, (await Set(alice, SecretScope.Shared, "GRAFANA_TOKEN", SecretKind.Secret, Team)).StatusCode);
        await SetOk(alice, SecretScope.Personal, "GRAFANA_TOKEN", SecretKind.Secret, AlicesOwn);
        await SetOk(boris, SecretScope.Shared, "GRAFANA_TOKEN", SecretKind.Secret, Team);
        var alices = await List(alice);
        Assert.Equal([(SecretScope.Personal, "GRAFANA_TOKEN"), (SecretScope.Shared, "GRAFANA_TOKEN")], alices.Select(secret => (secret.Scope, secret.Name)));
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.DeleteAsync("/api/v1/secrets/Shared/GRAFANA_TOKEN")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.DeleteAsync("/api/v1/secrets/Personal/GRAFANA_URL")).StatusCode); // Boris's, not hers
        Assert.Equal(3, (await List(boris)).Length);

        // The value opens for its own secret and version only: a ciphertext moved elsewhere, or replayed as another version, does not.
        var protection = server.Service<IDataProtectionProvider>();
        var row = Row(server, borisId, "GRAFANA_TOKEN");
        Assert.Equal(Rotated, SecretEndpoints.ValueOf(row, protection));
        Assert.Throws<CryptographicException>(() => SecretEndpoints.ValueOf(new SecretEntry
        {
            Kind = SecretKind.Secret, SecretId = row.SecretId, Version = 1, ProtectedValue = row.ProtectedValue
        }, protection));
        var team = Row(server, 0, "GRAFANA_TOKEN");
        Assert.Throws<CryptographicException>(() => SecretEndpoints.ValueOf(new SecretEntry
        {
            Kind = SecretKind.Secret, SecretId = team.SecretId, Version = row.Version, ProtectedValue = row.ProtectedValue
        }, protection));
        using (var query = server.Node.Publisher!.StartQuery())
            Assert.Equal([1], Ambient.Relation<ISecretVersionTable>().ListById(row.SecretId).Select(version => version.Version)); // kept for masking

        // Deleting keeps the replaced value for masking and frees the name, which may then become a variable.
        Assert.Equal(HttpStatusCode.NoContent, (await boris.DeleteAsync("/api/v1/secrets/Personal/GRAFANA_TOKEN")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await boris.DeleteAsync("/api/v1/secrets/Personal/GRAFANA_TOKEN")).StatusCode);
        using (var query = server.Node.Publisher!.StartQuery())
            Assert.Equal([1, 2], Ambient.Relation<ISecretVersionTable>().ListById(row.SecretId).Select(version => version.Version).Order());
        Assert.Equal(1, (await SetOk(boris, SecretScope.Personal, "GRAFANA_TOKEN", SecretKind.Variable, "not secret any more")).Version);

        var audit = await boris.GetStringAsync("/api/v1/audit");
        Assert.Contains("secret.rotated", audit);
        Assert.Contains("secret.deleted", audit);

        // No value of a secret leaves the server or lies in its data in the clear.
        var everything = await boris.GetStringAsync("/api/v1/secrets") + await alice.GetStringAsync("/api/v1/secrets") + audit;
        foreach (var value in new[] { Grafana, Rotated, AlicesOwn, Team })
        {
            Assert.DoesNotContain(value, everything);
            var needle = Encoding.UTF8.GetBytes(value);
            foreach (var file in Directory.EnumerateFiles(server.DataDirectory, "*", SearchOption.AllDirectories))
            {
                byte[] content;
                try { content = await File.ReadAllBytesAsync(file); }
                catch (IOException) { continue; }
                Assert.True(content.AsSpan().IndexOf(needle) < 0, $"{file} holds a secret in the clear");
            }
        }
    }

    [Fact]
    public async Task ReplacedValuesAreKeptForAWhileOnly()
    {
        await using var server = await TestServer.StartAsync();
        using var boris = await SignIn(server, "boris@example.com");
        for (var version = 1; version <= BoSoFa.Server.Domain.Secrets.RetainedVersions + 3; version++)
            await SetOk(boris, SecretScope.Shared, "TOKEN", SecretKind.Secret, $"value-number-{version:D4}");
        var row = Row(server, 0, "TOKEN");
        using var query = server.Node.Publisher!.StartQuery();
        var kept = Ambient.Relation<ISecretVersionTable>().ListById(row.SecretId).Select(version => version.Version).ToArray();
        Assert.Equal(BoSoFa.Server.Domain.Secrets.RetainedVersions, kept.Length);
        Assert.Equal(row.Version - 1, kept.Max());
    }
}
