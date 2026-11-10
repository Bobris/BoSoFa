using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using BoSoFa.Daemon.Sessions;
using BoSoFa.Daemon.Tools;
using BoSoFa.Protocol;
using Tool = BoSoFa.Protocol.MachineTools;

namespace BoSoFa.Server.Tests;

/// <summary>The machine tools of a server agent session, on real files and processes (docs/server-agent.md §6.1).</summary>
public class AgentToolHostTest : IAsyncLifetime
{
    sealed class Sink : ISessionSink
    {
        public ConcurrentQueue<(string Ref, string Text)> Deltas { get; } = new();
        public void Report(ulong sessionId, SessionReport report) { }
        public void Delta(ulong sessionId, string itemRef, string text) => Deltas.Enqueue((itemRef, text));
    }

    readonly Sink _sink = new();
    readonly string _root = Directory.CreateTempSubdirectory("bosofa-tools-").FullName;
    AgentToolHost _host = null!;
    int _next;

    string Directory_ => Path.Combine(_root, "work");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Directory_);
        _host = new AgentToolHost(_sink);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        foreach (var file in new DirectoryInfo(_root).EnumerateFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal; // git's read-only objects
        try { Directory.Delete(_root, true); }
        catch (IOException) { /* a process that just ended may still hold a file on Windows */ }
    }

    static string Json(params (string Name, object Value)[] arguments) =>
        JsonSerializer.Serialize(arguments.ToDictionary(pair => pair.Name, pair => pair.Value switch
        {
            string text => JsonSerializer.SerializeToElement(text),
            int number => JsonSerializer.SerializeToElement(number),
            bool flag => JsonSerializer.SerializeToElement(flag),
            _ => throw new ArgumentException("Not an argument type.")
        }));

    ToolRunRequest Request(string tool, string arguments, int timeoutSeconds = 30, string? key = null, bool isRetry = false, string? checkpoint = null) =>
        new(key ?? $"key-{++_next}", 7, Directory_, tool, arguments, timeoutSeconds, "tool-1", isRetry, checkpoint);

    async Task<ToolRunResult> Run(string tool, params (string Name, object Value)[] arguments) =>
        Assert.IsType<ToolRunResult>(await _host.HandleAsync(Request(tool, Json(arguments)), default));

    string PathOf(string name) => Path.Combine(Directory_, name);

    void Write(string name, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathOf(name))!);
        File.WriteAllText(PathOf(name), content);
    }

    static void Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error.Result);
    }

    static string Sleep(int seconds) => OperatingSystem.IsWindows() ? $"Start-Sleep {seconds}" : $"sleep {seconds}";

    [Fact]
    public async Task ReadingPagesThroughAFile()
    {
        Write("long.txt", string.Join('\n', Enumerable.Range(1, 5000).Select(n => "line " + n)) + "\n");
        var first = await Run(Tool.ReadFile, ("path", "long.txt"), ("limit", 2));
        Assert.Equal("1\tline 1\n2\tline 2\n[More lines follow. Continue with offset 3.]", first.Text);
        Assert.Equal("3\tline 3\n", (await Run(Tool.ReadFile, ("path", "long.txt"), ("offset", 3), ("limit", 1))).Text.Split("[More")[0]);
        var cut = await Run(Tool.ReadFile, ("path", "long.txt"));
        Assert.True(cut.Text.Length <= Tool.OutputLimit + 100);
        Assert.Contains("Continue with offset", cut.Text);
        Assert.Equal("The file has only 5000 lines.", (await Run(Tool.ReadFile, ("path", "long.txt"), ("offset", 6000))).Text);
    }

    [Fact]
    public async Task ReadingBinariesAndMissingFilesSaysSo()
    {
        await File.WriteAllBytesAsync(PathOf("blob.bin"), [1, 0, 2]);
        Assert.Contains("binary", (await Run(Tool.ReadFile, ("path", "blob.bin"))).Text);
        await File.WriteAllBytesAsync(PathOf("picture.png"), [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]);
        Assert.Contains("image/png", (await Run(Tool.ReadFile, ("path", "picture.png"))).Text);
        var missing = await Run(Tool.ReadFile, ("path", "nope.txt"));
        Assert.True(missing.IsError);
        Assert.Contains("nope.txt", missing.Text);
    }

    [Fact]
    public async Task ToolsStayInsideTheWorkingDirectory()
    {
        var outside = Path.Combine(_root, "secret.txt");
        await File.WriteAllTextAsync(outside, "secret");
        foreach (var path in new[] { "../secret.txt", outside })
        {
            var read = await Run(Tool.ReadFile, ("path", path));
            Assert.True(read.IsError);
            Assert.Contains("outside", read.Text);
            Assert.True((await Run(Tool.WriteFile, ("path", path), ("content", "x"))).IsError);
        }
        Assert.Equal("secret", await File.ReadAllTextAsync(outside));
        if (OperatingSystem.IsWindows()) return; // links need privileges there
        File.CreateSymbolicLink(PathOf("link.txt"), outside);
        Assert.True((await Run(Tool.ReadFile, ("path", "link.txt"))).IsError);
        var patch = await Run(Tool.ApplyPatch, ("input", "*** Begin Patch\n*** Add File: ../evil.txt\n+x\n*** End Patch"));
        Assert.True(patch.IsError);
        Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
    }

    [Fact]
    public async Task ListingAFolder()
    {
        Write("b.txt", "hello");
        Directory.CreateDirectory(PathOf("a"));
        Assert.Equal("a/\nb.txt\t5 bytes\n", (await Run(Tool.ListDir)).Text);
    }

    [Fact]
    public async Task GlobMatchesNamesAndLeavesOutWhatGitIgnores()
    {
        Write("src/a.cs", "");
        Write("src/deep/b.cs", "");
        Write("src/c.ts", "");
        Write("readme.md", "");
        Write("node_modules/x/index.cs", "");
        Assert.Equal("src/a.cs\nsrc/deep/b.cs\n", (await Run(Tool.Glob, ("pattern", "*.cs"))).Text);
        Assert.Equal("src/a.cs\nsrc/c.ts\n", (await Run(Tool.Glob, ("pattern", "src/*.{cs,ts}"))).Text);
        Assert.Equal("src/deep/b.cs\n", (await Run(Tool.Glob, ("pattern", "deep/*"), ("path", "src"))).Text);

        Git(Directory_, "init", "-q");
        Write(".gitignore", "generated/\n");
        Write("generated/out.cs", "");
        var text = (await Run(Tool.Glob, ("pattern", "**/*.cs"))).Text;
        Assert.DoesNotContain("generated", text);
        Assert.Contains("src/deep/b.cs", text);
        Assert.Equal("No file matches.", (await Run(Tool.Glob, ("pattern", "*.nothing"))).Text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GrepFindsLinesWithAndWithoutRipgrep(bool useRipgrep)
    {
        await using var host = new AgentToolHost(_sink, useRipgrep);
        Write("src/a.cs", "first\nNeedle here\nlast\n");
        Write("src/b.txt", "needle too\n");
        await File.WriteAllBytesAsync(PathOf("blob.bin"), [(byte)'n', 0, (byte)'e']);

        async Task<ToolRunResult> Grep(params (string Name, object Value)[] arguments) =>
            Assert.IsType<ToolRunResult>(await host.HandleAsync(Request(Tool.Grep, Json(arguments)), default));

        Assert.Equal("src/a.cs:2:Needle here\n", (await Grep(("pattern", "Needle"))).Text);
        Assert.Equal("src/a.cs:2:Needle here\nsrc/b.txt:1:needle too\n", (await Grep(("pattern", "needle"), ("ignore_case", true))).Text);
        Assert.Equal("src/b.txt:1:needle too\n", (await Grep(("pattern", "needle"), ("ignore_case", true), ("glob", "*.txt"))).Text);
        Assert.Equal("No line matches.", (await Grep(("pattern", "absent"))).Text);
    }

    [Fact]
    public async Task WritingCreatesFoldersAndKeepsPermissions()
    {
        Assert.Equal("Created deep/down/new.txt (2 bytes).", (await Run(Tool.WriteFile, ("path", "deep/down/new.txt"), ("content", "hi"))).Text);
        Assert.Equal("hi", await File.ReadAllTextAsync(PathOf("deep/down/new.txt")));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(PathOf("deep/down/new.txt"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Assert.StartsWith("Replaced", (await Run(Tool.WriteFile, ("path", "deep/down/new.txt"), ("content", "again"))).Text);
            Assert.True(File.GetUnixFileMode(PathOf("deep/down/new.txt")).HasFlag(UnixFileMode.UserExecute));
        }
        Assert.DoesNotContain(Directory.EnumerateFiles(PathOf("deep/down")), file => file.EndsWith(".tmp"));
    }

    [Fact]
    public async Task EditingNeedsTheExactTextOnce()
    {
        Write("a.txt", "one two one\n");
        var twice = await Run(Tool.EditFile, ("path", "a.txt"), ("old_text", "one"), ("new_text", "1"));
        Assert.True(twice.IsError);
        Assert.Contains("2 times", twice.Text);
        Assert.Equal("one two one\n", await File.ReadAllTextAsync(PathOf("a.txt")));
        Assert.True((await Run(Tool.EditFile, ("path", "a.txt"), ("old_text", "three"), ("new_text", "3"))).IsError);
        Assert.Equal("Replaced 1 occurrence in a.txt.", (await Run(Tool.EditFile, ("path", "a.txt"), ("old_text", "two"), ("new_text", "2"))).Text);
        Assert.Equal("Replaced 2 occurrences in a.txt.", (await Run(Tool.EditFile, ("path", "a.txt"), ("old_text", "one"), ("new_text", "1"), ("replace_all", true))).Text);
        Assert.Equal("1 2 1\n", await File.ReadAllTextAsync(PathOf("a.txt")));
    }

    [Fact]
    public async Task EditingKeepsLineEndingsAndTheByteOrderMark()
    {
        await File.WriteAllBytesAsync(PathOf("win.txt"), [0xEF, 0xBB, 0xBF, (byte)'a', 13, 10, (byte)'b', 13, 10]);
        Assert.False((await Run(Tool.EditFile, ("path", "win.txt"), ("old_text", "a\nb"), ("new_text", "x\ny"))).IsError);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'x', 13, 10, (byte)'y', 13, 10 }, await File.ReadAllBytesAsync(PathOf("win.txt")));
        await File.WriteAllBytesAsync(PathOf("latin.txt"), [0xE9, (byte)'a']);
        var refused = await Run(Tool.EditFile, ("path", "latin.txt"), ("old_text", "a"), ("new_text", "b"));
        Assert.True(refused.IsError);
        Assert.Contains("UTF-8", refused.Text);
    }

    [Fact]
    public async Task APatchAddsUpdatesMovesAndDeletes()
    {
        Write("keep.txt", "alpha\nbeta\ngamma\ndelta\n");
        Write("gone.txt", "x\n");
        Write("old.txt", "one\ntwo\n");
        var patch = """
            *** Begin Patch
            *** Add File: new/added.txt
            +hello
            +world
            *** Update File: keep.txt
            @@ alpha
            -beta
            +BETA
             gamma
            *** Delete File: gone.txt
            *** Update File: old.txt
            *** Move to: renamed.txt
            @@
             one
            -two
            +2
            *** End of File
            *** End Patch
            """.Replace("\r\n", "\n");
        var result = await Run(Tool.ApplyPatch, ("input", patch));
        Assert.False(result.IsError, result.Text);
        Assert.Equal("hello\nworld\n", await File.ReadAllTextAsync(PathOf("new/added.txt")));
        Assert.Equal("alpha\nBETA\ngamma\ndelta\n", await File.ReadAllTextAsync(PathOf("keep.txt")));
        Assert.False(File.Exists(PathOf("gone.txt")));
        Assert.False(File.Exists(PathOf("old.txt")));
        Assert.Equal("one\n2\n", await File.ReadAllTextAsync(PathOf("renamed.txt")));
    }

    [Fact]
    public async Task APatchThatDoesNotFitChangesNothing()
    {
        Write("a.txt", "one\n");
        Write("b.txt", "two\n");
        var result = await Run(Tool.ApplyPatch, ("input", "*** Begin Patch\n*** Update File: a.txt\n@@\n-one\n+ONE\n*** Update File: b.txt\n@@\n-not there\n+x\n*** End Patch"));
        Assert.True(result.IsError);
        Assert.Contains("not found", result.Text);
        Assert.Equal("one\n", await File.ReadAllTextAsync(PathOf("a.txt")));
        Assert.True((await Run(Tool.ApplyPatch, ("input", "not a patch"))).IsError);
    }

    [Fact]
    public async Task APatchToleratesTrailingSpaceAndKeepsCarriageReturns()
    {
        await File.WriteAllTextAsync(PathOf("a.txt"), "one  \r\ntwo\r\n");
        var result = await Run(Tool.ApplyPatch, ("input", "*** Begin Patch\n*** Update File: a.txt\n@@\n one\n-two\n+TWO\n*** End Patch"));
        Assert.False(result.IsError, result.Text);
        Assert.Equal("one  \r\nTWO\r\n", await File.ReadAllTextAsync(PathOf("a.txt")));
    }

    [Fact]
    public async Task ProjectInstructionsPreferAgentsAndStayInTheFolder()
    {
        Assert.Equal("", (await Run(Tool.ProjectInstructions)).Text);
        Write("CLAUDE.md", "from claude");
        Assert.Equal("CLAUDE.md\nfrom claude", (await Run(Tool.ProjectInstructions)).Text);
        Write("AGENTS.md", "from agents");
        Assert.Equal("AGENTS.md\nfrom agents", (await Run(Tool.ProjectInstructions)).Text);
        Write("AGENTS.md", new string('x', Tool.ProjectInstructionsLimit + 10));
        var cut = (await Run(Tool.ProjectInstructions)).Text;
        Assert.EndsWith("[The rest of this file was cut.]", cut);
        Assert.True(cut.Length < Tool.ProjectInstructionsLimit + 100);
        if (OperatingSystem.IsWindows()) return; // links need privileges there
        File.Delete(PathOf("AGENTS.md"));
        var outside = Path.Combine(_root, "secret.md");
        await File.WriteAllTextAsync(outside, "secret");
        File.CreateSymbolicLink(PathOf("AGENTS.md"), outside);
        Assert.Equal("CLAUDE.md\nfrom claude", (await Run(Tool.ProjectInstructions)).Text); // the link leads outside, so it is skipped
    }

    [Fact]
    public async Task ShellReportsOutputAndExitCode()
    {
        var ok = await Run(Tool.Shell, ("command", "echo hello"));
        Assert.False(ok.IsError);
        Assert.Equal("hello", ok.Text.Trim());
        var failed = await Run(Tool.Shell, ("command", OperatingSystem.IsWindows() ? "cmd /c exit 3; exit $LASTEXITCODE" : "echo oops >&2; exit 3"));
        Assert.True(failed.IsError);
        Assert.Contains("[Exit code 3]", failed.Text);
    }

    [Fact]
    public async Task ShellRunsInTheWorkingDirectoryWithoutTheDaemonsSecrets()
    {
        if (OperatingSystem.IsWindows()) return;
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "not-for-tools");
        try
        {
            var text = (await Run(Tool.Shell, ("command", "pwd; echo key=[$OPENAI_API_KEY]"))).Text;
            Assert.Contains(new DirectoryInfo(Directory_).Name, text);
            Assert.Contains("key=[]", text);
        }
        finally { Environment.SetEnvironmentVariable("OPENAI_API_KEY", null); }
    }

    [Fact]
    public async Task LongOutputKeepsItsStartAndItsEnd()
    {
        if (OperatingSystem.IsWindows()) return;
        var result = await Run(Tool.Shell, ("command", "echo FIRST; seq 1 40000; echo LAST"));
        Assert.StartsWith("FIRST", result.Text);
        Assert.Contains("LAST", result.Text);
        Assert.Contains("characters of output were cut", result.Text);
        Assert.True(result.Text.Length <= Tool.OutputLimit + 100);
    }

    [Fact]
    public async Task ShellOutputStreamsWhileItRuns()
    {
        var running = _host.HandleAsync(Request(Tool.Shell, Json(("command", $"echo early; {Sleep(30)}"))), default);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!_sink.Deltas.Any(delta => delta.Ref == "tool-1" && delta.Text.Contains("early")) && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.Contains(_sink.Deltas, delta => delta.Text.Contains("early"));
        Assert.False(running.IsCompleted);
        Assert.True(_host.IsBusy);
        await _host.HandleAsync(new ToolCancelRequest($"key-{_next}"), default);
        Assert.Equal("The tool was stopped.", Assert.IsType<ToolRunResult>(await running.WaitAsync(TimeSpan.FromSeconds(15))).Text);
        Assert.False(_host.IsBusy);
    }

    [Fact]
    public async Task ACommandStopsAtItsTimeoutWithItsOutput()
    {
        var watch = Stopwatch.StartNew();
        var result = Assert.IsType<ToolRunResult>(await _host.HandleAsync(Request(Tool.Shell, Json(("command", $"echo begun; {Sleep(60)}")), timeoutSeconds: 1), default));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20));
        Assert.True(result.IsError);
        Assert.Contains("begun", result.Text);
        Assert.Contains("Stopped after 1 seconds", result.Text);
    }

    [Fact]
    public async Task ABackgroundChildCannotHoldACommandOpen()
    {
        if (OperatingSystem.IsWindows()) return;
        var watch = Stopwatch.StartNew();
        var result = await Run(Tool.Shell, ("command", "sleep 30 & echo started"));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15));
        Assert.Contains("started", result.Text);
    }

    [Fact]
    public async Task ACallSentAgainJoinsTheFirstAndRunsOnce()
    {
        var log = PathOf("log.txt");
        var command = OperatingSystem.IsWindows() ? $"Add-Content '{log}' x; {Sleep(1)}" : $"echo x >> '{log}'; {Sleep(1)}";
        var request = Request(Tool.Shell, Json(("command", command)), key: "same");
        using var dropped = new CancellationTokenSource();
        var first = _host.HandleAsync(request, dropped.Token); // the connection that asked goes away
        await dropped.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var again = Assert.IsType<ToolRunResult>(await _host.HandleAsync(request with { IsRetry = true }, default));
        Assert.False(again.IsError, again.Text);
        var finished = Assert.IsType<ToolRunResult>(await _host.HandleAsync(request with { IsRetry = true }, default)); // and after it ended
        Assert.Equal(again, finished);
        Assert.Single(await File.ReadAllLinesAsync(log));
    }

    [Fact]
    public async Task ARetryThatNobodyKnowsDoesNotRunAnything()
    {
        var result = Assert.IsType<ToolRunResult>(await _host.HandleAsync(Request(Tool.WriteFile, Json(("path", "a.txt"), ("content", "x")), isRetry: true), default));
        Assert.True(result.IsError);
        Assert.Contains("unknown", result.Text);
        Assert.False(File.Exists(PathOf("a.txt")));
    }

    [Fact]
    public async Task WrongArgumentsAndAMissingFolderAreToolResults()
    {
        var missing = await Run(Tool.WriteFile, ("path", "a.txt"));
        Assert.True(missing.IsError);
        Assert.Contains("content", missing.Text);
        Assert.True((await Run("no_such_tool")).IsError);
        var noFolder = Assert.IsType<ToolRunResult>(await _host.HandleAsync(Request(Tool.ListDir, "{}") with { WorkingDirectory = PathOf("absent") }, default));
        Assert.True(noFolder.IsError);
        Assert.Contains("does not exist", noFolder.Text);
    }

    [Fact]
    public async Task ACheckpointAndADiffCoverWhatARunChanged()
    {
        Git(Directory_, "init", "-q", "-b", "main");
        Write("a.txt", "one\n");
        Git(Directory_, "add", "-A");
        Git(Directory_, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "first");

        const string before = "refs/bosofa-test/1-before", after = "refs/bosofa-test/1-after";
        Assert.False(Assert.IsType<ToolRunResult>(await _host.HandleAsync(Request(Tool.WriteFile, Json(("path", "a.txt"), ("content", "two\n")), checkpoint: before), default)).IsError);
        // A second change in the run must not move the starting point.
        Assert.False(Assert.IsType<ToolRunResult>(await _host.HandleAsync(Request(Tool.WriteFile, Json(("path", "b.txt"), ("content", "new\n")), checkpoint: before), default)).IsError);
        var diff = Assert.IsType<ToolDiffResult>(await _host.HandleAsync(new ToolDiffRequest(Directory_, before, after), default));
        Assert.Contains("-one", diff.Patch);
        Assert.Contains("+two", diff.Patch);
        Assert.Contains("b.txt", diff.Patch);
        var data = JsonDocument.Parse(diff.Data!).RootElement;
        Assert.Equal(2, data.GetProperty("added").GetInt32());
        Assert.Equal(1, data.GetProperty("removed").GetInt32());
    }

    [Fact]
    public async Task ADiffOutsideARepositoryIsNothing()
    {
        Assert.False(Assert.IsType<ToolRunResult>(await _host.HandleAsync(Request(Tool.WriteFile, Json(("path", "a.txt"), ("content", "x")), checkpoint: "refs/x/before"), default)).IsError);
        var diff = Assert.IsType<ToolDiffResult>(await _host.HandleAsync(new ToolDiffRequest(Directory_, "refs/x/before", "refs/x/after"), default));
        Assert.Null(diff.Patch);
    }

    /// <summary>A stdio MCP server in bash, which is enough to speak newline-delimited JSON-RPC.</summary>
    internal static string WriteFakeMcpServer(string directory)
    {
        var script = Path.Combine(directory, "fake-mcp.sh");
        File.WriteAllText(script, """
            while IFS= read -r line; do
              id=$(printf '%s' "$line" | sed -n 's/^{"id":\([0-9]*\).*/\1/p')
              case "$line" in
                *'"method":"initialize"'*) printf '{"jsonrpc":"2.0","id":%s,"result":{"protocolVersion":"2025-11-25","capabilities":{},"serverInfo":{"name":"fake"}}}\n' "$id" ;;
                *'"method":"tools/list"'*) printf '{"jsonrpc":"2.0","id":%s,"result":{"tools":[{"name":"where","description":"Says where it runs.","inputSchema":{"type":"object","properties":{}}}]}}\n' "$id" ;;
                *'"method":"tools/call"'*) printf '{"jsonrpc":"2.0","id":%s,"result":{"content":[{"type":"text","text":"in %s with %s"}]}}\n' "$id" "$(basename "$PWD")" "$MCP_SECRET" ;;
              esac
            done
            """.Replace("\r\n", "\n"));
        return script;
    }

    [Fact]
    public async Task AStdioMcpServerRunsInTheWorkingDirectoryWithItsSecrets()
    {
        if (OperatingSystem.IsWindows()) return;
        var server = new ResolvedMcpServer("local", McpTransport.Stdio, "/bin/bash", [WriteFakeMcpServer(_root)], null, new() { ["MCP_SECRET"] = "s3" });
        var tools = Assert.IsType<McpToolsResult>(await _host.HandleAsync(new McpToolsRequest(7, Directory_, server), default));
        Assert.Equal("where", Assert.Single(tools.Tools).Name);
        var request = Request("where", "{}") with { McpServer = server };
        var result = Assert.IsType<ToolRunResult>(await _host.HandleAsync(request, default));
        Assert.False(result.IsError, result.Text);
        Assert.Equal("in work with s3", result.Text);
    }

    sealed class RecordingUploader : IBlobUploader
    {
        public ConcurrentQueue<byte[]> Uploaded { get; } = new();

        public async Task<BlobRef> UploadAsync(string file, CancellationToken cancellation)
        {
            var content = await File.ReadAllBytesAsync(file, cancellation);
            Uploaded.Enqueue(content);
            return new(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content)), content.Length);
        }
    }

    [Fact]
    public async Task AnImageIsUploadedForTheModel()
    {
        var uploader = new RecordingUploader();
        await using var host = new AgentToolHost(_sink, uploader: uploader);
        var png = Convert.FromBase64String(FakeAgentBackend.RedPng);
        await File.WriteAllBytesAsync(PathOf("red.png"), png);
        var result = Assert.IsType<ToolRunResult>(await host.HandleAsync(Request(Tool.ReadFile, Json(("path", "red.png"))), default));
        var image = Assert.Single(result.Images!);
        Assert.Equal(("image/png", png.Length), (image.MediaType, image.Size));
        Assert.Equal(png, Assert.Single(uploader.Uploaded));

        await File.WriteAllBytesAsync(PathOf("huge.png"), [..png, ..new byte[PromptImages.MaximumBytes]]);
        var huge = Assert.IsType<ToolRunResult>(await host.HandleAsync(Request(Tool.ReadFile, Json(("path", "huge.png"))), default));
        Assert.Null(huge.Images);
        Assert.Contains("larger than", huge.Text);
        Assert.Contains("cannot show", (await Run(Tool.ReadFile, ("path", "red.png"))).Text); // the default host has no uploader
    }

    [Fact]
    public void AnMcpResultKeepsOnlyImagesTheModelsTake()
    {
        var content = McpResults.Read(System.Text.Json.Nodes.JsonNode.Parse($$"""
            {"content":[{"type":"text","text":"Here."},{"type":"image","mimeType":"image/png","data":"{{FakeAgentBackend.RedPng}}"},
              {"type":"image","mimeType":"image/svg+xml","data":"PHN2Zz48L3N2Zz4="},{"type":"image","data":"not base64!"}]}
            """));
        Assert.Equal(Convert.FromBase64String(FakeAgentBackend.RedPng), Assert.Single(content.Images));
        Assert.StartsWith("Here.\n[an image (image/svg+xml) the model cannot take", content.Text);
        Assert.Equal(3, content.Text.Split('\n').Length);
    }

    [Fact]
    public async Task AStdioMcpServerThatCannotStartIsAFailure()
    {
        if (OperatingSystem.IsWindows()) return;
        var server = new ResolvedMcpServer("broken", McpTransport.Stdio, "/no/such/command", [], null, []);
        Assert.Contains("did not start", Assert.IsType<RpcFailure>(await _host.HandleAsync(new McpToolsRequest(7, Directory_, server), default)).Message);
    }
}
