using System.Text.Json;
using System.Text.Json.Serialization;

namespace BoSoFa.Protocol;

/// <summary>Build identity of a BoSoFa component, served anonymously so daemons and clients can check compatibility.</summary>
public sealed record VersionInfo(string Component, string Version, int ProtocolVersion);

[JsonConverter(typeof(JsonStringEnumConverter<UserRole>))]
public enum UserRole
{
    Member,
    Admin
}

/// <summary>A person known to the server. Names and emails are visible to every signed-in user (single tenant).</summary>
public sealed record UserDto(ulong Id, string Name, string Email, UserRole Role);

public static class ProtocolVersion
{
    /// <summary>Bumped on every incompatible change of the daemon or client wire protocol.</summary>
    public const int Current = 2;
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(VersionInfo))]
[JsonSerializable(typeof(UserDto))]
[JsonSerializable(typeof(UserDto[]))]
[JsonSerializable(typeof(EnrollStartRequest))]
[JsonSerializable(typeof(EnrollStartResponse))]
[JsonSerializable(typeof(EnrollPollRequest))]
[JsonSerializable(typeof(EnrollPollResponse))]
[JsonSerializable(typeof(EnrollmentDto))]
[JsonSerializable(typeof(DaemonTokenRequest))]
[JsonSerializable(typeof(DaemonTokenResponse))]
[JsonSerializable(typeof(MachineDto))]
[JsonSerializable(typeof(MachineDto[]))]
[JsonSerializable(typeof(DaemonMessage))]
[JsonSerializable(typeof(MachineCommand))]
[JsonSerializable(typeof(OutboxRecord))]
[JsonSerializable(typeof(OutboxRecord[]))]
[JsonSerializable(typeof(SessionDto))]
[JsonSerializable(typeof(SessionDto[]))]
[JsonSerializable(typeof(SessionItemDto[]))]
[JsonSerializable(typeof(RuntimeRequestDto[]))]
[JsonSerializable(typeof(CreateSessionRequest))]
[JsonSerializable(typeof(SendMessageRequest))]
[JsonSerializable(typeof(AnswerRuntimeRequest))]
[JsonSerializable(typeof(HarnessAccountDto))]
[JsonSerializable(typeof(HarnessAccountDto[]))]
[JsonSerializable(typeof(CreateHarnessAccountRequest))]
[JsonSerializable(typeof(AccountLeaseRequest))]
[JsonSerializable(typeof(AccountLeaseResponse))]
[JsonSerializable(typeof(LiveClientMessage))]
[JsonSerializable(typeof(LiveServerMessage))]
public sealed partial class ProtocolJsonContext : JsonSerializerContext;
