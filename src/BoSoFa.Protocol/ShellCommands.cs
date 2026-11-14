using System.Globalization;
using System.Text;

namespace BoSoFa.Protocol;

/// <summary>A command the person runs in a session's folder on its machine, typed after <c>!</c> in the prompt (docs/shell-commands.md).</summary>
public sealed record RunShellCommandRequest(string Command);

/// <summary>A command run from the prompt of a session not started yet: in the folder of the machine the draft chose, with the variables of
/// its profile. Its output can go with the prompt that starts the session (<see cref="CreateSessionRequest.ShellOutputs"/>).</summary>
public sealed record RunDraftShellCommandRequest(string Command, ulong MachineId, string WorkingDirectory, string? ProfileName = null);

/// <summary>The <c>Data</c> of a <see cref="SessionItemKind.ShellOutput"/> item, whose <c>Text</c> is the output.</summary>
/// <param name="State">One of <see cref="ShellCommands.Done"/>, <see cref="ShellCommands.Stopped"/>, <see cref="ShellCommands.TimedOut"/> and
/// <see cref="ShellCommands.Failed"/>.</param>
/// <param name="ExitCode">Set for a command that ended by itself.</param>
public sealed record ShellOutputData(string Command, string Folder, string State, int? ExitCode, long Milliseconds);

public static class ShellCommands
{
    public const int MaximumCommandLength = 16_384;

    /// <summary>Commands that run at once in one session.</summary>
    public const int MaximumRunning = 4;

    /// <summary>Outputs one message may carry.</summary>
    public const int MaximumPerMessage = 8;

    /// <summary>Commands a draft keeps; the oldest that ended go first.</summary>
    public const int MaximumPerDraft = 50;

    public const string Done = "done", Stopped = "stopped", TimedOut = "timedOut", Failed = "failed";

    /// <summary>What ties a command's streamed output and its <see cref="SessionItemKind.ShellOutput"/> to its <see cref="SessionItemKind.ShellCommand"/>.</summary>
    public static string RefOf(long commandSeq) => "shell-" + commandSeq.ToString(CultureInfo.InvariantCulture);

    /// <summary>The command's seq, from <see cref="RefOf"/>; null for another ref.</summary>
    public static long? CommandSeqOf(string? itemRef) =>
        itemRef != null && itemRef.StartsWith("shell-", StringComparison.Ordinal) &&
        long.TryParse(itemRef.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out var seq)
            ? seq
            : null;

    static string Ending(ShellOutputData data, string output) => data.State switch
    {
        Done => $"it exited with code {data.ExitCode}",
        Stopped => "the user stopped it before it ended",
        TimedOut => "it ran out of time and was stopped",
        _ => $"it could not run: {output.Trim()}"
    };

    /// <summary>What the model reads of one command: where it ran, how it ended and what it printed.</summary>
    public static string Context(ShellOutputData data, string output)
    {
        var text = new StringBuilder();
        text.Append("The user ran this command in a shell, in ").Append(data.Folder).Append("; ").Append(Ending(data, output)).Append(".\n\n");
        text.Append("$ ").Append(data.Command).Append('\n');
        if (data.State != Failed) text.Append(output.Length == 0 ? "(no output)" : output.TrimEnd('\n'));
        return text.ToString().TrimEnd('\n') + "\n";
    }

    /// <summary>The message as its harness gets it: the outputs first, each shortened in its middle when all of them would not fit in
    /// <paramref name="limit"/> characters, then the person's own text.</summary>
    public static string Prepend(string text, IReadOnlyList<(ShellOutputData Data, string Output)> outputs, int limit)
    {
        if (outputs.Count == 0) return text;
        const string separator = "\n";
        var room = Math.Max(0, limit - text.Length - outputs.Count * separator.Length - outputs.Sum(output => Context(output.Data, "").Length));
        var share = room / outputs.Count;
        var result = new StringBuilder();
        foreach (var (data, output) in outputs)
            result.Append(Context(data, Shorten(output, share))).Append(separator);
        return result.Append(text).ToString();
    }

    static string Shorten(string output, int length)
    {
        if (output.Length <= length) return output;
        var marker = $"\n[… {output.Length - length} characters cut …]\n";
        if (length <= marker.Length) return "";
        var keep = length - marker.Length;
        var head = keep / 3;
        return output[..head] + marker + output[^(keep - head)..];
    }
}
