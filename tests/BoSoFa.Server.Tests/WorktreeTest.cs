using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using BoSoFa.Daemon;
using BoSoFa.Daemon.Harnesses.Claude;
using BoSoFa.Daemon.Sessions;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Tests;

[Collection("Replay")]
public class WorktreeTest
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    static string Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {error.Result}");
        return output.Trim();
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
    public async Task SessionRunsInItsOwnWorktreeAndEachRunReportsItsDiff()
    {
        ReplayTransport.SignalTimeoutOverride = TimeSpan.Zero;
        await using var server = await TestServer.StartAsync();
        var root = Path.Combine(Path.GetTempPath(), "bosofa-worktree-" + Guid.NewGuid().ToString("N"));
        var repository = Directory.CreateDirectory(Path.Combine(root, "repo")).FullName;
        Git(repository, "init", "-q", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "one\ntwo\n");
        await File.WriteAllTextAsync(Path.Combine(repository, ".gitignore"), "ignored.txt\n");
        Git(repository, "add", "-A");
        Git(repository, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "first");
        try
        {
            using var boris = server.Client();
            (await boris.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("boris@example.com", null))).EnsureSuccessStatusCode();
            var daemonDirectory = Path.Combine(root, "daemon");
            var state = await SessionTest.EnrollAsync(server.BaseAddress, boris, daemonDirectory);
            using var key = new DaemonStore(daemonDirectory).LoadOrCreateKey();
            using var outbox = new Outbox(Path.Combine(daemonDirectory, "outbox"));
            await using var host = new SessionHost(daemonDirectory, outbox, new(ReplayFixture: SessionTest.Fixture), TextWriter.Null);
            using var stop = new CancellationTokenSource();
            var running = new DaemonLink(state, key, TextWriter.Null, outbox, host).RunAsync(stop.Token);

            var session = (await (await boris.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest(state.MachineId,
                Harnesses.Replay, repository, "Fix the README, please!", null, null, null, UseWorktree: true)))
                .Content.ReadFromJsonAsync<SessionDto>())!;
            var requests = await Eventually(() => boris.GetFromJsonAsync<RuntimeRequestDto[]>($"/api/v1/sessions/{session.Id}/requests")!, r => r!.Length > 0);
            var running1 = await boris.GetFromJsonAsync<SessionDto>($"/api/v1/sessions/{session.Id}");
            Assert.Equal($"{Brand.Id}/{session.Id}-fix-the-readme-please", running1!.Branch);
            var worktree = running1.WorkingDirectory;
            Assert.NotEqual(repository, worktree);
            Assert.Equal(running1.Branch, Git(worktree, "branch", "--show-current"));

            // The agent's work, in the worktree: an edit, a new file and an ignored one.
            await File.WriteAllTextAsync(Path.Combine(worktree, "README.md"), "one\nTWO\nthree\n");
            await File.WriteAllTextAsync(Path.Combine(worktree, "new.txt"), "hello\n");
            await File.WriteAllTextAsync(Path.Combine(worktree, "ignored.txt"), "secret\n");
            (await boris.PostAsJsonAsync($"/api/v1/sessions/{session.Id}/requests/{requests![0].Id}/answer",
                new AnswerRuntimeRequest(true, null))).EnsureSuccessStatusCode();

            var items = await Eventually(() => boris.GetFromJsonAsync<SessionItemDto[]>($"/api/v1/sessions/{session.Id}/items")!,
                all => all!.Any(item => item.Kind == SessionItemKind.Diff));
            var diff = items!.Single(item => item.Kind == SessionItemKind.Diff);
            Assert.Contains("+TWO", diff.Text);
            Assert.Contains("+hello", diff.Text);
            Assert.DoesNotContain("secret", diff.Text);
            using var data = JsonDocument.Parse(diff.Data!);
            Assert.Equal(["README.md", "new.txt"], data.RootElement.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()));
            Assert.Equal(3, data.RootElement.GetProperty("added").GetInt32());
            Assert.Equal(1, data.RootElement.GetProperty("removed").GetInt32());

            // Checkpoints live in hidden refs; the worktree's index and HEAD are untouched, the original repo too.
            Assert.Contains($"refs/{Brand.Id}/{session.Id}/1-before", Git(repository, "for-each-ref", $"refs/{Brand.Id}"));
            Assert.Contains($"refs/{Brand.Id}/{session.Id}/1-after", Git(repository, "for-each-ref", $"refs/{Brand.Id}"));
            Assert.Equal("first", Git(worktree, "log", "-1", "--format=%s"));
            Assert.Equal(" M README.md\n?? new.txt", Git(worktree, "status", "--porcelain").Replace("\r\n", "\n").Insert(0, " "));
            Assert.Equal("", Git(repository, "status", "--porcelain"));
            Assert.Equal("one\ntwo\n", (await File.ReadAllTextAsync(Path.Combine(repository, "README.md"))).Replace("\r\n", "\n"));

            await stop.CancelAsync();
            await running.WaitAsync(Timeout);
        }
        finally
        {
            ReplayTransport.SignalTimeoutOverride = null;
            try { Directory.Delete(root, true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void AWorktreeBranchNameComesFromThePrompt()
    {
        Assert.Equal($"{Brand.Id}/7-fix-the-login-page-now", GitWorkspace.BranchName(7, "Fix the LOGIN page, now! And more words"));
        Assert.Equal($"{Brand.Id}/7", GitWorkspace.BranchName(7, "???"));
    }
}
