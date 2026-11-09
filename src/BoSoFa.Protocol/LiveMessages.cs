using System.Text.Json.Serialization;

namespace BoSoFa.Protocol;

/// <summary>UI client → server on <c>/api/v1/live</c>, type in <c>t</c>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(LiveSubscribe), "subscribe")]
[JsonDerivedType(typeof(LiveUnsubscribe), "unsubscribe")]
[JsonDerivedType(typeof(LiveTerminalSubscribe), "terminalSubscribe")]
[JsonDerivedType(typeof(LiveTerminalUnsubscribe), "terminalUnsubscribe")]
[JsonDerivedType(typeof(LiveTerminalInput), "terminalInput")]
[JsonDerivedType(typeof(LiveTerminalResize), "terminalResize")]
public abstract record LiveClientMessage;

/// <summary>Follow a session from the item after <c>AfterSeq</c>; a reconnecting client passes its last seq.</summary>
public sealed record LiveSubscribe(ulong SessionId, long AfterSeq) : LiveClientMessage;

public sealed record LiveUnsubscribe(ulong SessionId) : LiveClientMessage;

/// <summary>Watch a terminal: a snapshot of its retained output comes first. Subscribing again asks for a new snapshot.</summary>
public sealed record LiveTerminalSubscribe(ulong MachineId, string TerminalId) : LiveClientMessage;

public sealed record LiveTerminalUnsubscribe(string TerminalId) : LiveClientMessage;

/// <summary>Keystrokes (base64) for a terminal this connection watches.</summary>
public sealed record LiveTerminalInput(string TerminalId, string Data) : LiveClientMessage;

public sealed record LiveTerminalResize(string TerminalId, int Cols, int Rows) : LiveClientMessage;

/// <summary>Server → UI client on <c>/api/v1/live</c>, type in <c>t</c>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(LiveSessionUpdate), "session")]
[JsonDerivedType(typeof(LiveDelta), "delta")]
[JsonDerivedType(typeof(LiveError), "error")]
[JsonDerivedType(typeof(LiveTerminalSnapshot), "terminalSnapshot")]
[JsonDerivedType(typeof(LiveTerminalOutput), "terminalOutput")]
[JsonDerivedType(typeof(LiveTerminalExited), "terminalExited")]
public abstract record LiveServerMessage;

/// <summary>The session summary, the items after the client's cursor, and every pending runtime request (a complete
/// list that replaces the previous one).</summary>
public sealed record LiveSessionUpdate(SessionDto Session, SessionItemDto[] Items, RuntimeRequestDto[] Requests)
    : LiveServerMessage;

/// <summary>Streamed text for the assistant message <c>Ref</c>; discard it once an item with that ref arrives. With
/// <c>Reset</c>, first drop what came for that ref so far: the model is being asked again.</summary>
public sealed record LiveDelta(ulong SessionId, string Ref, string Text, bool Reset = false) : LiveServerMessage;

public sealed record LiveError(ulong SessionId, string Message) : LiveServerMessage;

/// <summary>The retained output (base64), ending at byte <c>EndOffset</c>. Output may arrive before it: apply what lies
/// beyond <c>EndOffset</c>, and ask for a new snapshot when a later chunk leaves a gap.</summary>
public sealed record LiveTerminalSnapshot(string TerminalId, long EndOffset, string Data, bool Exited, int? ExitCode) : LiveServerMessage;

public sealed record LiveTerminalOutput(string TerminalId, long Offset, string Data) : LiveServerMessage;

/// <summary>The shell ended, or (with <c>Message</c>) the terminal cannot be watched.</summary>
public sealed record LiveTerminalExited(string TerminalId, int? ExitCode, string? Message) : LiveServerMessage;

/// <summary>The live endpoint's path.</summary>
public static class LiveSocket
{
    public const string Path = "/api/v1/live";
}
