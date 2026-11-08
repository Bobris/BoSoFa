using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using BoSoFa.Daemon.Harnesses;
using BoSoFa.Protocol;

namespace BoSoFa.Daemon.Sessions;

/// <summary>
/// Git through the <c>git</c> CLI (plan §5.4): session worktrees, and checkpoints of a working tree in hidden refs.
/// A checkpoint is a commit of the whole tree including untracked files, built with a temporary index, so the user's
/// index, HEAD and files are never touched. Ignored files stay out, as in any commit.
/// </summary>
public sealed class GitWorkspace(string directory)
{
    public string Directory { get; } = directory;

    /// <summary>The difference between two checkpoints: the patch (cut to the inline limit) and per-file line counts.</summary>
    public sealed record RunDiff(string Patch, JsonArray Files, int Added, int Removed);

    public bool IsRepository => Run(null, "rev-parse", "--is-inside-work-tree") is { Code: 0, Output: var output } && output.Trim() == "true";

    /// <summary>Creates a worktree on a new branch from HEAD; the error text when git refuses.</summary>
    public string? AddWorktree(string path, string branch)
    {
        var result = Run(null, "worktree", "add", "-b", branch, path);
        return result.Code == 0 ? null : result.Error.Trim();
    }

    /// <summary>A branch name from a prompt: <c>bosofa/42-fix-the-login-page</c>.</summary>
    public static string BranchName(ulong sessionId, string prompt)
    {
        var words = new string(prompt.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(5);
        var slug = string.Join('-', words);
        return slug.Length == 0 ? $"bosofa/{sessionId}" : $"bosofa/{sessionId}-{slug}";
    }

    /// <summary>Commits the working tree as it is under the hidden ref <paramref name="name"/>; null when that fails
    /// (not a repository, git missing): checkpoints are a convenience and never stop a run.</summary>
    public string? Checkpoint(string name)
    {
        var index = Path.Combine(Path.GetTempPath(), "bosofa-index-" + Guid.NewGuid().ToString("N"));
        try
        {
            var head = Run(null, "rev-parse", "--verify", "-q", "HEAD") is { Code: 0, Output: var h } ? h.Trim() : null;
            if (head != null && Run(index, "read-tree", head).Code != 0) return null;
            if (Run(index, "add", "-A").Code != 0) return null;
            var tree = Run(index, "write-tree");
            if (tree.Code != 0) return null;
            var commit = head != null
                ? Run(null, "commit-tree", tree.Output.Trim(), "-p", head, "-m", "BoSoFa checkpoint")
                : Run(null, "commit-tree", tree.Output.Trim(), "-m", "BoSoFa checkpoint");
            if (commit.Code != 0) return null;
            var sha = commit.Output.Trim();
            return Run(null, "update-ref", name, sha).Code == 0 ? sha : null;
        }
        finally
        {
            try
            {
                File.Delete(index);
                File.Delete(index + ".lock");
            }
            catch (IOException) { }
        }
    }

    /// <summary>What changed between two checkpoints; null when nothing did.</summary>
    public RunDiff? Diff(string before, string after)
    {
        var numstat = Run(null, "diff", "--numstat", before, after);
        if (numstat.Code != 0 || numstat.Output.Trim().Length == 0) return null;
        var files = new List<JsonNode?>();
        int added = 0, removed = 0;
        foreach (var line in numstat.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t', 3);
            if (parts.Length != 3) continue;
            // Binary files show "-" instead of line counts.
            int.TryParse(parts[0], out var fileAdded);
            int.TryParse(parts[1], out var fileRemoved);
            added += fileAdded;
            removed += fileRemoved;
            files.Add(new JsonObject { ["path"] = parts[2], ["added"] = fileAdded, ["removed"] = fileRemoved });
        }
        var patch = Run(null, "diff", before, after).Output;
        if (patch.Length > ItemReport.InlineLimit)
            patch = patch[..ItemReport.InlineLimit] + $"\n… ({patch.Length - ItemReport.InlineLimit} more characters)";
        return new(patch, new JsonArray(files.ToArray()), added, removed);
    }

    (int Code, string Output, string Error) Run(string? indexFile, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = Directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach (var (name, value) in HarnessEnvironment.Clean()) start.Environment[name] = value;
        // commit-tree needs an identity, and a checkpoint must not depend on the user's git config having one.
        foreach (var variable in new[] { "GIT_AUTHOR_NAME", "GIT_COMMITTER_NAME" }) start.Environment[variable] = "BoSoFa";
        foreach (var variable in new[] { "GIT_AUTHOR_EMAIL", "GIT_COMMITTER_EMAIL" }) start.Environment[variable] = "bosofa@localhost";
        if (indexFile != null) start.Environment["GIT_INDEX_FILE"] = indexFile;
        try
        {
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output, error.GetAwaiter().GetResult());
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            return (-1, "", exception.Message); // git is not installed
        }
    }
}
