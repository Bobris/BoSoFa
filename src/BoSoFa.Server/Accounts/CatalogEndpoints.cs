using System.Text.Json;
using BoSoFa.Protocol;
using BoSoFa.Server.Btdb;
using BoSoFa.Server.Domain;
using BoSoFa.Server.Machines;
using BoSoFa.Server.Secrets;
using BoSoFa.Server.Tokens;
using BTDB.Replication.EventSourcing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.HttpResults;

namespace BoSoFa.Server.Accounts;

/// <summary>MCP servers, skill bundles and profiles (plan §5.5). Everyone may read the catalog; admins change it; a
/// daemon fetches what its session's profile resolves to, with the references to secrets and variables resolved for the
/// session's creator (docs/secrets.md §4), the only place those values leave the server.</summary>
public static partial class CatalogEndpoints
{
    /// <summary>The purpose of the encrypted values of older definitions, which <see cref="McpValueMigration"/> moves to secrets.</summary>
    internal const string Purpose = "BoSoFa.McpServer.v1";

    public static void MapCatalogEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization();
        api.MapGet("/mcp-servers", ListServersAsync);
        api.MapPost("/mcp-servers", PublishServerAsync);
        api.MapGet("/skills", ListSkillsAsync);
        api.MapPost("/skills", PublishSkillAsync);
        api.MapGet("/profiles", ListProfilesAsync);
        api.MapPost("/profiles", SaveProfileAsync);
        app.MapGet("/api/daemon/v1/sessions/{id}/profile", ResolveAsync).RequireAuthorization(DaemonTokens.Scheme);
    }

    static string[] Strings(string json) => JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.StringArray)!;
    static string Json(string[] values) => JsonSerializer.Serialize(values, ProtocolJsonContext.Default.StringArray);
    static SkillRef[] Skills(string json) => JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.SkillRefArray)!;

    internal static Dictionary<string, string> Values(string? json) => string.IsNullOrEmpty(json)
        ? new(StringComparer.Ordinal)
        : JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.DictionaryStringString)!;

    internal static string Json(Dictionary<string, string> values) =>
        JsonSerializer.Serialize(new SortedDictionary<string, string>(values, StringComparer.Ordinal).ToDictionary(StringComparer.Ordinal),
            ProtocolJsonContext.Default.DictionaryStringString);

    [System.Text.RegularExpressions.GeneratedRegex("^[!#$%&'*+.^_`|~0-9A-Za-z-]+$")]
    private static partial System.Text.RegularExpressions.Regex HeaderName();

    /// <summary>What is wrong with environment variables or headers as written, or null: references must be well formed, and
    /// a value whose name looks like a credential must come from a secret.</summary>
    static string? Problem(IReadOnlyDictionary<string, string> values, bool headers)
    {
        foreach (var (name, value) in values)
        {
            if (headers ? !HeaderName().IsMatch(name) : !SecretNames.IsValid(name))
                return headers ? $"{name} is not a header name." : $"{name} is not a variable name: letters, digits and _, not starting with a digit.";
            if (SecretReferences.Problem(value) is { } problem) return $"{name}: {problem}";
            if (SecretReferences.LooksSecret(name) && !SecretReferences.In(value).Any(reference => reference.Kind == SecretKind.Secret))
                return $"{name} looks like a credential: write it as ${{secret:NAME}} and set NAME under Secrets and variables.";
        }
        return null;
    }

    static Results<NoContent, NotFound, ForbidHttpResult, BadRequest<string>> Result(SessionCommandResult result) => result.Outcome switch
    {
        CommandOutcome.Ok => TypedResults.NoContent(),
        CommandOutcome.NotFound => TypedResults.NotFound(),
        CommandOutcome.Forbidden => TypedResults.Forbid(),
        _ => TypedResults.BadRequest("Names are lowercase letters, digits, - and _.")
    };

    static async Task<McpServerDto[]> ListServersAsync(BtdbNode node, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using var query = publisher.StartQuery();
        return Ambient.Relation<IMcpServerTable>().Select(server => new McpServerDto(server.Name, server.Transport, server.Command,
            Strings(server.Args), server.Url, Values(server.Values), server.ProtectedSecrets.Length > 0 ? Strings(server.SecretNames) : [])).ToArray();
    }

    /// <summary>A definition holds no secret: its credentials are references, resolved for each session.</summary>
    static async Task<Results<NoContent, NotFound, ForbidHttpResult, BadRequest<string>>> PublishServerAsync(PublishMcpServer request,
        BtdbNode node, HttpContext http)
    {
        // A reference in the URL is only resolved later, so its shape is checked with a stand-in.
        var url = request.Url == null ? null : SecretReferences.Replace(request.Url, _ => "x");
        if (request.Transport == McpTransport.Stdio ? string.IsNullOrWhiteSpace(request.Command) : !Uri.TryCreate(url, UriKind.Absolute, out _))
            return TypedResults.BadRequest("A stdio server needs a command, a remote server a URL.");
        var args = request.Args ?? [];
        var values = request.Values ?? [];
        if (Problem(values, headers: request.Transport == McpTransport.Http) is { } problem) return TypedResults.BadRequest(problem);
        foreach (var text in args.Append(request.Url ?? "").Append(request.Command ?? ""))
            if (SecretReferences.Problem(text) is { } wrong) return TypedResults.BadRequest(wrong);
        if (args.Append(request.Command ?? "").SelectMany(SecretReferences.In).Any(reference => reference.Kind == SecretKind.Secret))
            return TypedResults.BadRequest("A secret in a command line is visible to every user of the machine: pass it in an environment variable.");
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        return Result(await publisher.PublishAndWaitAsync(new McpServerPublished
        {
            UserId = http.User.UserId() ?? 0, Name = request.Name, Transport = request.Transport, Command = request.Command,
            Args = Json(args), Url = request.Url, Values = Json(values)
        }, http.RequestAborted));
    }

    static async Task<SkillBundleDto[]> ListSkillsAsync(BtdbNode node, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using var query = publisher.StartQuery();
        return Ambient.Relation<ISkillBundleTable>().Select(skill => new SkillBundleDto(skill.Name, skill.Version, skill.Sha256,
            skill.Description, skill.PublishedAt)).ToArray();
    }

    static async Task<Results<NoContent, NotFound, ForbidHttpResult, BadRequest<string>>> PublishSkillAsync(PublishSkillBundle request,
        BtdbNode node, IBlobStore blobs, HttpContext http)
    {
        if (!BlobHash.IsValid(request.Sha256) || !await blobs.ExistsAsync(request.Sha256, http.RequestAborted))
            return TypedResults.BadRequest("Upload the bundle first; no blob has that hash.");
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        return Result(await publisher.PublishAndWaitAsync(new SkillBundlePublished
        {
            UserId = http.User.UserId() ?? 0, Name = request.Name, Version = request.Version, Sha256 = request.Sha256,
            Description = request.Description ?? ""
        }, http.RequestAborted));
    }

    static async Task<ProfileDto[]> ListProfilesAsync(BtdbNode node, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using var query = publisher.StartQuery();
        return Ambient.Relation<IProfileTable>().Select(profile => new ProfileDto(profile.Name, profile.Model, profile.PermissionMode,
            Strings(profile.McpServers), Skills(profile.Skills), profile.SystemPrompt, profile.SystemPromptMode, profile.MaxToolGroup,
            Values(profile.Environment))).ToArray();
    }

    static async Task<Results<NoContent, NotFound, ForbidHttpResult, BadRequest<string>>> SaveProfileAsync(ProfileDto request,
        BtdbNode node, HttpContext http)
    {
        if (request.PermissionMode != null && !Harnesses.PermissionModes.Contains(request.PermissionMode))
            return TypedResults.BadRequest("Unknown permission mode.");
        if (request.SystemPromptMode != null && !Harnesses.SystemPromptModes.Contains(request.SystemPromptMode))
            return TypedResults.BadRequest("A system prompt either appends or replaces.");
        if (request.MaxToolGroup != null && !AgentToolGroups.All.Contains(request.MaxToolGroup)) return TypedResults.BadRequest("Unknown tool group.");
        var environment = request.Environment ?? [];
        if (Problem(environment, headers: false) is { } problem) return TypedResults.BadRequest(problem);
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        // What the admin sees as host-only is refused now; a person's own host-only secret of that name, at their session's start.
        using (publisher.StartQuery())
        {
            var secrets = Ambient.Relation<ISecretTable>();
            var userId = http.User.UserId() ?? 0;
            if (environment.Values.SelectMany(SecretReferences.In).Where(reference => reference.Kind == SecretKind.Secret)
                    .FirstOrDefault(reference => (secrets.FindByIdOrDefault(userId, reference.Name) ?? secrets.FindByIdOrDefault(0, reference.Name)) is { HostOnly: true })
                is { Name: not null } hostOnly)
                return TypedResults.BadRequest($"{hostOnly.Name} is for MCP servers only: it cannot be in the environment of a session.");
        }
        return Result(await publisher.PublishAndWaitAsync(new ProfileSaved
        {
            UserId = http.User.UserId() ?? 0, Name = request.Name, Model = request.Model, PermissionMode = request.PermissionMode,
            McpServers = Json(request.McpServers),
            Skills = JsonSerializer.Serialize(request.Skills, ProtocolJsonContext.Default.SkillRefArray), SystemPrompt = request.SystemPrompt,
            SystemPromptMode = request.SystemPromptMode, MaxToolGroup = request.MaxToolGroup, Environment = Json(environment)
        }, http.RequestAborted));
    }

    /// <summary>What a profile gives a session of <paramref name="resolver"/>'s person: its MCP servers and its environment, with
    /// references resolved. Must run inside a query scope; call <see cref="SecretResolver.ThrowIfMissing"/> after.</summary>
    internal static (ResolvedMcpServer[] Servers, Dictionary<string, string> Environment) Resolve(Profile profile, SecretResolver resolver,
        IDataProtectionProvider protection)
    {
        var servers = new List<ResolvedMcpServer>();
        foreach (var serverName in Strings(profile.McpServers))
        {
            if (Ambient.Relation<IMcpServerTable>().FindByIdOrDefault(serverName) is not { } server) continue;
            // An older definition whose values have not moved to secrets yet keeps them encrypted in itself.
            Dictionary<string, string> values;
            if (server.ProtectedSecrets.Length > 0)
            {
                values = JsonSerializer.Deserialize(protection.CreateProtector(Purpose, server.SecretId).Unprotect(server.ProtectedSecrets),
                    ProtocolJsonContext.Default.DictionaryStringString)!;
                resolver.Remember(values.Values);
            }
            else values = resolver.Resolve(Values(server.Values));
            servers.Add(new(server.Name, server.Transport, server.Command is { } command ? resolver.Resolve(command) : null,
                Strings(server.Args).Select(argument => resolver.Resolve(argument)).ToArray(), server.Url is { } url ? resolver.Resolve(url) : null, values));
        }
        return (servers.ToArray(), resolver.Resolve(Values(profile.Environment), shell: true));
    }

    /// <summary>
    /// The profile of a session on this machine, resolved for the session's creator; never stored or logged on the way. A
    /// reference that cannot be resolved fails the session's start with a message that names it.
    /// </summary>
    static async Task<Results<Ok<SessionProfileResponse>, NotFound, ForbidHttpResult, Conflict<string>>> ResolveAsync(ulong id, BtdbNode node,
        IDataProtectionProvider protection, ILoggerFactory loggers, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        SessionProfileResponse response;
        Session session;
        string? used;
        using (publisher.StartQuery())
        {
            if (Ambient.Relation<ISessionTable>().FindByIdOrDefault(id) is not { } found) return TypedResults.NotFound();
            session = found;
            if (session.MachineId != http.User.MachineId() || session.Status == SessionStatus.Stopped) return TypedResults.Forbid();
            if (session.ProfileName is not { } name || Ambient.Relation<IProfileTable>().FindByIdOrDefault(name) is not { } profile)
                return TypedResults.NotFound();
            var resolver = new SecretResolver(session.CreatedById, protection);
            var (servers, environment) = Resolve(profile, resolver, protection);
            try { resolver.ThrowIfMissing(); }
            catch (MissingSecretsException missing) { return TypedResults.Conflict(missing.Message); }
            used = resolver.Used;
            var skills = Skills(profile.Skills).Select(skill => Ambient.Relation<ISkillBundleTable>().FindByIdOrDefault(skill.Name, skill.Version))
                .Where(skill => skill != null).Select(skill => new ResolvedSkill(skill!.Name, skill.Version, skill.Sha256)).ToArray();
            response = new SessionProfileResponse(servers, skills, environment, resolver.Secrets);
        }
        if (used != null)
            await AuditEndpoints.RecordAsync(publisher, session.CreatedById, "secret.used", $"session {session.Id}",
                $"on machine {session.MachineId}: {used}", loggers.CreateLogger("BoSoFa.Server.Secrets"));
        return TypedResults.Ok(response);
    }
}
