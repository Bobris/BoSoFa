using System.Net.Http.Json;

namespace BoSoFa.Server.Tests;

/// <summary>White labeling: the names the UI shows come from the server's configuration and need no sign-in.</summary>
public class BrandingTest
{
    [Fact]
    public async Task DefaultsToBoSoFa()
    {
        await using var server = await TestServer.StartAsync();
        using var anonymous = server.Client();
        Assert.Equal(new Branding("BoSoFa", "Bobris Software Factory"), await anonymous.GetFromJsonAsync<Branding>("/api/v1/branding"));
    }

    [Fact]
    public async Task TheConfigurationRenamesItAndCanDropTheTagline()
    {
        await using var server = await TestServer.StartAsync(extraArgs: ["--BoSoFa:Branding:Name= Acme Forge ", "--BoSoFa:Branding:Tagline="]);
        using var anonymous = server.Client();
        Assert.Equal(new Branding("Acme Forge", ""), await anonymous.GetFromJsonAsync<Branding>("/api/v1/branding"));
    }
}
