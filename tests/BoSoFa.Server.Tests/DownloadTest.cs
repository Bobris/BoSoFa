using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using BoSoFa.Desktop;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Tests;

/// <summary>What the server's publish leaves beside it: the daemon and desktop builds and <c>bundle.json</c>.</summary>
static class BundleFixture
{
    /// <summary>A bundle folder with these files; their hashes in upper case, as MSBuild writes them.</summary>
    public static string Write(string version, params (string Product, string Rid, byte[] Content)[] files)
    {
        var directory = Directory.CreateTempSubdirectory("bosofa-bundle-").FullName;
        var entries = new List<string>();
        foreach (var (product, rid, content) in files)
        {
            var path = $"{product}/{rid}/{Releases.FileName(product, rid)}";
            Directory.CreateDirectory(Path.Combine(directory, product, rid));
            File.WriteAllBytes(Path.Combine(directory, path), content);
            entries.Add($$"""{"product":"{{product}}","rid":"{{rid}}","path":"{{path}}","sha256":"{{Convert.ToHexString(SHA256.HashData(content))}}"}""");
        }
        File.WriteAllText(Path.Combine(directory, "bundle.json"), $$"""{"version":"{{version}}","files":[{{string.Join(",", entries)}}]}""");
        return directory;
    }

    /// <summary>A server that carries these builds.</summary>
    public static Task<TestServer> StartAsync(string version, params (string Product, string Rid, byte[] Content)[] files) =>
        TestServer.StartAsync(extraArgs: ["--BoSoFa:BundleDirectory", Write(version, files)]);

    public static string FileOf(TestServer server, string product, string rid) => server.Service<BoSoFa.Server.Machines.ClientBundle>().Find(product, rid)!.FullPath;

    /// <summary>A packaged app as the build makes it: one folder with <c>release.json</c> and the executable.</summary>
    public static byte[] AppArchive(string version, string rid, string root, bool zip, string executable = "#!/bin/sh\n")
    {
        var stage = Directory.CreateTempSubdirectory("bosofa-package-").FullName;
        var code = Path.Combine(stage, root);
        Directory.CreateDirectory(Path.Combine(code, "spa"));
        File.WriteAllText(Path.Combine(code, "release.json"), $$"""{"version":"{{version}}","rid":"{{rid}}"}""");
        File.WriteAllText(Path.Combine(code, InstalledApp.ExecutableName), executable);
        File.WriteAllText(Path.Combine(code, "spa", "index.html"), version);
        var archive = Path.Combine(Path.GetTempPath(), "bosofa-" + Guid.NewGuid().ToString("N"));
        if (zip) ZipFile.CreateFromDirectory(stage, archive);
        else
        {
            using var file = File.Create(archive);
            using var compressed = new GZipStream(file, CompressionLevel.Fastest);
            TarFile.CreateFromDirectory(stage, compressed, false);
        }
        var bytes = File.ReadAllBytes(archive);
        File.Delete(archive);
        Directory.Delete(stage, true);
        return bytes;
    }
}

public class DownloadTest
{
    [Fact]
    public async Task TheBundledBuildsCanBeDownloadedWithoutSigningIn()
    {
        await using (var bare = await TestServer.StartAsync())
        using (var nobody = bare.Client())
        {
            Assert.Empty((await nobody.GetFromJsonAsync<ProductDownload[]>("/api/v1/downloads"))!);
            Assert.Equal(HttpStatusCode.NotFound, (await nobody.GetAsync("/api/v1/downloads/daemon/linux-x64")).StatusCode);
        }

        await using var server = await BundleFixture.StartAsync("2.0", (Releases.Daemon, "linux-x64", "linux one"u8.ToArray()),
            (Releases.Daemon, "win-x64", "windows one"u8.ToArray()), (Releases.Desktop, "osx-arm64", "mac app"u8.ToArray()));
        using var anonymous = server.Client();
        var downloads = (await anonymous.GetFromJsonAsync<ProductDownload[]>("/api/v1/downloads"))!;
        var daemon = Assert.Single(downloads, download => download.Product == Releases.Daemon);
        Assert.Equal("2.0", daemon.Version);
        var linux = daemon.Files.Single(file => file.Rid == "linux-x64");
        Assert.Equal((Brand.Daemon, "/api/v1/downloads/daemon/linux-x64", 9L), (linux.FileName, linux.Url, linux.Size));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData("linux one"u8)), linux.Sha256);
        Assert.Equal(Brand.Daemon + ".exe", daemon.Files.Single(file => file.Rid == "win-x64").FileName);
        var desktop = Assert.Single(downloads, download => download.Product == Releases.Desktop);
        Assert.Equal(("2.0", $"{Brand.Desktop}-osx-arm64.tar.gz"), (desktop.Version, desktop.Files.Single().FileName));

        using var file = await anonymous.GetAsync("/api/v1/downloads/daemon/linux-x64");
        Assert.Equal("linux one", await file.Content.ReadAsStringAsync());
        Assert.Equal("2.0", file.Headers.GetValues("X-BoSoFa-Version").Single());
        Assert.Equal(linux.Sha256, file.Headers.GetValues("X-BoSoFa-Sha256").Single());
        Assert.Equal(Brand.Daemon, file.Content.Headers.ContentDisposition?.FileName);
        Assert.Equal("mac app", await anonymous.GetStringAsync("/api/v1/downloads/desktop/osx-arm64"));
        foreach (var missing in new[] { "/api/v1/downloads/daemon/plan9-mips", "/api/v1/downloads/desktop/linux-x64", "/api/v1/downloads/nothing/linux-x64" })
            Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(missing)).StatusCode);
    }

    [Fact]
    public void ABundleNeverReachesOutsideItsFolder()
    {
        var directory = BundleFixture.Write("1.0", (Releases.Daemon, "linux-x64", "x"u8.ToArray()));
        File.WriteAllText(Path.Combine(directory, "bundle.json"), """
            {"version":"1.0","files":[{"product":"daemon","rid":"linux-x64","path":"../secret","sha256":"00"},
            {"product":"other","rid":"linux-x64","path":"daemon/linux-x64/bosofad","sha256":"00"},
            {"product":"daemon","rid":"win-x64","path":"daemon/win-x64/missing.exe","sha256":"00"}]}
            """);
        Assert.Empty(BoSoFa.Server.Machines.ClientBundle.Load(directory).Files);
        File.WriteAllText(Path.Combine(directory, "bundle.json"), """{"version":"../1.0","files":[]}""");
        Assert.Null(BoSoFa.Server.Machines.ClientBundle.Load(directory).Version);
        Assert.Null(BoSoFa.Server.Machines.ClientBundle.Load(Path.Combine(directory, "nothing")).Version);
        Directory.Delete(directory, true);
    }

    [Theory]
    [InlineData("1.1", "1.0", true)]
    [InlineData("1.10", "1.9", true)]
    [InlineData("1.0", "1.0", false)]
    [InlineData("0.9", "1.0", false)]
    [InlineData("1.0", "1.0-beta", true)]
    [InlineData("1.0-beta", "1.0", false)]
    [InlineData("1.0-beta.10", "1.0-beta.9", true)]
    [InlineData("1.0-beta.1", "1.0-alpha.2", true)]
    [InlineData("1.0+def", "1.0+abc", false)] // the same version from another commit
    [InlineData("0.2.0", "0.1.0+d86c23c", true)]
    [InlineData("dev-b", "dev-a", false)]
    public void OnlyNewerVersionsCount(string candidate, string current, bool newer) => Assert.Equal(newer, Releases.IsNewer(candidate, current));

    [Fact]
    public async Task InstallScriptsNameThisServer()
    {
        await using var server = await TestServer.StartAsync();
        using var anonymous = server.Client();
        var origin = server.BaseAddress.GetLeftPart(UriPartial.Authority);
        foreach (var script in new[] { "daemon.sh", "daemon.ps1", "desktop.sh", "desktop.ps1" })
        {
            var text = await anonymous.GetStringAsync("/install/" + script);
            Assert.Contains(script.EndsWith(".sh") ? $"server='{origin}'" : $"$server = '{origin}'", text);
            Assert.DoesNotContain("\r", text);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/install/other.sh")).StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/install/daemon.sh");
        request.Headers.Host = "bad_host.example";
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.SendAsync(request)).StatusCode);
    }

    static string Rid => DesktopUpdater.CurrentRid;

    static async Task<(int Code, string Output)> RunAsync(string executable, string[] arguments, Dictionary<string, string> environment)
    {
        var info = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        foreach (var (name, value) in environment) info.Environment[name] = value;
        // A PowerShell 7 parent (the CI shell) leaves its module path, which makes Windows PowerShell load the wrong modules.
        info.Environment.Remove("PSModulePath");
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, output + await error);
    }

    [Fact]
    public async Task TheDaemonScriptEnrollsAndInstallsWhatTheServerOffers()
    {
        if (OperatingSystem.IsWindows()) return; // the PowerShell script is checked below
        var work = Directory.CreateTempSubdirectory("bosofa-script-").FullName;
        var log = Path.Combine(work, "calls");
        // A stand-in that writes down how it was called.
        await using var server = await BundleFixture.StartAsync("1.0", (Releases.Daemon, Rid, Encoding.UTF8.GetBytes($"#!/bin/sh\necho \"$*\" >> '{log}'\n")));
        var origin = server.BaseAddress.GetLeftPart(UriPartial.Authority);
        var (code, output) = await RunAsync("sh", ["-c", $"curl -fsSL {origin}/install/daemon.sh | sh"],
            new() { ["BOSOFA_DATA_DIR"] = Path.Combine(work, "data"), ["BOSOFA_INSTALL_ARGS"] = "--scope user --dry-run" });
        Assert.True(code == 0, output);
        Assert.Equal([$"enroll --server {origin} --data-dir {work}/data", $"install --data-dir {work}/data --scope user --dry-run"], File.ReadAllLines(log));
        Directory.Delete(work, true);
    }

    [Fact]
    public async Task TheDesktopScriptUnpacksTheApp()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = OperatingSystem.IsMacOS() ? Brand.Name + ".app" : Brand.Desktop;
        await using var server = await BundleFixture.StartAsync("1.0", (Releases.Desktop, Rid, BundleFixture.AppArchive("1.0", Rid, root, zip: false)));
        var work = Directory.CreateTempSubdirectory("bosofa-script-").FullName;
        var target = Path.Combine(work, "Apps", root);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "old"), "");
        var (code, output) = await RunAsync("sh", ["-c", $"curl -fsSL {server.BaseAddress.GetLeftPart(UriPartial.Authority)}/install/desktop.sh | sh"],
            new() { ["BOSOFA_APP_DIR"] = target, ["BOSOFA_NO_LAUNCH"] = "1" });
        Assert.True(code == 0, output);
        Assert.Equal("1.0", InstalledApp.FromRoot(target)?.Version);
        Assert.False(File.Exists(Path.Combine(target, "old"))); // a reinstall replaces the app
        Directory.Delete(work, true);
    }

    static string? PowerShell() => OperatingSystem.IsWindows()
        ? "powershell"
        : new[] { "/usr/local/bin/pwsh", "/usr/bin/pwsh", "/opt/homebrew/bin/pwsh" }.FirstOrDefault(File.Exists);

    [Fact]
    public async Task ThePowerShellScriptsParseAndTheDesktopOneUnpacksTheApp()
    {
        if (PowerShell() is not { } shell) return;
        var rid = OperatingSystem.IsWindows() ? Rid : "win-x64"; // what the script asks for off Windows
        await using var server = await BundleFixture.StartAsync("1.0", (Releases.Desktop, rid, BundleFixture.AppArchive("1.0", rid, Brand.Name, zip: true)));
        var origin = server.BaseAddress.GetLeftPart(UriPartial.Authority);
        using var anonymous = server.Client();
        var work = Directory.CreateTempSubdirectory("bosofa-script-").FullName;
        var daemonScript = Path.Combine(work, "daemon.ps1");
        File.WriteAllText(daemonScript, await anonymous.GetStringAsync("/install/daemon.ps1"));
        var (parsed, parseOutput) = await RunAsync(shell, ["-NoProfile", "-Command",
            $"$errors = $null; [void][System.Management.Automation.Language.Parser]::ParseFile('{daemonScript}', [ref]$null, [ref]$errors); if ($errors) {{ $errors; exit 1 }}"], []);
        Assert.True(parsed == 0, parseOutput);

        var target = Path.Combine(work, "Programs", Brand.Name);
        var (code, output) = await RunAsync(shell, ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", $"irm {origin}/install/desktop.ps1 | iex"],
            new() { ["BOSOFA_APP_DIR"] = target, ["BOSOFA_NO_LAUNCH"] = "1" });
        Assert.True(code == 0, output);
        Assert.Equal("1.0", InstalledApp.FromRoot(target)?.Version);
        Directory.Delete(work, true);
    }
}
