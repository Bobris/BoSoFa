using BoSoFa.Protocol;

namespace BoSoFa.Server.Agents;

/// <summary>What a session's model is told, as first worked out: stored once in the transcript, so that every later request sends the same
/// text, as cached prefixes and signed thinking need. <c>Shared</c> is the same for many sessions and goes first.</summary>
public sealed record AgentInstructionsRecord(string Shared, string? Session);

/// <summary>The layers of the system prompt (docs/server-agent.md §7).</summary>
static class AgentInstructions
{
    public static readonly string Base = $"""
        You are an agent running inside {Brand.FullName}. A person follows this session and reads what you write.

        Use your tools rather than guessing. Tools may ask the person for approval first; a refusal is an answer, so do not try the same thing another way.
        Use {AgentToolbox.AskTool} when you need a yes-or-no decision, and {AgentToolbox.NotifyTool} for progress the person should see during long work.
        Ask before doing anything destructive. When you are done, say what you did and what you found.
        """;

    /// <summary>The built-in prompt and the profile's, which replaces it only when it says so and has something to say.</summary>
    public static string Shared(string? profilePrompt, string? mode)
    {
        if (string.IsNullOrWhiteSpace(profilePrompt)) return Base;
        return mode == "replace" ? profilePrompt : Base + "\n\n" + profilePrompt;
    }

    /// <param name="os">The machine's operating system; null without a machine.</param>
    public static string Runtime(DateTimeOffset now, string? os, string workingDirectory, string toolGroup)
    {
        var date = $"Today is {now:yyyy-MM-dd}.";
        if (os == null) return date + " This session has no machine: there is no file system and no shell. You act only through the tools you are given.";
        var shell = os.Contains("win", StringComparison.OrdinalIgnoreCase) && !os.Contains("darwin", StringComparison.OrdinalIgnoreCase) ? "PowerShell" : "bash or sh";
        var where = $"{date} You work on a computer running {os}, in the folder {workingDirectory}. Relative paths start there, and the file tools cannot leave it.";
        return toolGroup switch
        {
            AgentToolGroups.None => where + " You have no tools for its files or its shell.",
            AgentToolGroups.Read => where + " You can read its files, but not change them or run commands.",
            _ => where + $" Commands run in {shell}. Read files before you change them, and keep changes to what was asked. After changing code, run its tests or build when you can, and fix what you broke."
        };
    }

    /// <summary>The part that belongs to this session alone.</summary>
    public static string Session(string runtime, string? projectFile, string? projectText, string? sessionPrompt)
    {
        var parts = new List<string> { runtime };
        if (projectFile != null && !string.IsNullOrWhiteSpace(projectText)) parts.Add($"The project's instructions, from its {projectFile}:\n\n{projectText}");
        if (!string.IsNullOrWhiteSpace(sessionPrompt)) parts.Add(sessionPrompt);
        return string.Join("\n\n", parts);
    }
}
