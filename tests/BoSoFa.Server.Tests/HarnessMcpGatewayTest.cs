using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using BoSoFa.Daemon;
using BoSoFa.Daemon.Harnesses;
using BoSoFa.Daemon.Harnesses.Claude;
using BoSoFa.Daemon.Sessions;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Tests;

/// <summary>
/// The real Claude Code and Codex reach an MCP server of a profile through the daemon's gateway: the harness is told only the
/// gateway's address and the session's bearer, the daemon starts the server with its secret, and a tool call comes back. Both
/// harnesses call a tool without the model (<c>mcp_call</c>, <c>mcpServer/tool/call</c>), so no account is needed and nothing is
/// billed. Runs where the harness is installed; the fake server is a bash script, so not on Windows.
/// </summary>
public sealed class HarnessMcpGatewayTest : IAsyncLifetime
{
    const string Secret = "gateway-secret-5f1c9a7e";
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    sealed class Sink : ISessionSink
    {
        public void Report(ulong sessionId, SessionReport report) { }
        public void Delta(ulong sessionId, string itemRef, string text) { }
    }

    internal sealed class HarnessFactAttribute : FactAttribute
    {
        public HarnessFactAttribute(string harness)
        {
            if (OperatingSystem.IsWindows()) Skip = "The fake MCP server is a bash script.";
            else if (Find(harness) == null) Skip = $"{harness} is not installed on this machine.";
        }
    }

    static string? Find(string harness) => harness == Harnesses.Claude
        ? ClaudeProcess.FindExecutable(null)
        : HarnessEnvironment.FindExecutable(null, [harness], "/opt/homebrew/bin", "/usr/local/bin");

    readonly string _root = Directory.CreateTempSubdirectory("bosofa-harness-mcp-").FullName;
    string Work => Path.Combine(_root, "work");
    McpEndpoint _endpoint = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Work);
        _endpoint = new McpEndpoint(_ => null, new Sink(), null);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _endpoint.DisposeAsync();
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
    }

    /// <summary>What the daemon tells the harness of session 7, whose profile has one stdio server holding <see cref="Secret"/>.</summary>
    ResolvedMcpServer[] Entries()
    {
        var entries = _endpoint.ServersFor(7, Work, [new ResolvedMcpServer("local", McpTransport.Stdio, "/bin/bash",
            [AgentToolHostTest.WriteFakeMcpServer(_root)], null, new() { ["MCP_SECRET"] = Secret })]);
        Assert.All(entries, entry => Assert.DoesNotContain(Secret, string.Concat(entry.Secrets.Values) + entry.Url));
        return entries;
    }

    /// <summary>No process of this user has the secret on its command line, the harness and its children included.</summary>
    static void AssertNoCommandLineHoldsTheSecret()
    {
        var start = new ProcessStartInfo("ps", ["-axww", "-o", "command"]) { RedirectStandardOutput = true, UseShellExecute = false };
        using var ps = Process.Start(start)!;
        var lines = ps.StandardOutput.ReadToEnd();
        ps.WaitForExit();
        Assert.DoesNotContain(Secret, lines);
    }

    static async Task<T> Eventually<T>(Func<Task<T?>> probe) where T : class
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            if (await probe() is { } found) return found;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The harness did not get there in time.");
            await Task.Delay(250);
        }
    }

    [HarnessFact(Harnesses.Claude)]
    public async Task ClaudeCodeCallsAProfileServerThroughTheGateway()
    {
        var entries = Entries();
        var config = Path.Combine(_root, "mcp", "7.json");
        PrivateFile.CreateDirectory(Path.GetDirectoryName(config)!);
        PrivateFile.Write(config, McpConfig.ForClaude(entries));
        var home = Directory.CreateDirectory(Path.Combine(_root, "claude")).FullName;
        await using var claude = ClaudeProcess.Start(Find(Harnesses.Claude)!, new ClaudeLaunch(Work, "default", null, null,
            new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = home }, config));
        _ = Task.Run(async () => { await foreach (var _ in claude.Messages.ReadAllAsync()) { } });
        await claude.InitializeAsync(Timeout);

        // Claude Code connects to its MCP servers in the background.
        var status = await Eventually<string>(async () =>
        {
            var answer = await claude.ControlAsync("mcp_status", new JsonObject(), Timeout);
            var local = answer.GetProperty("mcpServers").EnumerateArray().Single(server => server.GetProperty("name").GetString() == "local");
            return local.GetProperty("status").GetString() switch
            {
                "connected" => answer.GetRawText(),
                "pending" => null,
                _ => throw new InvalidOperationException("Claude Code could not connect: " + local.GetRawText())
            };
        });
        Assert.DoesNotContain(Secret, status);
        AssertNoCommandLineHoldsTheSecret();

        var called = await claude.ControlAsync("mcp_call", new JsonObject { ["tool"] = "mcp__local__where", ["arguments"] = new JsonObject() }, Timeout);
        Assert.Contains($"in work with {Secret}", called.GetRawText());
        Assert.DoesNotContain(Secret, await File.ReadAllTextAsync(config));
    }

    [HarnessFact(Harnesses.Codex)]
    public async Task CodexCallsAProfileServerThroughTheGateway()
    {
        var entries = Entries();
        var home = Directory.CreateDirectory(Path.Combine(_root, "codex")).FullName;
        await File.WriteAllTextAsync(Path.Combine(home, "config.toml"), "cli_auth_credentials_store = \"file\"\ncheck_for_update_on_startup = false\n");
        var environment = HarnessEnvironment.Clean();
        environment["CODEX_HOME"] = home;
        await using var rpc = JsonRpcProcess.Start(Find(Harnesses.Codex)!, ["app-server"], Work, environment, includeVersion: false);
        var asked = new ConcurrentQueue<string>();
        rpc.OnRequest = request =>
        {
            asked.Enqueue(request.Method);
            return Task.FromResult<JsonNode?>(new JsonObject { ["action"] = "decline" });
        };
        _ = Task.Run(async () =>
        {
            await foreach (var message in rpc.Incoming.ReadAllAsync())
                if (message.Id != null) rpc.Serve(message);
        });
        await rpc.RequestAsync("initialize", new JsonObject
        {
            ["clientInfo"] = new JsonObject { ["name"] = Brand.Id, ["title"] = Brand.Name, ["version"] = "test" },
            ["capabilities"] = new JsonObject { ["experimentalApi"] = false }
        }, Timeout);
        await rpc.NotifyAsync("initialized");
        // As CodexSession opens a thread: the gateway's entries are the thread's config.
        var started = await rpc.RequestAsync("thread/start", new JsonObject
        {
            ["cwd"] = Work, ["approvalPolicy"] = "never", ["sandbox"] = "read-only", ["serviceName"] = Brand.Id,
            ["config"] = McpConfig.ForCodex(entries)
        }, Timeout);
        var threadId = started.GetProperty("thread").GetProperty("id").GetString()!;

        var status = await Eventually<string>(async () =>
        {
            var answer = (await rpc.RequestAsync("mcpServerStatus/list", new JsonObject { ["threadId"] = threadId }, Timeout)).GetRawText();
            return answer.Contains("\"where\"") ? answer : null;
        });
        Assert.DoesNotContain(Secret, status);
        AssertNoCommandLineHoldsTheSecret();

        var called = await rpc.RequestAsync("mcpServer/tool/call", new JsonObject
        {
            ["threadId"] = threadId, ["server"] = "local", ["tool"] = "where", ["arguments"] = new JsonObject()
        }, Timeout);
        Assert.Contains($"in work with {Secret}", called.GetRawText());
        Assert.Empty(asked);
    }
}
