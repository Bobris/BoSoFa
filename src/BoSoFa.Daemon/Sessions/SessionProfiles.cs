using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using BoSoFa.Protocol;

namespace BoSoFa.Daemon.Sessions;

/// <summary>Where the daemon gets what a session's profile resolves to; the server's answer holds MCP secrets, so it is
/// fetched per launch and kept in memory only.</summary>
public interface IProfileSource : IBlobDownloader
{
    Task<SessionProfileResponse> GetProfileAsync(ulong sessionId, CancellationToken cancellation);
}

/// <summary>A profile made ready for a harness: its MCP servers, and a folder holding <c>skills/&lt;name&gt;/</c>.</summary>
public sealed record PreparedProfile(ResolvedMcpServer[] McpServers, string? PluginDirectory);

/// <summary>
/// Materializes profiles on the machine (plan §5.5). Skill bundles are verified against their hash and cached by it.
/// A session gets its own copy at its start, shaped as a Claude Code plugin, so a later change to the profile cannot
/// alter a running conversation. User-owned config is never edited.
/// </summary>
public sealed class SessionProfiles(string dataDirectory, IProfileSource? source)
{
    public async Task<PreparedProfile?> PrepareAsync(SessionState state)
    {
        if (!state.HasProfile) return null;
        if (source == null) throw new InvalidOperationException("This daemon cannot load profiles.");
        var profile = await source.GetProfileAsync(state.SessionId, CancellationToken.None);
        if (profile.Skills.Length == 0) return new(profile.McpServers, null);
        var folder = Path.Combine(dataDirectory, "session-skills", state.SessionId.ToString());
        if (!Directory.Exists(folder))
        {
            var staging = folder + ".staging-" + Guid.NewGuid().ToString("N");
            try
            {
                foreach (var skill in profile.Skills)
                    CopyDirectory(await CachedAsync(skill), Path.Combine(staging, "skills", skill.Name));
                Directory.CreateDirectory(Path.Combine(staging, ".claude-plugin"));
                await File.WriteAllTextAsync(Path.Combine(staging, ".claude-plugin", "plugin.json"), new JsonObject
                {
                    ["name"] = "bosofa-skills", ["version"] = "1.0.0", ["description"] = "Skills of this BoSoFa session's profile."
                }.ToJsonString());
                Directory.Move(staging, folder);
            }
            finally
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
            }
        }
        return new(profile.McpServers, folder);
    }

    async Task<string> CachedAsync(ResolvedSkill skill)
    {
        var cached = Path.Combine(dataDirectory, "skills-cache", skill.Sha256);
        if (Directory.Exists(cached)) return cached;
        var temporary = cached + ".tmp-" + Guid.NewGuid().ToString("N");
        var zip = temporary + ".zip";
        Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
        try
        {
            await source!.DownloadBlobAsync(skill.Sha256, zip, CancellationToken.None);
            string actual;
            await using (var file = File.OpenRead(zip)) actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file));
            if (actual != skill.Sha256) throw new InvalidOperationException($"Skill {skill.Name}: the download's hash is {actual}.");
            ZipFile.ExtractToDirectory(zip, temporary); // refuses entries that leave the folder
            if (!File.Exists(Path.Combine(temporary, "SKILL.md"))) throw new InvalidOperationException($"Skill {skill.Name} has no SKILL.md at its root.");
            try { Directory.Move(temporary, cached); }
            catch (IOException) when (Directory.Exists(cached)) { /* another session cached it first */ }
            return cached;
        }
        finally
        {
            File.Delete(zip);
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
        }
    }

    public static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }
}

/// <summary>MCP servers in each harness's native configuration shape.</summary>
public static class McpConfig
{
    static JsonArray Strings(IEnumerable<string> values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

    static JsonObject Map(IReadOnlyDictionary<string, string> values)
    {
        var map = new JsonObject();
        foreach (var (name, value) in values) map[name] = value;
        return map;
    }

    /// <summary>The JSON of Claude Code's <c>--mcp-config</c>.</summary>
    public static string ForClaude(IEnumerable<ResolvedMcpServer> servers)
    {
        var map = new JsonObject();
        foreach (var server in servers)
            map[server.Name] = server.Transport == McpTransport.Stdio
                ? new JsonObject { ["type"] = "stdio", ["command"] = server.Command, ["args"] = Strings(server.Args), ["env"] = Map(server.Secrets) }
                : new JsonObject { ["type"] = "http", ["url"] = server.Url, ["headers"] = Map(server.Secrets) };
        return new JsonObject { ["mcpServers"] = map }.ToJsonString();
    }

    /// <summary>Codex's <c>config</c> override, shaped like config.toml.</summary>
    public static JsonObject ForCodex(IEnumerable<ResolvedMcpServer> servers)
    {
        var map = new JsonObject();
        foreach (var server in servers)
            map[server.Name] = server.Transport == McpTransport.Stdio
                ? new JsonObject { ["command"] = server.Command, ["args"] = Strings(server.Args), ["env"] = Map(server.Secrets) }
                : new JsonObject { ["url"] = server.Url, ["http_headers"] = Map(server.Secrets) };
        return new JsonObject { ["mcp_servers"] = map };
    }

    /// <summary>ACP's <c>mcpServers</c> of <c>session/new</c>.</summary>
    public static JsonArray ForAcp(IEnumerable<ResolvedMcpServer> servers)
    {
        static JsonArray Pairs(IReadOnlyDictionary<string, string> values) => new(values
            .Select(pair => (JsonNode?)new JsonObject { ["name"] = pair.Key, ["value"] = pair.Value }).ToArray());
        return new(servers.Select(server => (JsonNode?)(server.Transport == McpTransport.Stdio
            ? new JsonObject { ["name"] = server.Name, ["command"] = server.Command, ["args"] = Strings(server.Args), ["env"] = Pairs(server.Secrets) }
            : new JsonObject { ["type"] = "http", ["name"] = server.Name, ["url"] = server.Url, ["headers"] = Pairs(server.Secrets) })).ToArray());
    }
}
