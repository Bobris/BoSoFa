using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BoSoFa.Daemon;
using BoSoFa.Daemon.Harnesses.Claude;
using BoSoFa.Daemon.Sessions;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Tests;

[Collection("Replay")]
public class ProfileTest
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    const string Secret = "mcp-secret-value-0123456789";

    static byte[] SkillZip(string text)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("SKILL.md").Open());
            writer.Write(text);
        }
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
    public async Task AProfilesSkillsAndMcpServersReachTheSession()
    {
        await using var server = await TestServer.StartAsync();
        using var admin = server.Client();
        (await admin.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("boris@example.com", null))).EnsureSuccessStatusCode();
        using var member = server.Client();
        (await member.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("alice@example.com", null))).EnsureSuccessStatusCode();

        var blob = await (await admin.PostAsync("/api/v1/blobs", new ByteArrayContent(SkillZip("---\nname: greeter\n---\nSay hi.\n"))))
            .Content.ReadFromJsonAsync<BlobRef>();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/skills",
            new PublishSkillBundle("greeter", "1", new string('a', 64), null))).StatusCode); // no such blob
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync("/api/v1/skills",
            new PublishSkillBundle("greeter", "1", blob!.Sha256, null))).StatusCode);
        (await admin.PostAsJsonAsync("/api/v1/skills", new PublishSkillBundle("greeter", "1", blob.Sha256, "Greets"))).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync("/api/v1/mcp-servers", new PublishMcpServer("echo", McpTransport.Stdio, "python3", ["echo.py"], null,
            new() { ["ECHO_TOKEN"] = Secret }))).EnsureSuccessStatusCode();
        Assert.DoesNotContain(Secret, await member.GetStringAsync("/api/v1/mcp-servers")); // readable by all, without values
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync("/api/v1/profiles",
            new ProfileDto("broken", null, null, ["missing"], []))).StatusCode);
        (await admin.PostAsJsonAsync("/api/v1/profiles", new ProfileDto("team", "haiku", "acceptEdits", ["echo"],
            [new("greeter", "1")]))).EnsureSuccessStatusCode();

        var directory = Path.Combine(Path.GetTempPath(), "bosofa-profile-" + Guid.NewGuid().ToString("N"));
        try
        {
            var state = await SessionTest.EnrollAsync(server.BaseAddress, admin, directory);
            using var key = new DaemonStore(directory).LoadOrCreateKey();
            var source = new ServerAccountLeaser(state, key);
            using var outbox = new Outbox(Path.Combine(directory, "outbox"));
            await using var host = new SessionHost(directory, outbox, new(ReplayFixture: SessionTest.Fixture, Profiles: source), TextWriter.Null);
            using var stop = new CancellationTokenSource();
            var running = new DaemonLink(state, key, TextWriter.Null, outbox, host).RunAsync(stop.Token);

            var session = (await (await admin.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest(state.MachineId, Harnesses.Replay,
                directory, "hello", null, null, null, false, "team"))).Content.ReadFromJsonAsync<SessionDto>())!;
            Assert.Equal(("team", "haiku", "acceptEdits"), (session.ProfileName, session.Model, session.PermissionMode)); // the profile's defaults

            // The skill arrives as a per-session plugin; the secret only through the machine's own request.
            var plugin = Path.Combine(directory, "session-skills", session.Id.ToString());
            await Eventually(() => Task.FromResult(File.Exists(Path.Combine(plugin, "skills", "greeter", "SKILL.md"))), found => found);
            Assert.Contains("Say hi.", await File.ReadAllTextAsync(Path.Combine(plugin, "skills", "greeter", "SKILL.md")));
            Assert.True(File.Exists(Path.Combine(plugin, ".claude-plugin", "plugin.json")));
            var resolved = await source.GetProfileAsync(session.Id, default);
            Assert.Equal(Secret, Assert.Single(resolved.McpServers).Secrets["ECHO_TOKEN"]);

            // The harness is told the daemon's gateway, in a file only this user reads; the secret stays in the daemon's memory.
            var mcpConfig = Path.Combine(directory, "mcp", $"{session.Id}.json");
            await Eventually(() => Task.FromResult(File.Exists(mcpConfig)), found => found);
            using (var config = JsonDocument.Parse(await File.ReadAllTextAsync(mcpConfig)))
            {
                var echo = config.RootElement.GetProperty("mcpServers").GetProperty("echo");
                Assert.Equal("http", echo.GetProperty("type").GetString());
                Assert.EndsWith($"/mcp/{session.Id}/echo", echo.GetProperty("url").GetString());
            }
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(mcpConfig));
            AssertNoFileHolds(directory, Secret);

            var otherDirectory = Path.Combine(directory, "other");
            var other = await SessionTest.EnrollAsync(server.BaseAddress, admin, otherDirectory);
            using var otherKey = new DaemonStore(otherDirectory).LoadOrCreateKey();
            var refused = await Assert.ThrowsAsync<HttpRequestException>(() => new ServerAccountLeaser(other, otherKey).GetProfileAsync(session.Id, default));
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

            await stop.CancelAsync();
            await running.WaitAsync(Timeout);
        }
        finally
        {
            try { Directory.Delete(directory, true); }
            catch (IOException) { }
        }

        AssertNoFileHolds(server.DataDirectory, Secret);
    }

    static void AssertNoFileHolds(string directory, string secret)
    {
        var needle = Encoding.UTF8.GetBytes(secret);
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            byte[] content;
            try { content = File.ReadAllBytes(file); }
            catch (IOException) { continue; }
            Assert.True(content.AsSpan().IndexOf(needle) < 0, $"{file} contains the MCP secret in the clear");
        }
    }

    [Fact]
    public void McpServersBecomeEachHarnesssNativeConfig()
    {
        ResolvedMcpServer[] servers =
        [
            new("echo", McpTransport.Stdio, "python3", ["echo.py"], null, new() { ["TOKEN"] = "t" }),
            new("web", McpTransport.Http, null, [], "https://mcp.example/x", new() { ["Authorization"] = "Bearer b" })
        ];
        using var claude = JsonDocument.Parse(McpConfig.ForClaude(servers));
        var map = claude.RootElement.GetProperty("mcpServers");
        Assert.Equal("stdio", map.GetProperty("echo").GetProperty("type").GetString());
        Assert.Equal("t", map.GetProperty("echo").GetProperty("env").GetProperty("TOKEN").GetString());
        Assert.Equal("Bearer b", map.GetProperty("web").GetProperty("headers").GetProperty("Authorization").GetString());

        Assert.Equal("Bearer b", McpConfig.ForCodex(servers)["mcp_servers"]!["web"]!["http_headers"]!["Authorization"]!.GetValue<string>());
        var acp = McpConfig.ForAcp(servers);
        Assert.Equal("TOKEN", acp[0]!["env"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("http", acp[1]!["type"]!.GetValue<string>());

        var arguments = ClaudeProcess.Arguments(new("/w", "default", null, null, null, "/d/mcp/1.json", "/plugin"));
        Assert.Equal("/d/mcp/1.json", arguments[arguments.ToList().IndexOf("--mcp-config") + 1]);
        Assert.Equal("/plugin", arguments[arguments.ToList().IndexOf("--plugin-dir") + 1]);
    }
}
