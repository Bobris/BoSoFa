using System.Text.Json;
using System.Text.Json.Nodes;

namespace BoSoFa.Server.Tests;

/// <summary>The OpenAPI document checked in at web/packages/api/openapi.json is what the web client is generated
/// from. Run with BOSOFA_UPDATE_CONTRACT=1 to rewrite it, then regenerate the client with `pnpm gen:api`.</summary>
public class ContractTest
{
    static string RepositoryRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(directory, "BoSoFa.slnx")))
            directory = Path.GetDirectoryName(directory) ?? throw new InvalidOperationException("Repository root not found.");
        return directory;
    }

    [Fact]
    public async Task CheckedInOpenApiDocumentMatchesTheServer()
    {
        await using var server = await TestServer.StartAsync();
        using var client = server.Client();
        var document = JsonNode.Parse(await client.GetStringAsync("/openapi/v1.json"))!.AsObject();
        document.Remove("servers"); // the test server's random port
        var actual = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";
        var path = Path.Combine(RepositoryRoot(), "web", "packages", "api", "openapi.json");
        if (Environment.GetEnvironmentVariable("BOSOFA_UPDATE_CONTRACT") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, actual);
            return;
        }
        Assert.True(File.Exists(path), $"Missing {path}; run the tests with BOSOFA_UPDATE_CONTRACT=1.");
        Assert.True(actual == await File.ReadAllTextAsync(path),
            "The API changed: run the tests with BOSOFA_UPDATE_CONTRACT=1 and then `pnpm gen:api` in web/.");
    }
}
