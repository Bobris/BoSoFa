using BoSoFa.Protocol;

namespace BoSoFa.Daemon.Sessions;

/// <summary>One BoSoFa session on one harness (plan §5.4). Implementations translate the harness's own protocol into
/// <see cref="ISessionSink"/> reports and deltas; <see cref="SessionHost"/> owns them and applies the server's
/// commands. Calls for one session never overlap.</summary>
public interface IHarnessSession : IAsyncDisposable
{
    SessionState State { get; }

    /// <summary>Starts a turn, or (while one runs) queues the message or steers the running turn with it.</summary>
    Task SendAsync(string text, DispatchMode mode);

    Task InterruptAsync();

    /// <summary>Answers a runtime request the harness raised (reported as <see cref="ApprovalRequestedReport"/>).</summary>
    Task AnswerAsync(string key, bool allow, string? message);

    /// <summary>Ends the harness process; a later message starts it again, resuming the conversation.</summary>
    Task StopAsync();
}
