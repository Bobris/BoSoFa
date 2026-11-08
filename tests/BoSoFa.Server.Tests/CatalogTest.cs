using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using BoSoFa.Daemon;
using BoSoFa.Daemon.Sessions;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Tests;

public class CatalogTest
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    static byte[] TarGz(string name, string content)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) });
        return output.ToArray();
    }

    static async Task<T> Eventually<T>(Func<Task<T>> probe, Func<T, bool> done)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            var value = await probe();
            if (done(value) || DateTime.UtcNow > deadline) return value;
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task AMachineInstallsAPinnedReleaseAndRefusesAWrongHash()
    {
        var artifact = TarGz("bin/fake-harness", "#!/bin/sh\necho hi\n");
        var hash = Convert.ToHexStringLower(SHA256.HashData(artifact));
        using var listener = new HttpListener();
        var port = DevCluster.NodeProcess.FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); }
                catch (Exception) { return; }
                await context.Response.OutputStream.WriteAsync(artifact);
                context.Response.Close();
            }
        });

        await using var server = await TestServer.StartAsync();
        using var admin = server.Client();
        (await admin.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("boris@example.com", null))).EnsureSuccessStatusCode();
        using var member = server.Client();
        (await member.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("alice@example.com", null))).EnsureSuccessStatusCode();
        const string platform = "linux-x64"; // what SessionTest.EnrollAsync registers the machine as
        PublishHarnessRelease Release(string version, string sha) => new("codex", version,
            [new(platform, $"http://127.0.0.1:{port}/codex.tgz", sha, ArtifactKind.TarGz, "bin/fake-harness")]);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/harness-releases", new PublishHarnessRelease("codex", "0",
            [new(platform, "http://example.com/x", hash, ArtifactKind.TarGz, "x")]))).StatusCode); // plain http elsewhere
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync("/api/v1/harness-releases", Release("1.0", hash))).StatusCode);
        (await admin.PostAsJsonAsync("/api/v1/harness-releases", Release("1.0", hash))).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync("/api/v1/harness-releases", Release("2.0", new string('0', 64)))).EnsureSuccessStatusCode();
        Assert.Equal(2, (await member.GetFromJsonAsync<HarnessReleaseDto[]>("/api/v1/harness-releases"))!.Length);

        var directory = Path.Combine(server.DataDirectory, "daemon");
        var state = await SessionTest.EnrollAsync(server.BaseAddress, admin, directory);
        using var key = new DaemonStore(directory).LoadOrCreateKey();
        using var outbox = new Outbox(Path.Combine(directory, "outbox"));
        await using var host = new SessionHost(directory, outbox, new(), TextWriter.Null);
        using var stop = new CancellationTokenSource();
        var running = new DaemonLink(state, key, TextWriter.Null, outbox, host).RunAsync(stop.Token);
        await Eventually(() => admin.GetFromJsonAsync<MachineDto[]>("/api/v1/machines")!, m => m!.Single().Online);

        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync($"/api/v1/machines/{state.MachineId}/harnesses",
            new InstallHarnessRequest("codex", "1.0"))).StatusCode);
        (await admin.PostAsJsonAsync($"/api/v1/machines/{state.MachineId}/harnesses", new InstallHarnessRequest("codex", "1.0")))
            .EnsureSuccessStatusCode();
        var machines = await Eventually(() => admin.GetFromJsonAsync<MachineDto[]>("/api/v1/machines")!, m => m!.Single().Harnesses.Length > 0);
        Assert.Equal(new InstalledHarness("codex", "1.0", null), Assert.Single(machines!.Single().Harnesses));
        var installed = host.Installer.Find("codex");
        Assert.Equal(Path.Combine(Path.GetFullPath(directory), "harnesses", "codex", "1.0", "bin", "fake-harness"), installed);
        if (!OperatingSystem.IsWindows()) Assert.True(File.GetUnixFileMode(installed!).HasFlag(UnixFileMode.UserExecute));

        // A download that does not match its pinned hash is reported and never replaces what works.
        (await admin.PostAsJsonAsync($"/api/v1/machines/{state.MachineId}/harnesses", new InstallHarnessRequest("codex", "2.0")))
            .EnsureSuccessStatusCode();
        machines = await Eventually(() => admin.GetFromJsonAsync<MachineDto[]>("/api/v1/machines")!, m => m!.Single().Harnesses.Single().Error != null);
        var failed = machines!.Single().Harnesses.Single();
        Assert.Equal("2.0", failed.Version);
        Assert.Contains("SHA-256", failed.Error);
        Assert.Equal(installed, host.Installer.Find("codex"));
        Assert.False(Directory.Exists(Path.Combine(directory, "harnesses", "codex", "2.0")));

        await stop.CancelAsync();
        await running.WaitAsync(Timeout);
        listener.Stop();
    }
}
