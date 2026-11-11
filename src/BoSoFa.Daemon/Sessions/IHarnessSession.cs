using BoSoFa.Protocol;

namespace BoSoFa.Daemon.Sessions;

/// <summary>An image of a prompt, as a file on this machine.</summary>
public sealed record LocalImage(string Path, string MediaType);

/// <summary>What a person sent: text and any images.</summary>
public sealed record HarnessPrompt(string Text, IReadOnlyList<LocalImage> Images)
{
    public static implicit operator HarnessPrompt(string text) => new(text, []);

    /// <summary>What asks a harness to summarize its conversation: the slash command Claude Code and OpenCode take as a prompt,
    /// which the Codex adapter recognizes by identity and turns into its own request.</summary>
    public static readonly HarnessPrompt Compaction = new("/compact", []);

    /// <summary>What asks a harness to start its conversation over: Claude Code's own slash command, which the other adapters
    /// recognize by identity and turn into a new conversation of their own.</summary>
    public static readonly HarnessPrompt Clear = new("/clear", []);

    /// <summary>What a side question (/btw) is wrapped in, so the harness answers it instead of going on with the work.</summary>
    public static string SideQuestion(string question) => $"""
        The person asks this on the side, while the work goes on. Answer it from what you know so far, briefly. Do not use tools, and do
        not continue the work: this question and your answer are not kept in the conversation.

        {question}
        """;
}

/// <summary>One BoSoFa session on one harness (plan §5.4). Implementations translate the harness's own protocol into
/// <see cref="ISessionSink"/> reports and deltas; <see cref="SessionHost"/> owns them and applies the server's
/// commands. Calls for one session never overlap.</summary>
public interface IHarnessSession : IAsyncDisposable
{
    SessionState State { get; }

    /// <summary>Starts a turn, or (while one runs) queues the message or steers the running turn with it.</summary>
    Task SendAsync(HarnessPrompt prompt, DispatchMode mode);

    Task InterruptAsync();

    /// <summary>Summarizes the conversation so far to free the context, after the running turn if one runs.</summary>
    Task CompactAsync();

    /// <summary>Starts the conversation over, after the running turn if one runs, and reports <see cref="SessionItemKind.Cleared"/>.</summary>
    Task ClearAsync();

    /// <summary>Answers a side question (/btw) from the conversation so far in a process of its own, beside any running turn and without
    /// adding either to the conversation. Throws, with a message for the person, when it cannot.</summary>
    Task<string> AnswerSideQuestionAsync(string question, CancellationToken cancellation);

    /// <summary>Answers a runtime request the harness raised (reported as <see cref="ApprovalRequestedReport"/>).</summary>
    Task AnswerAsync(string key, bool allow, string? message);

    /// <summary>Ends the harness process; a later message starts it again, resuming the conversation.</summary>
    Task StopAsync();
}
