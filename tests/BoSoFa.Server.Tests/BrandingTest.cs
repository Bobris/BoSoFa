using System.Net.Http.Json;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Tests;

/// <summary>White labeling: the build's brand names the binaries and folders, and the server's configuration can rename
/// the product in the UI. Neither needs a sign-in to read.</summary>
public class BrandingTest
{
    [Fact]
    public async Task DefaultsToTheBuildsBrand()
    {
        await using var server = await TestServer.StartAsync();
        using var anonymous = server.Client();
        Assert.Equal(new Branding(Brand.Name, Brand.FullName, Brand.Daemon), await anonymous.GetFromJsonAsync<Branding>("/api/v1/branding"));
    }

    [Fact]
    public async Task TheConfigurationRenamesItAndCanDropTheTagline()
    {
        await using var server = await TestServer.StartAsync(extraArgs: ["--BoSoFa:Branding:Name= Acme Forge ", "--BoSoFa:Branding:Tagline="]);
        using var anonymous = server.Client();
        Assert.Equal(new Branding("Acme Forge", "", Brand.Daemon), await anonymous.GetFromJsonAsync<Branding>("/api/v1/branding"));
    }

    [Theory]
    [InlineData("daemon.sh")]
    [InlineData("daemon.ps1")]
    [InlineData("desktop.sh")]
    [InlineData("desktop.ps1")]
    public async Task InstallScriptsUseTheBrandsNames(string script)
    {
        await using var server = await TestServer.StartAsync();
        using var anonymous = server.Client();
        var text = await anonymous.GetStringAsync($"/install/{script}");
        Assert.DoesNotContain("__BRAND", text);
        Assert.DoesNotContain("__BOSOFA", text);
        Assert.Contains(script.StartsWith("daemon") ? Brand.Daemon : Brand.Desktop, text);
    }

    [Fact]
    public void DerivedNamesFollowTheBrand()
    {
        Assert.Equal(Brand.Daemon, Releases.FileName(Releases.Daemon, "linux-x64"));
        Assert.Equal(Brand.Daemon + ".exe", Releases.FileName(Releases.Daemon, "win-x64"));
        Assert.Equal($"{Brand.Id}-desktop-osx-arm64.tar.gz", Releases.FileName(Releases.Desktop, "osx-arm64"));
        Assert.Equal($"com.{Brand.Id}.daemon", Brand.LaunchdLabel);
        Assert.EndsWith(OperatingSystem.IsLinux() ? Brand.Id : Brand.Name, Brand.DataFolder());
    }
}
