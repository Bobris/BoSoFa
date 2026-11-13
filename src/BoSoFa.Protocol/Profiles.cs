using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BoSoFa.Protocol;

[JsonConverter(typeof(JsonStringEnumConverter<McpTransport>))]
public enum McpTransport
{
    Stdio,
    Http
}

/// <summary>An MCP server in the catalog as people see it. <c>Values</c> are environment variables of a stdio server, or HTTP
/// headers of a remote one, as written: references to secrets (<c>${secret:NAME}</c>) and variables, or plain values that are
/// not secret. <c>InlineValueNames</c> name values of an older definition, kept encrypted in it until they move to secrets.</summary>
public sealed record McpServerDto(string Name, McpTransport Transport, string? Command, string[] Args, string? Url,
    Dictionary<string, string> Values, string[] InlineValueNames);

/// <summary><c>Values</c> are environment variables of a stdio server, or HTTP headers of a remote one; a credential is written
/// as <c>${secret:NAME}</c>, never in the clear (docs/secrets.md §4.1).</summary>
public sealed record PublishMcpServer(string Name, McpTransport Transport, string? Command, string[]? Args, string? Url,
    Dictionary<string, string>? Values);

/// <summary>A versioned skill: a zip in the blob store with <c>SKILL.md</c> at its root.</summary>
public sealed record SkillBundleDto(string Name, string Version, string Sha256, string Description, DateTime PublishedAt);

public sealed record PublishSkillBundle(string Name, string Version, string Sha256, string? Description);

public sealed record SkillRef(string Name, string Version);

/// <summary>What sessions of a profile get (plan §5.5).</summary>
/// <param name="SystemPrompt">For server agent sessions, with <c>SystemPromptMode</c> saying whether it extends or replaces the built-in one.</param>
/// <param name="MaxToolGroup">The most of a machine's tools a server agent session of this profile may have; null for all.</param>
/// <param name="Environment">Variables of the session's harness and of the commands its agent runs, references allowed.</param>
public sealed record ProfileDto(string Name, string? Model, string? PermissionMode, string[] McpServers, SkillRef[] Skills, string? SystemPrompt = null,
    string? SystemPromptMode = null, string? MaxToolGroup = null, Dictionary<string, string>? Environment = null);

/// <summary>Daemon → server, just in time: everything a session's profile resolves to, secrets included. <c>Environment</c>
/// is for the harness process (and so the agent's shell); <c>Mask</c> lists the values in it that are secrets, which the
/// daemon masks in all it reports from then on (docs/secrets.md §5.2).</summary>
public sealed record SessionProfileResponse(ResolvedMcpServer[] McpServers, ResolvedSkill[] Skills, Dictionary<string, string>? Environment = null,
    string[]? Mask = null);

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
