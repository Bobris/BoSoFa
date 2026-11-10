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

    /// <summary>Answers a runtime request the harness raised (reported as <see cref="ApprovalRequestedReport"/>).</summary>
    Task AnswerAsync(string key, bool allow, string? message);

    /// <summary>Ends the harness process; a later message starts it again, resuming the conversation.</summary>
    Task StopAsync();
}
