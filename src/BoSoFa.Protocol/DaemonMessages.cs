using System.Text.Json.Serialization;

namespace BoSoFa.Protocol;

/// <summary>Messages on the daemon's WebSocket (<c>/api/daemon/v1/connect</c>), one JSON text frame each, with the
/// type in <c>t</c>. The daemon always dials out; the long-poll fallback carries the same messages.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(WelcomeMessage), "welcome")]
[JsonDerivedType(typeof(PingMessage), "ping")]
[JsonDerivedType(typeof(PongMessage), "pong")]
public abstract record DaemonMessage;

/// <summary>First message of the daemon after connecting.</summary>
public sealed record HelloMessage(string DaemonVersion, int ProtocolVersion, string Os, string Arch, string HostName)
    : DaemonMessage;

/// <summary>The server's answer to <see cref="HelloMessage"/>.</summary>
public sealed record WelcomeMessage(ulong MachineId, string ServerVersion) : DaemonMessage;

/// <summary>Keepalive from either side; the other answers with <see cref="PongMessage"/>.</summary>
public sealed record PingMessage(long Sequence) : DaemonMessage;

public sealed record PongMessage(long Sequence) : DaemonMessage;
