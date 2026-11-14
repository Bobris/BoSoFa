using System.Collections.Concurrent;
using System.Text.Json;
using BoSoFa.Protocol;
using BoSoFa.Server.Btdb;
using BoSoFa.Server.Domain;
using BoSoFa.Server.Machines;
using BoSoFa.Server.Tokens;
using BTDB.Replication.EventSourcing;
using Microsoft.AspNetCore.Http.HttpResults;

namespace BoSoFa.Server.SkillShare;

/// <summary>
/// SkillShare in the API (docs/skillshare.md §6): its status, the mirrored skill catalog, a person's own MCP servers and which of them
/// their sessions get, and the tokens a daemon's gateway asks for, for the session's creator only.
/// </summary>
public static class SkillShareEndpoints
{
    /// <summary>A session's token requests are audited once per server and node, not at every refresh.</summary>
    static readonly ConcurrentDictionary<(ulong SessionId, string Provider), bool> Audited = new();

    static readonly TimeSpan ImportLimit = TimeSpan.FromSeconds(30);

    public static void MapSkillShareEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1/skillshare").RequireAuthorization();
        api.MapGet("", StatusAsync);
        api.MapPost("/sync", SyncAsync);
        api.MapGet("/skills", ListSkillsAsync);
        api.MapGet("/mcps", ListMcpsAsync);
        api.MapPut("/mcp-choice", SaveChoiceAsync);
        app.MapPost("/api/daemon/v1/sessions/{id}/skillshare-tokens/{provider}", TokenAsync).RequireAuthorization(DaemonTokens.Scheme);
    }

    static async Task<SkillShareStatusDto> StatusAsync(SkillShareClient skillShare, BtdbNode node, HttpContext http)
    {
        var info = await skillShare.InfoAsync(http.RequestAborted);
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using var query = publisher.StartQuery();
        var userId = http.User.UserId() ?? 0;
        var grant = Ambient.Relation<ISkillShareGrantTable>().FindByIdOrDefault(userId)?.Status ?? SkillShareGrantStatus.None;
        if (info == null) return new(false, null, null, grant);
        var skills = Ambient.Relation<ISkillShareSkillTable>().Count(skill => !skill.Deleted);
        if (Ambient.Relation<IUserTable>().FindByIdOrDefault(userId)?.Role != UserRole.Admin)
            return new(true, info.Name, info.Issuer, grant, info.McpSetupUrl, skills);
        var sync = Ambient.Relation<ISkillShareSyncTable>().FindByIdOrDefault(SkillShareCatalog.SyncRowId);
        return new(true, info.Name, info.Issuer, grant, info.McpSetupUrl, skills, sync?.LastSyncAt is { } at && at != default ? at : null,
            sync?.LastError is { Length: > 0 } error ? error : null,
            Ambient.Relation<ISkillShareGrantTable>().Count(row => row.Status == SkillShareGrantStatus.Active));
    }

    /// <summary>Queues a catalog sync when none ever ran, so the first person signing in through SkillShare finds its skills soon.</summary>
    public static async Task SyncOnceAsync(BtdbNode node, BTDB.BackendActions.BackendActions actions, CancellationToken cancellation)
    {
        var publisher = await node.Ready.WaitAsync(cancellation);
        using var query = publisher.StartQuery();
        if (Ambient.Relation<ISkillShareSyncTable>().FindByIdOrDefault(SkillShareCatalog.SyncRowId) == null)
            await actions.EnqueueAsync(new SkillShareCatalogSync());
    }

    static async Task<Results<NoContent, ForbidHttpResult, NotFound>> SyncAsync(SkillShareClient skillShare, BtdbNode node,
        BTDB.BackendActions.BackendActions actions, HttpContext http)
    {
        if (await skillShare.InfoAsync(http.RequestAborted) == null) return TypedResults.NotFound();
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using var query = publisher.StartQuery();
        if (Ambient.Relation<IUserTable>().FindByIdOrDefault(http.User.UserId() ?? 0)?.Role != UserRole.Admin) return TypedResults.Forbid();
        await actions.EnqueueAsync(new SkillShareCatalogSync());
        return TypedResults.NoContent();
    }

    static async Task<SkillShareSkillDto[]> ListSkillsAsync(BtdbNode node, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using var query = publisher.StartQuery();
        return Ambient.Relation<ISkillShareSkillTable>().Where(skill => !skill.Deleted).Select(ToDto).OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    static SkillShareSkillDto ToDto(SkillShareSkill skill)
    {
        var versions = SkillShareCatalog.Versions(skill.Versions);
        return new(skill.Id, skill.Name, skill.BundleName, skill.Description, SkillShareCatalog.Strings(skill.Tags), skill.Owner,
            SkillShareCatalog.VersionOf(skill, SkillSources.Latest)?.Version, versions, skill.Deprecated);
    }

    static async Task<Results<Ok<SkillShareMcpDto[]>, Conflict<string>>> ListMcpsAsync(SkillShareClient skillShare, bool? fresh, HttpContext http)
    {
        var userId = http.User.UserId() ?? 0;
        try
        {
            var servers = await skillShare.ListMcpsAsync(userId, http.RequestAborted, fresh ?? false);
            var off = await skillShare.OffAsync(userId, http.RequestAborted);
            return TypedResults.Ok(servers.Select(server => new SkillShareMcpDto(server.Id, server.Title, server.Url, server.Connected, server.AccessMode,
                server.SetupUrl, server.Connected && !off.Contains(server.Id))).ToArray());
        }
        catch (SkillShareException problem) { return TypedResults.Conflict(problem.Message); }
    }

    static async Task<Results<NoContent, BadRequest<string>>> SaveChoiceAsync(SkillShareMcpChoiceRequest request, BtdbNode node, HttpContext http)
    {
        if (request.Off.Any(id => !CatalogNames.IsValid(id))) return TypedResults.BadRequest("Not a SkillShare MCP server.");
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        await publisher.PublishAndWaitAsync(new SkillShareMcpChoiceSaved
        {
            UserId = http.User.UserId() ?? 0,
            Off = JsonSerializer.Serialize(request.Off.Distinct().Order(StringComparer.Ordinal).ToArray(), ProtocolJsonContext.Default.StringArray)
        }, http.RequestAborted);
        return TypedResults.NoContent();
    }

    /// <summary>A token of the session's creator for one of the session's SkillShare MCP servers, for the session's own machine while it
    /// runs (docs/skillshare.md §5.3). <paramref name="fresh"/>: the server refused the token the daemon had.</summary>
    static async Task<Results<Ok<SkillShareTokenResponse>, NotFound, ForbidHttpResult, Conflict<string>>> TokenAsync(ulong id, string provider, bool? fresh,
        SkillShareClient skillShare, BtdbNode node, ILoggerFactory loggers, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        Session session;
        SkillShareMcpRef server;
        using (publisher.StartQuery())
        {
            if (Ambient.Relation<ISessionTable>().FindByIdOrDefault(id) is not { } found) return TypedResults.NotFound();
            session = found;
            if (session.MachineId != http.User.MachineId() || session.Status == SessionStatus.Stopped) return TypedResults.Forbid();
            if (McpOf(session).FirstOrDefault(candidate => candidate.Id == provider) is not { } named) return TypedResults.NotFound();
            server = named;
        }
        try
        {
            var token = await UserTokenAsync(skillShare, publisher, session, server, fresh ?? false, loggers.CreateLogger("BoSoFa.Server.SkillShare"),
                http.RequestAborted);
            return TypedResults.Ok(token);
        }
        catch (SkillShareException problem) { return TypedResults.Conflict(problem.Message); }
    }

    /// <summary>The session creator's token for one of the session's servers, audited once per session and server on this node.</summary>
    internal static async Task<SkillShareTokenResponse> UserTokenAsync(SkillShareClient skillShare, EventPublisher publisher, Session session,
        SkillShareMcpRef server, bool fresh, ILogger logger, CancellationToken cancellation)
    {
        var (token, expiresAt) = await skillShare.UserTokenAsync(session.CreatedById, server.Url, fresh, cancellation);
        if (Audited.TryAdd((session.Id, server.Id), true))
            await AuditEndpoints.RecordAsync(publisher, session.CreatedById, "skillshare.token", server.Id, $"session {session.Id}", logger);
        return new(token, expiresAt.UtcDateTime);
    }

    /// <summary>The session's SkillShare MCP servers.</summary>
    public static SkillShareMcpRef[] McpOf(Session session) => session.SkillShareMcp.Length == 0
        ? []
        : JsonSerializer.Deserialize(session.SkillShareMcp, ProtocolJsonContext.Default.SkillShareMcpRefArray) ?? [];

    /// <summary>The session's SkillShare MCP servers as a harness gets them: through the daemon's gateway, with a token asked for just in
    /// time. One whose name a catalog server of the session has already is left out.</summary>
    public static IEnumerable<ResolvedMcpServer> ServersOf(Session session, IReadOnlyCollection<ResolvedMcpServer> others) =>
        McpOf(session).Where(server => others.All(other => other.Name != server.Id))
            .Select(server => new ResolvedMcpServer(server.Id, McpTransport.Http, null, [], server.Url, [], server.Id));

    /// <summary>
    /// What a new session gets from SkillShare (docs/skillshare.md §4.3, §5.1): its own skills and the profile's SkillShare skills with
    /// "latest" resolved and their archives in the blob store, and the creator's MCP servers: those asked for, or by default every one they
    /// connected and left on, plus those the profile names. A problem is a message for the person; SkillShare being away only drops
    /// defaults.
    /// </summary>
    public static async Task<(string Skills, string Mcp, string? Problem)> ForSessionAsync(SkillShareClient skillShare, BtdbNode node, ulong userId,
        string? profileName, SkillRef[]? skills, string[]? mcps, ILogger logger, CancellationToken cancellation)
    {
        var publisher = await node.Ready.WaitAsync(cancellation);
        var wanted = new List<SkillRef>();
        var profileMcps = new List<string>();
        var resolved = new List<(SkillRef Ref, string SkillId, SkillShareSkillVersionDto? Version)>();
        using (publisher.StartQuery())
        {
            var profile = profileName == null ? null : Ambient.Relation<IProfileTable>().FindByIdOrDefault(profileName);
            if (profile != null)
            {
                wanted.AddRange(JsonSerializer.Deserialize(profile.Skills, ProtocolJsonContext.Default.SkillRefArray)!.Where(skill => skill.Source != null));
                profileMcps.AddRange(JsonSerializer.Deserialize(profile.McpServers, ProtocolJsonContext.Default.StringArray)!
                    .Where(SkillShareCatalog.IsSkillShareMcp).Select(name => name[SkillSources.McpPrefix.Length..]));
            }
            wanted.AddRange(skills ?? []);
            var bundles = Ambient.Relation<ISkillBundleTable>();
            var mirrored = Ambient.Relation<ISkillShareSkillTable>();
            foreach (var skill in wanted)
                switch (skill.Source)
                {
                    case null when bundles.FindByIdOrDefault(skill.Name, skill.Version) != null:
                        resolved.Add((skill, "", null));
                        break;
                    case null:
                        return ("", "", $"There is no skill {skill.Name} {skill.Version}.");
                    case SkillSources.SkillShare when mirrored.FindByIdOrDefault(skill.Name) is { Deleted: false } found:
                        if (SkillShareCatalog.VersionOf(found, skill.Version) is not { } version)
                            return ("", "", $"SkillShare's {found.Name} has no version {skill.Version}.");
                        resolved.Add((new SkillRef(found.Id, version.Version, SkillSources.SkillShare), found.Id, version));
                        break;
                    case SkillSources.SkillShare:
                        return ("", "", $"SkillShare has no skill {skill.Name} any more.");
                    default:
                        return ("", "", $"Skills do not come from {skill.Source}.");
                }
        }
        // Each version's archive once in the blob store, so the daemon fetches it from here like any bundle.
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
        {
            limit.CancelAfter(ImportLimit);
            foreach (var (_, skillId, version) in resolved.Where(entry => entry.Version != null).DistinctBy(entry => (entry.SkillId, entry.Version!.Version)))
                try
                {
                    var sha256 = await skillShare.EnsureArchiveAsync(skillId, version!, limit.Token);
                    // SkillShare had not hashed it yet: the mirror learns the hash now, so the launch finds the blob.
                    if (version!.Sha256.Length == 0) await RecordHashAsync(publisher, skillId, version.Version, sha256, limit.Token);
                }
                catch (SkillShareException problem) { return ("", "", problem.Message); }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    return ("", "", $"SkillShare did not hand over {skillId} {version!.Version} within {ImportLimit.TotalSeconds:0} seconds.");
                }
        }
        foreach (var (reference, skillId, _) in resolved.Where(entry => entry.Version != null))
            _ = skillShare.RecordDownloadAsync(userId, skillId, reference.Version);
        var skillsJson = resolved.Count == 0 ? "" : JsonSerializer.Serialize(resolved.Select(entry => entry.Ref).ToArray(), ProtocolJsonContext.Default.SkillRefArray);

        var servers = new List<SkillShareMcpRef>();
        if (mcps is { Length: > 0 } || profileMcps.Count > 0 || mcps == null)
        {
            if (await skillShare.InfoAsync(cancellation) == null)
            {
                if (mcps is { Length: > 0 }) return ("", "", "This server does not sign in through SkillShare.");
            }
            else
            {
                SkillShareRemoteMcp[] known;
                try { known = await skillShare.ListMcpsAsync(userId, cancellation); }
                catch (SkillShareException problem)
                {
                    // Asked for by name, a server that cannot be had stops the start; by default, the session starts without them.
                    if (mcps is { Length: > 0 }) return ("", "", problem.Message);
                    if (problem.Problem is not (SkillShareProblem.NoGrant or SkillShareProblem.NotEnabled))
                        logger.LogWarning("A session of user {User} starts without SkillShare's MCP servers: {Message}", userId, problem.Message);
                    known = [];
                }
                var off = mcps == null ? await skillShare.OffAsync(userId, cancellation) : [];
                var names = mcps ?? known.Where(server => server.Connected && !off.Contains(server.Id)).Select(server => server.Id).ToArray();
                foreach (var name in names.Concat(profileMcps).Distinct(StringComparer.Ordinal))
                {
                    if (known.FirstOrDefault(server => server.Id == name) is { } server) servers.Add(new(server.Id, server.Url));
                    else if (mcps?.Contains(name) == true) return ("", "", $"You have no SkillShare MCP server {name}.");
                }
            }
        }
        var mcpJson = servers.Count == 0 ? "" : JsonSerializer.Serialize(servers.ToArray(), ProtocolJsonContext.Default.SkillShareMcpRefArray);
        return (skillsJson, mcpJson, null);
    }

    static async Task RecordHashAsync(EventPublisher publisher, string skillId, string version, string sha256, CancellationToken cancellation)
    {
        SyncedSkill updated;
        using (publisher.StartQuery())
        {
            if (Ambient.Relation<ISkillShareSkillTable>().FindByIdOrDefault(skillId) is not { } skill) return;
            var versions = SkillShareCatalog.Versions(skill.Versions)
                .Select(known => known.Version == version && known.Sha256.Length == 0 ? known with { Sha256 = sha256 } : known).ToArray();
            updated = new()
            {
                Id = skill.Id, Name = skill.Name, BundleName = skill.BundleName, Description = skill.Description, Tags = skill.Tags, Owner = skill.Owner,
                Deprecated = skill.Deprecated, Deleted = skill.Deleted,
                Versions = JsonSerializer.Serialize(versions, ProtocolJsonContext.Default.SkillShareSkillVersionDtoArray)
            };
        }
        await publisher.PublishAndWaitAsync(new SkillShareCatalogSynced { Skills = [updated] }, cancellation);
    }

    /// <summary>The skills a launch gets: the profile's own bundles, then the session's (SkillShare's among them), one per folder name.
    /// Must run inside a query scope.</summary>
    public static ResolvedSkill[] SkillsOf(Session session, Profile? profile)
    {
        var bundles = Ambient.Relation<ISkillBundleTable>();
        var mirrored = Ambient.Relation<ISkillShareSkillTable>();
        var refs = (profile == null ? [] : JsonSerializer.Deserialize(profile.Skills, ProtocolJsonContext.Default.SkillRefArray)!.Where(skill => skill.Source == null))
            .Concat(session.Skills.Length == 0 ? [] : JsonSerializer.Deserialize(session.Skills, ProtocolJsonContext.Default.SkillRefArray)!);
        var skills = new List<ResolvedSkill>();
        foreach (var skill in refs)
        {
            ResolvedSkill? resolved = skill.Source switch
            {
                null => bundles.FindByIdOrDefault(skill.Name, skill.Version) is { } bundle ? new(bundle.Name, bundle.Version, bundle.Sha256) : null,
                SkillSources.SkillShare => mirrored.FindByIdOrDefault(skill.Name) is { } found && SkillShareCatalog.VersionOf(found, skill.Version) is { Sha256.Length: > 0 } version
                    ? new(found.BundleName, version.Version, version.Sha256)
                    : null,
                _ => null
            };
            if (resolved != null && skills.All(other => other.Name != resolved.Name)) skills.Add(resolved);
        }
        return skills.ToArray();
    }
}
