using System.Text.Json.Serialization;

namespace BoSoFa.Protocol;

/// <summary>UI client → server on <c>/api/v1/live</c>, type in <c>t</c>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(LiveSubscribe), "subscribe")]
[JsonDerivedType(typeof(LiveUnsubscribe), "unsubscribe")]
public abstract record LiveClientMessage;

/// <summary>Follow a session from the item after <c>AfterSeq</c>; a reconnecting client passes its last seq.</summary>
public sealed record LiveSubscribe(ulong SessionId, long AfterSeq) : LiveClientMessage;

public sealed record LiveUnsubscribe(ulong SessionId) : LiveClientMessage;

/// <summary>Server → UI client on <c>/api/v1/live</c>, type in <c>t</c>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(LiveSessionUpdate), "session")]
[JsonDerivedType(typeof(LiveDelta), "delta")]
[JsonDerivedType(typeof(LiveError), "error")]
public abstract record LiveServerMessage;

/// <summary>The session summary, the items after the client's cursor, and every pending runtime request (a complete
/// list that replaces the previous one).</summary>
public sealed record LiveSessionUpdate(SessionDto Session, SessionItemDto[] Items, RuntimeRequestDto[] Requests)
    : LiveServerMessage;

/// <summary>Streamed text for the assistant message <c>Ref</c>; discard it once an item with that ref arrives.</summary>
public sealed record LiveDelta(ulong SessionId, string Ref, string Text) : LiveServerMessage;

public sealed record LiveError(ulong SessionId, string Message) : LiveServerMessage;

/// <summary>The live endpoint's path.</summary>
public static class LiveSocket
{
    public const string Path = "/api/v1/live";
}
