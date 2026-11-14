using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BoSoFa.Protocol;
using BoSoFa.Server.Btdb;
using BoSoFa.Server.Domain;
using BTDB.Replication.EventSourcing;
using Microsoft.AspNetCore.DataProtection;

namespace BoSoFa.Server.SkillShare;

/// <summary>Why SkillShare gave nothing; the UI explains each one.</summary>
public enum SkillShareProblem
{
    /// <summary>This server does not sign in through SkillShare.</summary>
    NotEnabled,

    /// <summary>The person never signed in through SkillShare here.</summary>
    NoGrant,

    /// <summary>SkillShare refused the person's grant: revoked, expired, or the person was disabled. Signing in again helps.</summary>
    GrantRefused,

    /// <summary>The server is not a connected app there, or its secret is wrong.</summary>
    ClientRefused,

    /// <summary>SkillShare could not be reached or failed.</summary>
    Unavailable,

    /// <summary>SkillShare has no such skill, version or MCP server.</summary>
    NotFound
}

public sealed class SkillShareException(SkillShareProblem problem, string message) : Exception(message)
{
    public SkillShareProblem Problem { get; } = problem;
}

/// <summary>What SkillShare's discovery document says (docs/skillshare.md §2.2): <c>skillshare_connect_api</c> marks it.</summary>
public sealed record SkillShareInfo(string Issuer, Uri TokenEndpoint, Uri ConnectApi, string[] McpResources, string Name, string? McpSetupUrl);

public sealed record SkillShareRemoteMcp(string Id, string Title, string Url, bool Connected, string? AccessMode, string? SetupUrl);

public sealed record SkillShareRemoteSkills(List<SyncedSkill> Skills, string? Cursor);

/// <summary>
/// The server's side of SkillShare, the OIDC provider it signs people in through: its discovery, tokens and connect API
/// (<c>/api/connect/v1</c>). App tokens (<c>client_credentials</c>) read the catalog; a person's token comes from the refresh token
/// their sign-in granted, for one resource at a time (RFC 8707). Tokens live in this node's memory only, never in the database or a
/// log; the refresh token does not rotate, so any node may use it at any time.
/// </summary>
public sealed class SkillShareClient(ServerOptions options, BtdbNode node, IDataProtectionProvider protection, IBlobStore blobs, TimeProvider time,
    ILogger<SkillShareClient> logger)
{
    public const string GrantPurpose = "BoSoFa.SkillShareGrant.v1";

    /// <summary>The scopes a sign-in asks for besides OpenID's own.</summary>
    public static readonly string[] Scopes = ["offline_access", "skills.read", "mcps.read", "mcp.use"];

    static readonly TimeSpan Margin = TimeSpan.FromMinutes(5), McpListAge = TimeSpan.FromMinutes(5), DiscoveryAge = TimeSpan.FromHours(1),
        DiscoveryRetry = TimeSpan.FromMinutes(1);

    const long MaximumArchiveBytes = 64 * 1024 * 1024;

    readonly HttpClient _http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
    {
        Timeout = TimeSpan.FromSeconds(60)
    };

    readonly ConcurrentDictionary<string, (string Token, DateTimeOffset ExpiresAt)> _tokens = new();
    readonly ConcurrentDictionary<ulong, (SkillShareRemoteMcp[] Servers, DateTimeOffset At)> _mcps = new();
    readonly ConcurrentDictionary<string, SemaphoreSlim> _imports = new();
    readonly SemaphoreSlim _discovering = new(1, 1);
    (SkillShareInfo? Info, DateTimeOffset At)? _discovery;

    /// <summary>Whether a sign-in through the configured provider can be SkillShare's, before discovery was read.</summary>
    public bool MayBeEnabled => options.Oidc is { Authority.Length: > 0 };

    /// <summary>The discovery document when the sign-in provider is SkillShare, else null.</summary>
    public async Task<SkillShareInfo?> InfoAsync(CancellationToken cancellation)
    {
        if (!MayBeEnabled) return null;
        var now = time.GetUtcNow();
        if (_discovery is { } known && now - known.At < (known.Info == null ? DiscoveryRetry : DiscoveryAge)) return known.Info;
        await _discovering.WaitAsync(cancellation);
        try
        {
            if (_discovery is { } again && now - again.At < (again.Info == null ? DiscoveryRetry : DiscoveryAge)) return again.Info;
            SkillShareInfo? info = null;
            try { info = await ReadDiscoveryAsync(cancellation); }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException or UriFormatException)
            {
                logger.LogWarning("The sign-in provider's discovery document could not be read: {Message}", exception.Message);
                // A provider that answered before stays known through a short outage.
                if (_discovery?.Info is { } previous) info = previous;
            }
            _discovery = (info, now);
            return info;
        }
        finally { _discovering.Release(); }
    }

    async Task<SkillShareInfo?> ReadDiscoveryAsync(CancellationToken cancellation)
    {
        var authority = options.Oidc!.Authority.TrimEnd('/');
        using var response = await _http.GetAsync(authority + "/.well-known/openid-configuration", cancellation);
        response.EnsureSuccessStatusCode();
        var document = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation)) as JsonObject;
        if (Text(document?["skillshare_connect_api"]) is not { Length: > 0 } api || Text(document!["token_endpoint"]) is not { Length: > 0 } token)
            return null;
        var resources = (document["skillshare_mcp_resources"] as JsonArray ?? []).Select(Text).OfType<string>().ToArray();
        return new(Text(document["issuer"]) ?? authority, new Uri(token), new Uri(api.TrimEnd('/') + "/"), resources,
            Text(document["skillshare_name"]) ?? "SkillShare", Text(document["skillshare_mcp_setup_url"]) ?? authority + "/mcps");
    }

    async Task<SkillShareInfo> RequireAsync(CancellationToken cancellation) =>
        await InfoAsync(cancellation) ?? throw new SkillShareException(SkillShareProblem.NotEnabled, "This server does not sign in through SkillShare.");

    /// <summary>An app token for the catalog, which needs no person.</summary>
    public async Task<string> AppTokenAsync(CancellationToken cancellation)
    {
        var info = await RequireAsync(cancellation);
        if (_tokens.TryGetValue("app", out var cached) && cached.ExpiresAt - Margin > time.GetUtcNow()) return cached.Token;
        var (token, expiresAt) = await RequestTokenAsync(info, new()
        {
            ["grant_type"] = "client_credentials", ["scope"] = "skills.read"
        }, null, cancellation);
        _tokens["app"] = (token, expiresAt);
        return token;
    }

    /// <summary>
    /// A token of the person for one resource: an MCP server's exact URL (scope <c>mcp.use</c>), or null for the connect API (scopes
    /// <c>mcps.read skills.read</c>). <paramref name="fresh"/> skips the cache, after a resource refused the cached one.
    /// </summary>
    public async Task<(string Token, DateTimeOffset ExpiresAt)> UserTokenAsync(ulong userId, string? resource, bool fresh, CancellationToken cancellation)
    {
        var info = await RequireAsync(cancellation);
        var publisher = await node.Ready.WaitAsync(cancellation);
        SkillShareGrant grant;
        using (publisher.StartQuery())
        {
            if (Ambient.Relation<ISkillShareGrantTable>().FindByIdOrDefault(userId) is not { } found)
                throw new SkillShareException(SkillShareProblem.NoGrant, "Sign in through SkillShare to use its MCP servers here.");
            grant = found;
        }
        if (grant.Status != SkillShareGrantStatus.Active)
            throw new SkillShareException(SkillShareProblem.GrantRefused, "SkillShare no longer accepts your sign-in here: sign out and sign in again.");
        var key = $"{userId}|{grant.GrantId}|{resource}";
        if (!fresh && _tokens.TryGetValue(key, out var cached) && cached.ExpiresAt - Margin > time.GetUtcNow()) return cached;
        var refreshToken = protection.CreateProtector(GrantPurpose, grant.GrantId).Unprotect(grant.ProtectedRefreshToken);
        var form = new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken };
        if (resource != null)
        {
            form["resource"] = resource;
            form["scope"] = "mcp.use";
        }
        else form["scope"] = "mcps.read skills.read";
        try
        {
            var issued = await RequestTokenAsync(info, form, grant, cancellation);
            _tokens[key] = issued;
            return issued;
        }
        catch (SkillShareException refused) when (refused.Problem == SkillShareProblem.GrantRefused)
        {
            await publisher.PublishAndWaitAsync(new SkillShareGrantExpired { UserId = userId, GrantId = grant.GrantId, Reason = refused.Message }, cancellation);
            throw;
        }
    }

    async Task<(string Token, DateTimeOffset ExpiresAt)> RequestTokenAsync(SkillShareInfo info, Dictionary<string, string> form, SkillShareGrant? grant,
        CancellationToken cancellation)
    {
        var oidc = options.Oidc!;
        using var request = new HttpRequestMessage(HttpMethod.Post, info.TokenEndpoint) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{Uri.EscapeDataString(oidc.ClientId)}:{Uri.EscapeDataString(oidc.ClientSecret ?? "")}")));
        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, cancellation); }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellation.IsCancellationRequested)
        {
            throw new SkillShareException(SkillShareProblem.Unavailable, $"SkillShare could not be reached: {exception.Message}");
        }
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellation);
            JsonObject? answer = null;
            try { answer = JsonNode.Parse(body) as JsonObject; }
            catch (JsonException) { }
            if (response.IsSuccessStatusCode && Text(answer?["access_token"]) is { Length: > 0 } token)
            {
                var seconds = answer!["expires_in"] is JsonValue value && value.TryGetValue<double>(out var number) ? number : 3600;
                return (token, time.GetUtcNow().AddSeconds(seconds));
            }
            var error = Text(answer?["error"]) ?? "";
            var reason = Text(answer?["error_description"]) ?? Text(answer?["reason"]) ?? error;
            throw error switch
            {
                "invalid_grant" when grant != null => new SkillShareException(SkillShareProblem.GrantRefused,
                    $"SkillShare refused your sign-in here ({reason}): sign out and sign in again."),
                "invalid_client" or "unauthorized_client" => new SkillShareException(SkillShareProblem.ClientRefused,
                    $"SkillShare does not accept this server as a connected app ({reason})."),
                "invalid_target" or "invalid_scope" => new SkillShareException(SkillShareProblem.NotFound, $"SkillShare refused the request: {reason}."),
                _ => new SkillShareException(SkillShareProblem.Unavailable, $"SkillShare's token endpoint answered {(int)response.StatusCode}{(reason.Length > 0 ? ": " + reason : "")}.")
            };
        }
    }

    /// <summary>A person's SkillShare MCP servers, kept five minutes.</summary>
    public async Task<SkillShareRemoteMcp[]> ListMcpsAsync(ulong userId, CancellationToken cancellation, bool fresh = false)
    {
        if (!fresh && _mcps.TryGetValue(userId, out var cached) && time.GetUtcNow() - cached.At < McpListAge) return cached.Servers;
        var info = await RequireAsync(cancellation);
        var (token, _) = await UserTokenAsync(userId, null, false, cancellation);
        using var response = await SendAsync(HttpMethod.Get, new Uri(info.ConnectApi, "me/mcps"), token, cancellation);
        var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation));
        var list = root as JsonArray ?? root?["servers"] as JsonArray ?? root?["mcps"] as JsonArray ?? root?["providers"] as JsonArray ?? [];
        var servers = list.OfType<JsonObject>()
            .Select(server => (Id: Text(server["id"]), Url: Text(server["url"]), Server: server))
            .Where(entry => entry.Id is { Length: > 0 } && entry.Url is { Length: > 0 } && CatalogNames.IsValid(entry.Id!))
            .Select(entry => new SkillShareRemoteMcp(entry.Id!, Text(entry.Server["title"]) ?? entry.Id!, entry.Url!,
                Bool(entry.Server["connected"]) ?? true, Text(entry.Server["accessMode"]), Text(entry.Server["setupUrl"])))
            .ToArray();
        _mcps[userId] = (servers, time.GetUtcNow());
        return servers;
    }

    /// <summary>The servers a person's session gets when it names none: every one they connected, except those they turned off.</summary>
    public async Task<SkillShareMcpRef[]> DefaultMcpsAsync(ulong userId, CancellationToken cancellation)
    {
        var servers = await ListMcpsAsync(userId, cancellation);
        var off = await OffAsync(userId, cancellation);
        return servers.Where(server => server.Connected && !off.Contains(server.Id)).Select(server => new SkillShareMcpRef(server.Id, server.Url)).ToArray();
    }

    public async Task<HashSet<string>> OffAsync(ulong userId, CancellationToken cancellation)
    {
        var publisher = await node.Ready.WaitAsync(cancellation);
        using var query = publisher.StartQuery();
        return Ambient.Relation<ISkillShareMcpChoiceTable>().FindByIdOrDefault(userId) is { } choice
            ? SkillShareCatalog.Strings(choice.Off).ToHashSet(StringComparer.Ordinal)
            : [];
    }

    /// <summary>What changed in the catalog since <paramref name="cursor"/> (all of it when empty), and the cursor to ask from next.</summary>
    public async Task<SkillShareRemoteSkills> ReadCatalogAsync(string cursor, CancellationToken cancellation)
    {
        var info = await RequireAsync(cancellation);
        var token = await AppTokenAsync(cancellation);
        var asked = time.GetUtcNow();
        var url = new Uri(info.ConnectApi, cursor.Length == 0 ? "skills" : $"skills?changedSince={Uri.EscapeDataString(cursor)}");
        using var response = await SendAsync(HttpMethod.Get, url, token, cancellation);
        var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation));
        var list = root as JsonArray ?? root?["skills"] as JsonArray ?? [];
        var skills = new List<SyncedSkill>();
        foreach (var skill in list.OfType<JsonObject>())
        {
            if (Text(skill["id"]) is not { Length: > 0 } id) continue;
            var name = Text(skill["name"]) ?? id;
            var versions = (skill["versions"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(version => (Version: Text(version["version"]), Sha256: Text(version["contentSha256"])?.ToLowerInvariant() ?? "", Node: version))
                // SkillShare hashes a version's archive the first time someone asks for it, so a hash may be missing yet; a version
                // it knows has no SKILL.md is left out.
                .Where(version => version.Version is { Length: > 0 } && (version.Sha256.Length == 0 || BlobHash.IsValid(version.Sha256)) &&
                    Bool(version.Node["usable"]) != false)
                .Select(version => new SkillShareSkillVersionDto(version.Version!, Time(version.Node["publishedAt"]) ?? DateTime.UnixEpoch,
                    Bool(version.Node["latest"]) ?? false, version.Sha256))
                .ToArray();
            var tags = (skill["tags"] as JsonArray ?? []).Select(tag => Text(tag) ?? Text(tag?["slug"]) ?? Text(tag?["name"])).OfType<string>().ToArray();
            skills.Add(new()
            {
                Id = id, Name = name, BundleName = SkillShareCatalog.BundleNameOf(name, id), Description = Text(skill["description"]) ?? "",
                Tags = JsonSerializer.Serialize(tags, ProtocolJsonContext.Default.StringArray),
                Owner = Text(skill["owner"]) ?? Text(skill["owner"]?["name"]) ?? Text(skill["ownerName"]) ?? "",
                Deprecated = Bool(skill["deprecated"]) ?? false, Deleted = Bool(skill["deleted"]) ?? false,
                Versions = JsonSerializer.Serialize(versions, ProtocolJsonContext.Default.SkillShareSkillVersionDtoArray)
            });
        }
        // SkillShare's own clock when it says it; else this one's, a little early, so nothing published meanwhile is missed.
        var now = Text(root?["serverTime"]) ?? Text(root?["now"]) ?? asked.AddMinutes(-2).UtcDateTime.ToString("O");
        return new(skills, now);
    }

    /// <summary>
    /// Makes sure the blob store holds a SkillShare version's archive, fetching it once per version, and returns its SHA-256: the
    /// catalog's, so it is its blob's name too. A version the catalog has no hash for yet is fetched, and its hash is whatever the
    /// archive's is. A version with no <c>SKILL.md</c> at its root is refused.
    /// </summary>
    public async Task<string> EnsureArchiveAsync(string skillId, SkillShareSkillVersionDto version, CancellationToken cancellation)
    {
        var known = version.Sha256.Length > 0;
        if (known && await blobs.ExistsAsync(version.Sha256, cancellation)) return version.Sha256;
        var gate = _imports.GetOrAdd(known ? version.Sha256 : $"{skillId}@{version.Version}", _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellation);
        try
        {
            if (known && await blobs.ExistsAsync(version.Sha256, cancellation)) return version.Sha256;
            var info = await RequireAsync(cancellation);
            var token = await AppTokenAsync(cancellation);
            var url = new Uri(info.ConnectApi, $"skills/{Uri.EscapeDataString(skillId)}/versions/{Uri.EscapeDataString(version.Version)}/archive");
            using var response = await SendAsync(HttpMethod.Get, url, token, cancellation);
            await using var content = await response.Content.ReadAsStreamAsync(cancellation);
            var (file, sha256, _) = await BlobHash.SpoolAsync(content, MaximumArchiveBytes, cancellation);
            try
            {
                if (known && sha256 != version.Sha256)
                    throw new SkillShareException(SkillShareProblem.Unavailable, $"SkillShare's archive of {skillId} {version.Version} is not the one its catalog names.");
                using (var zip = System.IO.Compression.ZipFile.OpenRead(file))
                    if (zip.GetEntry("SKILL.md") == null)
                        throw new SkillShareException(SkillShareProblem.NotFound, $"{skillId} {version.Version} has no SKILL.md at its root.");
                await blobs.PutAsync(sha256, file, cancellation);
                return sha256;
            }
            finally { File.Delete(file); }
        }
        catch (InvalidDataException problem) { throw new SkillShareException(SkillShareProblem.Unavailable, $"SkillShare's archive of {skillId}: {problem.Message}"); }
        finally { gate.Release(); }
    }

    /// <summary>Counts a session's use of a version for its person in SkillShare's statistics; a failure only costs the count.</summary>
    public async Task RecordDownloadAsync(ulong userId, string skillId, string version)
    {
        try
        {
            var info = await RequireAsync(CancellationToken.None);
            var (token, _) = await UserTokenAsync(userId, null, false, CancellationToken.None);
            using var response = await SendAsync(HttpMethod.Post,
                new Uri(info.ConnectApi, $"skills/{Uri.EscapeDataString(skillId)}/versions/{Uri.EscapeDataString(version)}/downloads"), token, CancellationToken.None);
        }
        catch (Exception exception) when (exception is SkillShareException or HttpRequestException or TaskCanceledException)
        {
            logger.LogInformation("A download of {Skill} {Version} was not counted in SkillShare: {Message}", skillId, version, exception.Message);
        }
    }

    async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri url, string token, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (method == HttpMethod.Post) request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation); }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellation.IsCancellationRequested)
        {
            throw new SkillShareException(SkillShareProblem.Unavailable, $"SkillShare could not be reached: {exception.Message}");
        }
        if (response.IsSuccessStatusCode) return response;
        using (response)
            throw response.StatusCode switch
            {
                HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity => new SkillShareException(SkillShareProblem.NotFound,
                    $"SkillShare has no {url.AbsolutePath}."),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new SkillShareException(SkillShareProblem.ClientRefused,
                    $"SkillShare refused {url.AbsolutePath} ({(int)response.StatusCode})."),
                _ => new SkillShareException(SkillShareProblem.Unavailable, $"SkillShare answered {(int)response.StatusCode} to {url.AbsolutePath}.")
            };
    }

    static string? Text(JsonNode? node) => node is JsonValue value
        ? value.TryGetValue<string>(out var text) ? text
        : value.TryGetValue<long>(out var number) ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : null
        : null;

    static bool? Bool(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    static DateTime? Time(JsonNode? node) =>
        Text(node) is { } text && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) ? parsed.UtcDateTime : null;
}
