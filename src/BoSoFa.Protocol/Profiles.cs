using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BoSoFa.Protocol;

[JsonConverter(typeof(JsonStringEnumConverter<McpTransport>))]
public enum McpTransport
{
    Stdio,
    Http
}

/// <summary>An MCP server in the catalog as people see it: names of its secret values, never the values.</summary>
public sealed record McpServerDto(string Name, McpTransport Transport, string? Command, string[] Args, string? Url, string[] SecretNames);

/// <summary><c>Secrets</c> are environment variables of a stdio server, or HTTP headers of a remote one.</summary>
public sealed record PublishMcpServer(string Name, McpTransport Transport, string? Command, string[]? Args, string? Url,
    Dictionary<string, string>? Secrets);

/// <summary>A versioned skill: a zip in the blob store with <c>SKILL.md</c> at its root.</summary>
public sealed record SkillBundleDto(string Name, string Version, string Sha256, string Description, DateTime PublishedAt);

public sealed record PublishSkillBundle(string Name, string Version, string Sha256, string? Description);

public sealed record SkillRef(string Name, string Version);

/// <summary>What sessions of a profile get (plan §5.5).</summary>
public sealed record ProfileDto(string Name, string? Model, string? PermissionMode, string[] McpServers, SkillRef[] Skills);

/// <summary>Daemon → server, just in time: everything a session's profile resolves to, secrets included.</summary>
public sealed record SessionProfileResponse(ResolvedMcpServer[] McpServers, ResolvedSkill[] Skills);

public sealed record ResolvedMcpServer(string Name, McpTransport Transport, string? Command, string[] Args, string? Url,
    Dictionary<string, string> Secrets);

public sealed record ResolvedSkill(string Name, string Version, string Sha256);

public static partial class CatalogNames
{
    /// <summary>Names become folder and tool names on machines, so they stay simple.</summary>
    public static bool IsValid(string name) => Simple().IsMatch(name);

    [GeneratedRegex(@"^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex Simple();
}
