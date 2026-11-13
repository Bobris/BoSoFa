using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Web;
using BoSoFa.Daemon.Service;
using BoSoFa.Desktop;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Tests;

public class LocalDaemonTest
{
    sealed class MemoryVault : ITokenVault
    {
        string? _token;
        public Task<string?> ReadAsync(CancellationToken cancellation) => Task.FromResult(_token);

        public Task WriteAsync(string token, CancellationToken cancellation)
        {
            _token = token;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(CancellationToken cancellation)
        {
            _token = null;
            return Task.CompletedTask;
        }
    }

    /// <summary>Plays the daemon's command line.</summary>
    sealed class FakeDaemonCommands(string daemonDirectory, Func<string, Task>? onCode = null, string token = "sk-ant-oat01-abc_DEF-123456789012345678901234567890123456789012345678901234567890") : ICommandRunner
    {
        public List<string[]> Runs { get; } = [];
        public string? Executable { get; private set; }

        public async Task<(int Code, string Output, string Error)> RunAsync(string executable, string[] arguments, Action<string>? onOutputLine, CancellationToken cancellation)
        {
            Executable = executable;
            Runs.Add(arguments);
            switch (arguments[0])
            {
                case "enroll":
                    onOutputLine!("and approve the code ABCD-EFGH (valid until 10:00).");
                    if (onCode != null) await onCode("ABCD-EFGH");
                    Directory.CreateDirectory(daemonDirectory);
                    // Finished by the time the approval fails and cancels it: the state must exist for the test to go on.
                    await File.WriteAllTextAsync(Path.Combine(daemonDirectory, "daemon.json"), $$"""{"server":"{{arguments[2]}}","machineId":9}""", CancellationToken.None);
                    return (0, "", "");
                case "install":
                    Directory.CreateDirectory(Path.Combine(daemonDirectory, "versions"));
                    await File.WriteAllTextAsync(Path.Combine(daemonDirectory, "versions", "state.json"), """{"current":"1.0"}""", cancellation);
                    return (0, "", "");
                case "claude-setup-token":
                    return (0, token + "\n", "");
                default:
                    return (0, "", "");
            }
        }
    }

    static async Task<(TestServer Server, HttpClient Browser, DesktopAccount Account, string Data)> StartAsync(string? bundle = null)
    {
        var server = await TestServer.StartAsync(extraArgs: bundle == null ? null : ["--BoSoFa:BundleDirectory", bundle]);
        var browser = server.Client(new CookieContainer());
        (await browser.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("boris@example.com", null))).EnsureSuccessStatusCode();
        var data = Directory.CreateTempSubdirectory("bosofa-local-").FullName;
        var account = new DesktopAccount(data, new MemoryVault(), async authorize =>
        {
            var query = HttpUtility.ParseQueryString(authorize.Query);
            var approved = await (await browser.PostAsJsonAsync("/auth/desktop/approve", new DesktopApproveRequest(query["redirect_uri"]!, query["code_challenge"]!, query["state"]!)))
                .Content.ReadFromJsonAsync<DesktopApproveResponse>();
            using var client = new HttpClient();
            (await client.GetAsync(approved!.Redirect)).EnsureSuccessStatusCode();
        });
        Assert.Null(await account.SignInAsync(server.BaseAddress.ToString(), default));
        return (server, browser, account, data);
    }

    [Fact]
    public async Task EnablingFetchesTheServersDaemonEnrollsAndInstalls()
    {
        var (bare, bareBrowser, bareAccount, bareData) = await StartAsync();
        await using (bare)
        using (bareBrowser)
        using (bareAccount)
        {
            using var none = new HttpClient();
            Assert.Contains("no daemon", await new LocalDaemon(bareAccount, bareData, Path.Combine(bareData, "d"), new FakeDaemonCommands(Path.Combine(bareData, "d")), none)
                .EnableAsync(default));
        }

        var rid = Releases.RidOf(OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux",
            System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant());
        var (server, browser, account, data) = await StartAsync(BundleFixture.Write("2.0", (Releases.Daemon, rid, "daemon"u8.ToArray())));
        await using (server)
        using (browser)
        using (account)
        {
            var daemon = Path.Combine(data, "daemon-data");
            var commands = new FakeDaemonCommands(daemon);
            using var http = new HttpClient();
            var local = new LocalDaemon(account, data, daemon, commands, http);
            Assert.False(local.Status().Enrolled);
            var file = BundleFixture.FileOf(server, Releases.Daemon, rid);
            File.WriteAllText(file, "tampered");
            Assert.Contains("does not match", await local.EnableAsync(default));
            Assert.Empty(commands.Runs);
            File.WriteAllText(file, "daemon");

            // The real enroll would wait for this approval, so the app must have made it; there is no such code on this server.
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => local.EnableAsync(default));
            var fetched = Path.Combine(data, "daemon", "2.0", OperatingSystem.IsWindows() ? Brand.Daemon + ".exe" : Brand.Daemon);
            Assert.Equal("daemon", await File.ReadAllTextAsync(fetched));

            var status = local.Status();
            Assert.Equal((true, false, 9d), (status.Enrolled, status.Installed, status.MachineId));
            Assert.Null(await local.EnableAsync(default)); // enrolled already: only installs now
            Assert.True(local.Status().Installed);
            Assert.Equal(1, commands.Runs.Count(run => run[0] == "enroll"));
            Assert.Equal(["install", "--scope", "user", "--data-dir", daemon], commands.Runs.Last());
            Assert.Equal(fetched, commands.Executable);
        }
    }

    [Fact]
    public async Task EnablingApprovesTheEnrollmentAsTheSignedInPerson()
    {
        var (server, browser, account, data) = await StartAsync();
        await using (server)
        using (browser)
        using (account)
        {
            // A real enrollment is started on the server; the fake command line reports its code.
            var machineKey = Convert.ToBase64String(ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportSubjectPublicKeyInfo());
            using var daemon = server.Client();
            var started = await (await daemon.PostAsJsonAsync("/api/daemon/v1/enroll/start", new EnrollStartRequest("this-mac", "macos", "arm64", "1.0", machineKey)))
                .Content.ReadFromJsonAsync<EnrollStartResponse>();
            var directory = Path.Combine(data, "daemon-data");
            var commands = new CodeReporting(started!.UserCode, directory);
            using var http = new HttpClient();
            var local = new LocalDaemon(account, data, directory, commands, http, configuredExecutable: "/fake/bosofad");
            Assert.Null(await local.EnableAsync(default));
            var poll = await (await daemon.PostAsJsonAsync("/api/daemon/v1/enroll/poll", new EnrollPollRequest(started.EnrollmentId))).Content.ReadFromJsonAsync<EnrollPollResponse>();
            Assert.Equal(EnrollStatus.Approved, poll!.Status);
            var machine = (await browser.GetFromJsonAsync<MachineDto[]>("/api/v1/machines"))!.Single();
            Assert.Equal(("this-mac", true), (machine.Name, machine.CanControl));

            await File.WriteAllTextAsync(Path.Combine(directory, "daemon.json"), """{"server":"https://other.example","machineId":1}""");
            Assert.Equal("This computer already belongs to https://other.example.", await local.EnableAsync(default));
        }
    }

    sealed class CodeReporting(string code, string directory) : ICommandRunner
    {
        public async Task<(int Code, string Output, string Error)> RunAsync(string executable, string[] arguments, Action<string>? onOutputLine, CancellationToken cancellation)
        {
            if (arguments[0] == "enroll")
            {
                onOutputLine!($"and approve the code {code} (valid until 10:00).");
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(Path.Combine(directory, "daemon.json"), $$"""{"server":"{{arguments[2]}}","machineId":9}""", cancellation);
            }
            return (0, "", "");
        }
    }

    [Fact]
    public async Task AClaudeTokenGoesStraightToTheServer()
    {
        var (server, browser, account, data) = await StartAsync();
        await using (server)
        using (browser)
        using (account)
        {
            using var http = new HttpClient();
            var local = new LocalDaemon(account, data, Path.Combine(data, "d"), new FakeDaemonCommands(Path.Combine(data, "d")), http, "/fake/bosofad");
            Assert.Null(await local.AddClaudeAccountAsync("My Claude", default));
            var added = Assert.Single((await browser.GetFromJsonAsync<HarnessAccountDto[]>("/api/v1/accounts"))!);
            Assert.Equal(("My Claude", AccountKind.ClaudeOAuthToken), (added.Label, added.Kind));
            Assert.DoesNotContain("abc_DEF", JsonSerializer.Serialize(added, ProtocolJsonContext.Default.HarnessAccountDto));
        }
    }

    [Theory]
    [InlineData("\u001b[2J\u001b[1;1HYour token:\r\n\u001b[32msk-ant-oat01-Abc_123-xyz\u001b[0m\r\nDone", "sk-ant-oat01-Abc_123-xyz")]
    [InlineData("sk-ant-oat01-Abc\u001b]0;title\u0007_123 and later sk-ant-oat01-Abc_123456", "sk-ant-oat01-Abc_123456")]
    [InlineData("nothing here", null)]
    public void TheTokenIsFoundInWhatTheTerminalShowed(string output, string? token) => Assert.Equal(token, ClaudeSetupToken.FindToken(output));
}
