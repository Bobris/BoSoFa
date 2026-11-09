using System.Text;
using BoSoFa.Protocol;

namespace BoSoFa.Daemon.Tools;

/// <summary>The multi-file patch format OpenAI's models are trained on. Every change is worked out first, so a patch that does not
/// fit changes nothing.</summary>
sealed class ApplyPatchTool(FileTools files)
{
    const string Begin = "*** Begin Patch", End = "*** End Patch", Add = "*** Add File: ", Delete = "*** Delete File: ", Update = "*** Update File: ",
        Move = "*** Move to: ", EndOfFile = "*** End of File", Hunk = "@@";

    /// <summary>The lines of one change, in order: <c>Keep</c> lines must be there and stay as the file has them.</summary>
    sealed record HunkPart(string? Context, List<(char Op, string Text)> Lines, bool AtEnd)
    {
        public const char Keep = ' ', Remove = '-', Insert = '+';
        public List<string> Before => Lines.Where(line => line.Op != Insert).Select(line => line.Text).ToList();
    }

    public async Task<ToolRunResult> RunAsync(ToolArguments arguments, CancellationToken cancellation)
    {
        var lines = arguments.Required("input").Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
        if (lines.Length < 2 || lines[0].Trim() != Begin || lines[^1].Trim() != End)
            throw new ToolProblem($"A patch starts with \"{Begin}\" and ends with \"{End}\".");
        var writes = new List<(string File, byte[] Content)>();
        var deletes = new List<string>();
        var summary = new StringBuilder();
        var index = 1;
        while (index < lines.Length - 1)
        {
            var line = lines[index];
            if (line.StartsWith(Add, StringComparison.Ordinal))
            {
                var path = line[Add.Length..].Trim();
                var file = files.Resolve(path);
                if (File.Exists(file)) throw new ToolProblem($"{path} already exists.");
                index++;
                var content = new StringBuilder();
                for (; index < lines.Length - 1 && !lines[index].StartsWith("*** ", StringComparison.Ordinal); index++)
                {
                    if (!lines[index].StartsWith('+')) throw new ToolProblem($"In {path}, every added line starts with +.");
                    content.Append(lines[index], 1, lines[index].Length - 1).Append('\n');
                }
                writes.Add((file, Encoding.UTF8.GetBytes(content.ToString())));
                summary.Append($"A {path}\n");
            }
            else if (line.StartsWith(Delete, StringComparison.Ordinal))
            {
                var path = line[Delete.Length..].Trim();
                var file = files.Resolve(path);
                if (!File.Exists(file)) throw new ToolProblem($"There is no file {path}.");
                deletes.Add(file);
                index++;
                summary.Append($"D {path}\n");
            }
            else if (line.StartsWith(Update, StringComparison.Ordinal))
            {
                var path = line[Update.Length..].Trim();
                var file = files.Resolve(path);
                if (!File.Exists(file)) throw new ToolProblem($"There is no file {path}.");
                index++;
                string? moveTo = null;
                if (index < lines.Length - 1 && lines[index].StartsWith(Move, StringComparison.Ordinal)) moveTo = lines[index++][Move.Length..].Trim();
                var hunks = ReadHunks(lines, ref index, path);
                var (text, encoding) = await FileTools.ReadTextAsync(file, cancellation);
                var updated = FileTools.Encode(Apply(text, hunks, path), encoding);
                if (moveTo != null)
                {
                    var target = files.Resolve(moveTo);
                    if (File.Exists(target)) throw new ToolProblem($"{moveTo} already exists.");
                    writes.Add((target, updated));
                    deletes.Add(file);
                    summary.Append($"R {path} -> {moveTo}\n");
                }
                else
                {
                    writes.Add((file, updated));
                    summary.Append($"M {path}\n");
                }
            }
            else throw new ToolProblem($"Line {index + 1} of the patch is not understood: {line}");
        }
        if (writes.Count == 0 && deletes.Count == 0) throw new ToolProblem("The patch changes nothing.");
        foreach (var (file, content) in writes) await FileTools.ReplaceAsync(file, content, cancellation);
        foreach (var file in deletes) File.Delete(file);
        return new("Applied:\n" + summary, false);
    }

    static List<HunkPart> ReadHunks(string[] lines, ref int index, string path)
    {
        var hunks = new List<HunkPart>();
        HunkPart? current = null;
        for (; index < lines.Length - 1; index++)
        {
            var line = lines[index];
            if (line.StartsWith(Hunk, StringComparison.Ordinal))
            {
                current = new(line[Hunk.Length..].Trim() is { Length: > 0 } context ? context : null, [], false);
                hunks.Add(current);
                continue;
            }
            if (line.Trim() == EndOfFile)
            {
                if (current != null) hunks[^1] = current = current with { AtEnd = true };
                continue;
            }
            if (line.StartsWith("*** ", StringComparison.Ordinal)) break;
            if (current == null) hunks.Add(current = new(null, [], false));
            if (line.Length == 0)
            {
                current.Lines.Add((HunkPart.Keep, ""));
                continue;
            }
            if (line[0] is not (HunkPart.Keep or HunkPart.Remove or HunkPart.Insert))
                throw new ToolProblem($"In {path}, this change line starts with neither space, - nor +: {line}");
            current.Lines.Add((line[0], line[1..]));
        }
        if (hunks.Count == 0) throw new ToolProblem($"The update of {path} has no changes.");
        return hunks;
    }

    static string Apply(string text, List<HunkPart> hunks, string path)
    {
        var lineEnding = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var endsWithNewline = text.Length == 0 || text.EndsWith('\n');
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (endsWithNewline) lines.RemoveAt(lines.Count - 1);
        var cursor = 0;
        foreach (var hunk in hunks)
        {
            if (hunk.Context != null)
            {
                var found = Find(lines, [hunk.Context], cursor);
                if (found < 0) throw new ToolProblem($"The context line \"{hunk.Context}\" was not found in {path}.");
                cursor = found + 1;
            }
            var before = hunk.Before;
            if (before.Count == 0)
            {
                // Only additions: after the context line, or at the end of the file.
                var at = hunk.Context != null ? cursor : lines.Count;
                var added = hunk.Lines.Select(line => line.Text).ToList();
                lines.InsertRange(at, added);
                cursor = at + added.Count;
                continue;
            }
            var start = hunk.AtEnd && lines.Count >= before.Count && Matches(lines, before, lines.Count - before.Count, Exact)
                ? lines.Count - before.Count
                : Find(lines, before, cursor);
            if (start < 0) throw new ToolProblem($"These lines were not found in {path}: {before[0]} … Read the file and make the patch match it.");
            var after = new List<string>();
            var position = start;
            foreach (var (op, body) in hunk.Lines)
                switch (op)
                {
                    case HunkPart.Keep:
                        after.Add(lines[position++]); // the file's own line, also when it matched only loosely
                        break;
                    case HunkPart.Remove:
                        position++;
                        break;
                    default:
                        after.Add(body);
                        break;
                }
            lines.RemoveRange(start, before.Count);
            lines.InsertRange(start, after);
            cursor = start + after.Count;
        }
        return string.Join(lineEnding, lines) + (endsWithNewline || lines.Count == 0 ? lineEnding : "");
    }

    static readonly Func<string, string> Exact = line => line;
    static readonly Func<string, string>[] Strictness = [Exact, line => line.TrimEnd(), line => line.Trim()];

    /// <summary>The first place from <paramref name="from"/> where these lines are, as exactly as possible.</summary>
    static int Find(List<string> lines, List<string> wanted, int from)
    {
        foreach (var normalize in Strictness)
            for (var at = from; at + wanted.Count <= lines.Count; at++)
                if (Matches(lines, wanted, at, normalize)) return at;
        return -1;
    }

    static bool Matches(List<string> lines, List<string> wanted, int at, Func<string, string> normalize)
    {
        for (var i = 0; i < wanted.Count; i++)
            if (normalize(lines[at + i]) != normalize(wanted[i])) return false;
        return true;
    }
}
