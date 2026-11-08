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
    public const int Current = 1;
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(VersionInfo))]
[JsonSerializable(typeof(UserDto))]
[JsonSerializable(typeof(UserDto[]))]
public sealed partial class ProtocolJsonContext : JsonSerializerContext;
