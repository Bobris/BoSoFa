using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using BoSoFa.Daemon.Harnesses;
using BoSoFa.Daemon.Sessions;
using BoSoFa.Protocol;
using ToolNames = BoSoFa.Protocol.MachineTools;

namespace BoSoFa.Daemon.Tools;

/// <summary>The file tools of a server agent session, always inside its working directory (<see cref="PathGuard"/>).</summary>
sealed class FileTools(string workingDirectory, bool useRipgrep = true)
{
    const int LongestLine = 2000, DefaultLines = 2000, MaximumEntries = 1000, MaximumMatches = 500;
    const long LargestSearchedFile = 4 * 1024 * 1024;
    static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { ".git", "node_modules" };

    readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));

    /// <summary>The full path, or a problem the model can read.</summary>
    public string Resolve(string path) =>
        PathGuard.Resolve([_root], path, _root) ?? throw new ToolProblem($"{path} is outside the session's working directory, {_root}.");

    string Relative(string path) => Path.GetRelativePath(_root, path).Replace('\\', '/');

    public async Task<ToolRunResult> ReadAsync(ToolArguments arguments, CancellationToken cancellation)
    {
        var file = Resolve(arguments.Required("path"));
        if (Directory.Exists(file)) throw new ToolProblem($"{Relative(file)} is a folder; use {ToolNames.ListDir}.");
        if (!File.Exists(file)) throw new ToolProblem($"There is no file {Relative(file)}.");
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var header = new byte[8192];
        var headerLength = await stream.ReadAtLeastAsync(header, header.Length, false, cancellation);
        if (PromptImages.MediaTypeOf(header.AsSpan(0, headerLength)) is { } mediaType)
            return new($"{Relative(file)} is an image ({mediaType}, {stream.Length} bytes). This tool cannot show images yet.", false);
        if (header.AsSpan(0, headerLength).Contains((byte)0)) return new($"{Relative(file)} is a binary file of {stream.Length} bytes.", false);
        stream.Position = 0;
        var offset = Math.Max(arguments.Number("offset") ?? 1, 1);
        var limit = Math.Max(arguments.Number("limit") ?? DefaultLines, 1);
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        var text = new StringBuilder();
        var number = 0;
        var shown = 0;
        while (await reader.ReadLineAsync(cancellation) is { } line)
        {
            number++;
            if (number < offset) continue;
            var entry = $"{number}\t{(line.Length > LongestLine ? line[..LongestLine] + " [line cut]" : line)}\n";
            if (shown == limit || text.Length + entry.Length > ToolNames.OutputLimit)
                return new(text + $"[More lines follow. Continue with offset {number}.]", false);
            text.Append(entry);
            shown++;
        }
        if (shown > 0) return new(text.ToString(), false);
        return new(number == 0 ? "The file is empty." : $"The file has only {number} lines.", false);
    }

    public ToolRunResult List(ToolArguments arguments)
    {
        var directory = Resolve(arguments.Text("path") ?? ".");
        if (!Directory.Exists(directory)) throw new ToolProblem($"There is no folder {Relative(directory)}.");
        var entries = new DirectoryInfo(directory).EnumerateFileSystemInfos().OrderByDescending(entry => entry is DirectoryInfo)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var text = new StringBuilder();
        foreach (var entry in entries.Take(MaximumEntries))
        {
            var line = entry is FileInfo file ? $"{entry.Name}\t{file.Length} bytes\n" : entry.Name + "/\n";
            if (text.Length + line.Length > ToolNames.OutputLimit) return new(text + $"[Cut: the folder has {entries.Count} entries.]", false);
            text.Append(line);
        }
        if (entries.Count > MaximumEntries) text.Append($"[Cut: the folder has {entries.Count} entries.]");
        return new(text.Length == 0 ? "The folder is empty." : text.ToString(), false);
    }

    /// <summary>The files under a folder that a person would call theirs, relative to the working directory: git's view in a
    /// repository, so build output stays out, else a walk that does not follow links.</summary>
    IEnumerable<string> FilesUnder(string directory)
    {
        var listed = new GitWorkspace(directory).ListFiles();
        if (listed != null)
            return listed.Select(file => Path.GetFullPath(file, directory)).Where(File.Exists).Select(Relative).Order(StringComparer.Ordinal);
        return Walk(directory).Select(Relative).Order(StringComparer.Ordinal);
    }

    static IEnumerable<string> Walk(string directory)
    {
        var pending = new Stack<string>([directory]);
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, ReturnSpecialDirectories = false };
        while (pending.TryPop(out var current))
            foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos("*", options))
                if (entry is FileInfo) yield return entry.FullName;
                else if (entry.LinkTarget == null && !SkippedFolders.Contains(entry.Name)) pending.Push(entry.FullName);
    }

    public ToolRunResult Glob(ToolArguments arguments, CancellationToken cancellation)
    {
        var directory = Resolve(arguments.Text("path") ?? ".");
        if (!Directory.Exists(directory)) throw new ToolProblem($"There is no folder {Relative(directory)}.");
        var pattern = GlobPattern(arguments.Required("pattern"));
        var prefix = Relative(directory) is "." ? "" : Relative(directory) + "/";
        var text = new StringBuilder();
        var count = 0;
        foreach (var file in FilesUnder(directory))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!pattern.IsMatch(file.StartsWith(prefix, StringComparison.Ordinal) ? file[prefix.Length..] : file)) continue;
            if (++count > MaximumMatches || text.Length + file.Length + 1 > ToolNames.OutputLimit) return new(text + "[More files match. Narrow the pattern.]", false);
            text.Append(file).Append('\n');
        }
        return new(count == 0 ? "No file matches." : text.ToString(), false);
    }

    /// <summary><c>**</c> crosses folders, <c>*</c> and <c>?</c> do not, <c>{a,b}</c> is either. A pattern without a slash matches a name anywhere.</summary>
    public static Regex GlobPattern(string glob)
    {
        glob = glob.Replace('\\', '/');
        if (!glob.Contains('/')) glob = "**/" + glob;
        if (glob.StartsWith("./", StringComparison.Ordinal)) glob = glob[2..];
        var regex = new StringBuilder("^");
        var braces = 0;
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*' when i + 2 < glob.Length && glob[i + 1] == '*' && glob[i + 2] == '/':
                    regex.Append("(?:.*/)?");
                    i += 2;
                    break;
                case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                    regex.Append(".*");
                    i++;
                    break;
                case '*':
                    regex.Append("[^/]*");
                    break;
                case '?':
                    regex.Append("[^/]");
                    break;
                case '{':
                    regex.Append("(?:");
                    braces++;
                    break;
                case '}' when braces > 0:
                    regex.Append(')');
                    braces--;
                    break;
                case ',' when braces > 0:
                    regex.Append('|');
                    break;
                default:
                    regex.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        if (braces > 0) throw new ToolProblem("The pattern has an unclosed {.");
        return new(regex.Append('$').ToString(), OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None);
    }

    public async Task<ToolRunResult> GrepAsync(ToolArguments arguments, CancellationToken cancellation)
    {
        var pattern = arguments.Required("pattern");
        var target = Resolve(arguments.Text("path") ?? ".");
        if (!Directory.Exists(target) && !File.Exists(target)) throw new ToolProblem($"There is no {Relative(target)}.");
        var ignoreCase = arguments.Flag("ignore_case");
        var glob = arguments.Text("glob");
        return useRipgrep && HarnessEnvironment.FindExecutable(null, OperatingSystem.IsWindows() ? ["rg.exe"] : ["rg"], "/opt/homebrew/bin", "/usr/local/bin") is { } ripgrep
            ? await RipgrepAsync(ripgrep, pattern, target, ignoreCase, glob, cancellation)
            : await SearchAsync(pattern, target, ignoreCase, glob, cancellation);
    }

    async Task<ToolRunResult> RipgrepAsync(string executable, string pattern, string target, bool ignoreCase, string? glob, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in new[] { "--no-heading", "--with-filename", "--line-number", "--color=never", "--max-columns=500", "--max-columns-preview", "--sort=path", "--path-separator=/" })
            start.ArgumentList.Add(argument);
        if (ignoreCase) start.ArgumentList.Add("--ignore-case");
        if (glob != null)
        {
            start.ArgumentList.Add("--glob");
            start.ArgumentList.Add(glob);
        }
        start.ArgumentList.Add("--regexp");
        start.ArgumentList.Add(pattern);
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(Relative(target));
        start.Environment.Clear();
        foreach (var (name, value) in HarnessEnvironment.Clean()) start.Environment[name] = value;
        using var process = Process.Start(start) ?? throw new IOException("ripgrep did not start.");
        await using var _ = cancellation.Register(() => Kill(process));
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var text = new StringBuilder();
        var count = 0;
        while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
        {
            var entry = line.StartsWith("./", StringComparison.Ordinal) ? line[2..] : line;
            if (++count > MaximumMatches || text.Length + entry.Length + 1 > ToolNames.OutputLimit)
            {
                Kill(process);
                return new(text + "[More lines match. Narrow the search.]", false);
            }
            text.Append(entry).Append('\n');
        }
        await process.WaitForExitAsync(CancellationToken.None);
        cancellation.ThrowIfCancellationRequested();
        return process.ExitCode switch
        {
            0 => new(text.ToString(), false),
            1 => new("No line matches.", false),
            _ => new((await error).Trim(), true)
        };
    }

    static void Kill(Process process)
    {
        try { process.Kill(true); }
        catch (Exception problem) when (problem is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    /// <summary>For machines without ripgrep.</summary>
    async Task<ToolRunResult> SearchAsync(string pattern, string target, bool ignoreCase, string? glob, CancellationToken cancellation)
    {
        Regex regex;
        try { regex = new(pattern, ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None, TimeSpan.FromSeconds(2)); }
        catch (ArgumentException problem) { throw new ToolProblem($"The pattern is not valid: {problem.Message}"); }
        var fileFilter = glob == null ? null : GlobPattern(glob);
        var files = File.Exists(target) ? [Relative(target)] : FilesUnder(target);
        var text = new StringBuilder();
        var count = 0;
        foreach (var file in files)
        {
            cancellation.ThrowIfCancellationRequested();
            if (fileFilter?.IsMatch(file) == false) continue;
            var full = Path.Combine(_root, file);
            FileInfo info = new(full);
            if (!info.Exists || info.Length > LargestSearchedFile) continue;
            string[] lines;
            try
            {
                var content = await File.ReadAllBytesAsync(full, cancellation);
                if (content.AsSpan(0, Math.Min(content.Length, 8192)).Contains((byte)0)) continue;
                lines = Encoding.UTF8.GetString(content).Split('\n');
            }
            catch (Exception problem) when (problem is IOException or UnauthorizedAccessException) { continue; }
            for (var i = 0; i < lines.Length; i++)
            {
                try { if (!regex.IsMatch(lines[i])) continue; }
                catch (RegexMatchTimeoutException) { throw new ToolProblem("The pattern takes too long; use a simpler one."); }
                var line = lines[i].TrimEnd('\r');
                var entry = $"{file}:{i + 1}:{(line.Length > 500 ? line[..500] + " [line cut]" : line)}\n";
                if (++count > MaximumMatches || text.Length + entry.Length > ToolNames.OutputLimit) return new(text + "[More lines match. Narrow the search.]", false);
                text.Append(entry);
            }
        }
        return new(count == 0 ? "No line matches." : text.ToString(), false);
    }

    /// <summary>Replaces a file in one step, keeping its permissions. A link is written through, as it stays inside the guard.</summary>
    public static async Task ReplaceAsync(string file, byte[] content, CancellationToken cancellation)
    {
        var info = new FileInfo(file);
        Directory.CreateDirectory(info.DirectoryName!);
        if (info.LinkTarget != null)
        {
            await File.WriteAllBytesAsync(file, content, cancellation);
            return;
        }
        var temporary = Path.Combine(info.DirectoryName!, $".{Brand.Id}-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, content, cancellation);
            if (info.Exists && !OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, info.UnixFileMode);
            File.Move(temporary, file, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    public async Task<ToolRunResult> WriteAsync(ToolArguments arguments, CancellationToken cancellation)
    {
        var file = Resolve(arguments.Required("path"));
        var content = Encoding.UTF8.GetBytes(arguments.Text("content") ?? throw new ToolProblem("The argument content is missing."));
        if (Directory.Exists(file)) throw new ToolProblem($"{Relative(file)} is a folder.");
        var existed = File.Exists(file);
        await ReplaceAsync(file, content, cancellation);
        return new($"{(existed ? "Replaced" : "Created")} {Relative(file)} ({content.Length} bytes).", false);
    }

    public async Task<ToolRunResult> EditAsync(ToolArguments arguments, CancellationToken cancellation)
    {
        var file = Resolve(arguments.Required("path"));
        var oldText = arguments.Required("old_text");
        var newText = arguments.Text("new_text") ?? throw new ToolProblem("The argument new_text is missing.");
        if (oldText.Length == 0) throw new ToolProblem("old_text is empty; use write_file to create a file.");
        if (!File.Exists(file)) throw new ToolProblem($"There is no file {Relative(file)}.");
        var (content, encoding) = await ReadTextAsync(file, cancellation);
        // A model writes \n; a file from Windows has \r\n.
        if (!content.Contains(oldText, StringComparison.Ordinal) && content.Contains("\r\n", StringComparison.Ordinal))
        {
            oldText = oldText.Replace("\n", "\r\n", StringComparison.Ordinal);
            newText = newText.Replace("\n", "\r\n", StringComparison.Ordinal);
        }
        var count = Occurrences(content, oldText);
        if (count == 0) throw new ToolProblem($"old_text does not occur in {Relative(file)}. Read the file and copy the text exactly.");
        var all = arguments.Flag("replace_all");
        if (count > 1 && !all) throw new ToolProblem($"old_text occurs {count} times in {Relative(file)}. Add more of the surrounding text, or set replace_all.");
        await ReplaceAsync(file, Encode(content.Replace(oldText, newText, StringComparison.Ordinal), encoding), cancellation);
        return new($"Replaced {count} {(count == 1 ? "occurrence" : "occurrences")} in {Relative(file)}.", false);
    }

    static int Occurrences(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    /// <summary>The text, and the encoding to write it back with; a file that is not UTF-8 is refused rather than damaged.</summary>
    public static async Task<(string Text, UTF8Encoding Encoding)> ReadTextAsync(string file, CancellationToken cancellation)
    {
        var bytes = await File.ReadAllBytesAsync(file, cancellation);
        var hasMark = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        var encoding = new UTF8Encoding(hasMark, throwOnInvalidBytes: true);
        try { return (encoding.GetString(bytes, hasMark ? 3 : 0, bytes.Length - (hasMark ? 3 : 0)), encoding); }
        catch (DecoderFallbackException) { throw new ToolProblem($"{Path.GetFileName(file)} is not UTF-8 text, so it cannot be edited with this tool."); }
    }

    public static byte[] Encode(string text, UTF8Encoding encoding) => [..encoding.GetPreamble(), ..encoding.GetBytes(text)];
}
