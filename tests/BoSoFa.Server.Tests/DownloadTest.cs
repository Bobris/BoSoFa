using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BoSoFa.Desktop;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Tests;

/// <summary>Releases published by an admin and fetched by computers that have not signed in.</summary>
static class ReleaseFixture
{
    public static readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public static string PublicKey => Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo());

    public static async Task<HttpClient> AdminAsync(TestServer server)
    {
        var browser = server.Client(new CookieContainer());
        (await browser.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("admin@example.com", null))).EnsureSuccessStatusCode();
        return browser;
    }

    public static async Task<HttpResponseMessage> PublishAsync(HttpClient admin, string product, string version, (string Rid, byte[] Content)[] files,
        string channel = Releases.Stable, string? manifestProduct = "", ECDsa? key = null)
    {
        var artifacts = new List<ReleaseArtifact>();
        foreach (var (rid, content) in files)
        {
            var blob = await (await admin.PostAsync("/api/v1/blobs", new ByteArrayContent(content))).Content.ReadFromJsonAsync<BlobRef>();
            artifacts.Add(new(rid, blob!.Sha256, blob.Size));
        }
        var named = manifestProduct == "" ? product == Releases.Daemon ? null : product : manifestProduct;
        var manifest = JsonSerializer.Serialize(new ReleaseManifest(version, artifacts.ToArray(), named), ProtocolJsonContext.Default.ReleaseManifest);
        var signature = Convert.ToBase64String((key ?? Key).SignData(Encoding.UTF8.GetBytes(manifest), HashAlgorithmName.SHA256));
        return await admin.PostAsJsonAsync($"/api/v1/{product}-releases", new PublishRelease(manifest, signature, channel));
    }

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
    public async Task PublishedReleasesCanBeDownloadedWithoutSigningIn()
    {
        await using var server = await TestServer.StartAsync();
        using var anonymous = server.Client();
        Assert.Empty((await anonymous.GetFromJsonAsync<ProductDownload[]>("/api/v1/downloads"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/api/v1/downloads/daemon/latest/linux-x64")).StatusCode);

        using var admin = await ReleaseFixture.AdminAsync(server);
        (await ReleaseFixture.PublishAsync(admin, Releases.Daemon, "1.0", [("linux-x64", "linux one"u8.ToArray()), ("win-x64", "windows one"u8.ToArray())])).EnsureSuccessStatusCode();
        (await ReleaseFixture.PublishAsync(admin, Releases.Daemon, "2.0", [("linux-x64", "linux two"u8.ToArray())])).EnsureSuccessStatusCode();
        (await ReleaseFixture.PublishAsync(admin, Releases.Daemon, "3.0-beta", [("linux-x64", "linux beta"u8.ToArray())], Releases.Beta)).EnsureSuccessStatusCode();
        (await ReleaseFixture.PublishAsync(admin, Releases.Desktop, "1.5", [("osx-arm64", "mac app"u8.ToArray())])).EnsureSuccessStatusCode();

        var downloads = (await anonymous.GetFromJsonAsync<ProductDownload[]>("/api/v1/downloads"))!;
        var daemon = Assert.Single(downloads, download => download.Product == Releases.Daemon);
        Assert.Equal("2.0", daemon.Version); // the newest stable one, not the beta
        Assert.Equal(("linux-x64", "bosofad", "/api/v1/downloads/daemon/2.0/linux-x64"), (daemon.Files.Single().Rid, daemon.Files.Single().FileName, daemon.Files.Single().Url));
        var desktop = Assert.Single(downloads, download => download.Product == Releases.Desktop);
        Assert.Equal(("1.5", "bosofa-desktop-osx-arm64.tar.gz"), (desktop.Version, desktop.Files.Single().FileName));

        using var latest = await anonymous.GetAsync("/api/v1/downloads/daemon/latest/linux-x64");
        Assert.Equal("linux two", await latest.Content.ReadAsStringAsync());
        Assert.Equal("2.0", latest.Headers.GetValues("X-BoSoFa-Version").Single());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData("linux two"u8)), latest.Headers.GetValues("X-BoSoFa-Sha256").Single());
        Assert.Equal("bosofad", latest.Content.Headers.ContentDisposition?.FileName);
        // The newest stable build for a platform, even when a later release left that platform out.
        Assert.Equal("windows one", await anonymous.GetStringAsync("/api/v1/downloads/daemon/latest/win-x64"));
        Assert.Equal("linux beta", await anonymous.GetStringAsync("/api/v1/downloads/daemon/3.0-beta/linux-x64"));
        foreach (var missing in new[] { "/api/v1/downloads/daemon/latest/plan9-mips", "/api/v1/downloads/daemon/9.9/linux-x64", "/api/v1/downloads/nothing/latest/linux-x64" })
            Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(missing)).StatusCode);

        var signed = (await anonymous.GetFromJsonAsync<SignedRelease>("/api/v1/downloads/desktop/latest?rid=osx-arm64"))!;
        Assert.True(Releases.Verify(ReleaseFixture.PublicKey, signed.Manifest, signed.Signature));
        // Other blobs still need a sign-in.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/blobs/" + daemon.Files.Single().Sha256)).StatusCode);
    }

    [Fact]
    public async Task AReleaseOfOneProductCannotBePublishedAsTheOther()
    {
        await using var server = await TestServer.StartAsync();
        using var admin = await ReleaseFixture.AdminAsync(server);
        var asDesktop = await ReleaseFixture.PublishAsync(admin, Releases.Desktop, "1.0", [("linux-x64", "x"u8.ToArray())], manifestProduct: null);
        Assert.Equal(HttpStatusCode.BadRequest, asDesktop.StatusCode);
        Assert.Contains("not for the desktop", await asDesktop.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await ReleaseFixture.PublishAsync(admin, Releases.Daemon, "1.0", [("linux-x64", "x"u8.ToArray())], manifestProduct: Releases.Desktop)).StatusCode);
        // A member may not publish.
        using var member = server.Client(new CookieContainer());
        (await member.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("member@example.com", null))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await ReleaseFixture.PublishAsync(member, Releases.Desktop, "1.0", [("linux-x64", "x"u8.ToArray())])).StatusCode);
    }

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
        await using var server = await TestServer.StartAsync();
        using var admin = await ReleaseFixture.AdminAsync(server);
        var work = Directory.CreateTempSubdirectory("bosofa-script-").FullName;
        var log = Path.Combine(work, "calls");
        // A stand-in that writes down how it was called.
        (await ReleaseFixture.PublishAsync(admin, Releases.Daemon, "1.0", [(Rid, Encoding.UTF8.GetBytes($"#!/bin/sh\necho \"$*\" >> '{log}'\n"))])).EnsureSuccessStatusCode();
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
        await using var server = await TestServer.StartAsync();
        using var admin = await ReleaseFixture.AdminAsync(server);
        var root = OperatingSystem.IsMacOS() ? "BoSoFa.app" : "bosofa-desktop";
        (await ReleaseFixture.PublishAsync(admin, Releases.Desktop, "1.0", [(Rid, ReleaseFixture.AppArchive("1.0", Rid, root, zip: false))])).EnsureSuccessStatusCode();
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
        await using var server = await TestServer.StartAsync();
        using var admin = await ReleaseFixture.AdminAsync(server);
        var origin = server.BaseAddress.GetLeftPart(UriPartial.Authority);
        using var anonymous = server.Client();
        var work = Directory.CreateTempSubdirectory("bosofa-script-").FullName;
        var daemonScript = Path.Combine(work, "daemon.ps1");
        File.WriteAllText(daemonScript, await anonymous.GetStringAsync("/install/daemon.ps1"));
        var (parsed, parseOutput) = await RunAsync(shell, ["-NoProfile", "-Command",
            $"$errors = $null; [void][System.Management.Automation.Language.Parser]::ParseFile('{daemonScript}', [ref]$null, [ref]$errors); if ($errors) {{ $errors; exit 1 }}"], []);
        Assert.True(parsed == 0, parseOutput);

        var rid = OperatingSystem.IsWindows() ? Rid : "win-x64"; // what the script asks for off Windows
        (await ReleaseFixture.PublishAsync(admin, Releases.Desktop, "1.0", [(rid, ReleaseFixture.AppArchive("1.0", rid, "BoSoFa", zip: true))])).EnsureSuccessStatusCode();
        var target = Path.Combine(work, "Programs", "BoSoFa");
        var (code, output) = await RunAsync(shell, ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", $"irm {origin}/install/desktop.ps1 | iex"],
            new() { ["BOSOFA_APP_DIR"] = target, ["BOSOFA_NO_LAUNCH"] = "1" });
        Assert.True(code == 0, output);
        Assert.Equal("1.0", InstalledApp.FromRoot(target)?.Version);
        Directory.Delete(work, true);
    }
}
