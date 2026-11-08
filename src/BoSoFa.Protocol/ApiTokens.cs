using System.Text.Json.Serialization;

namespace BoSoFa.Protocol;

/// <summary>What a script's token may do with its owner's rights.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ApiTokenScope>))]
public enum ApiTokenScope
{
    /// <summary>Only reads: no changes, nothing on a machine.</summary>
    Read,

    /// <summary>Everything its owner may do, except managing tokens.</summary>
    Control
}

public sealed record ApiTokenDto(ulong Id, string Name, ApiTokenScope Scope, DateTime CreatedAt, DateTime? ExpiresAt);

public sealed record CreateApiTokenRequest(string Name, ApiTokenScope Scope, int? ExpiresInDays = null);

/// <summary>The only moment the token itself is shown.</summary>
public sealed record CreatedApiToken(string Token, ApiTokenDto Details);
