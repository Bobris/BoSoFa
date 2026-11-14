using System.Text.Json.Serialization;

namespace BoSoFa.Protocol;

/// <summary>Where a skill of a profile or a session comes from (docs/skillshare.md §4).</summary>
public static class SkillSources
{
    /// <summary>A skill of the SkillShare the server signs in through: <see cref="SkillRef.Name"/> is its SkillShare id, and
    /// <see cref="SkillRef.Version"/> a version or <see cref="Latest"/>.</summary>
    public const string SkillShare = "skillshare";

    /// <summary>The newest version when the session starts.</summary>
    public const string Latest = "latest";

    /// <summary>A profile names a person's SkillShare MCP server as <c>skillshare:&lt;provider&gt;</c> among its MCP servers.</summary>
    public const string McpPrefix = "skillshare:";
}

[JsonConverter(typeof(JsonStringEnumConverter<SkillShareGrantStatus>))]
public enum SkillShareGrantStatus
{
    /// <summary>The person has not signed in through SkillShare since the server started using it.</summary>
    None,
    Active,

    /// <summary>SkillShare refused the grant (revoked, expired, or the person was disabled): signing in again makes a new one.</summary>
    Expired
}

/// <summary>Whether this server signs in through SkillShare, and what that gives the person asking.</summary>
/// <param name="Name">SkillShare's own name, for the UI.</param>
/// <param name="McpSetupUrl">Where a person connects their tools in SkillShare.</param>
/// <param name="LastSyncAt">For admins: the last time the skill catalog was read.</param>
/// <param name="ActiveGrants">For admins: how many people have a working grant.</param>
public sealed record SkillShareStatusDto(bool Enabled, string? Name, string? Url, SkillShareGrantStatus Grant, string? McpSetupUrl = null,
    int Skills = 0, DateTime? LastSyncAt = null, string? LastSyncError = null, int? ActiveGrants = null);

public sealed record SkillShareSkillVersionDto(string Version, DateTime PublishedAt, bool Latest, string Sha256);

/// <summary>A skill of SkillShare's catalog, as mirrored here. <c>BundleName</c> is the folder it gets on a machine.</summary>
public sealed record SkillShareSkillDto(string Id, string Name, string BundleName, string Description, string[] Tags, string Owner,
    string? LatestVersion, SkillShareSkillVersionDto[] Versions, bool Deprecated);

/// <summary>One of a person's SkillShare MCP servers. <c>On</c>: their sessions get it unless a session says otherwise.</summary>
public sealed record SkillShareMcpDto(string Id, string Title, string Url, bool Connected, string? AccessMode, string? SetupUrl, bool On);

/// <summary>The person's SkillShare MCP servers that their sessions do not get by default.</summary>
public sealed record SkillShareMcpChoiceRequest(string[] Off);

/// <summary>A SkillShare MCP server of a session: its provider id and the exact URL its tokens are for.</summary>
public sealed record SkillShareMcpRef(string Id, string Url);

/// <summary>Server → daemon: a token for one SkillShare MCP server of a session, for the session's creator.</summary>
public sealed record SkillShareTokenResponse(string AccessToken, DateTime ExpiresAt);
