using System.Text.Json;
using BoSoFa.Protocol;
using BoSoFa.Server.Btdb;
using BoSoFa.Server.Domain;
using BoSoFa.Server.Machines;
using BTDB.Replication.EventSourcing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.HttpResults;

namespace BoSoFa.Server.Accounts;

/// <summary>MCP servers, skill bundles and profiles (plan §5.5). Everyone may read the catalog; admins change it; a
/// daemon fetches what its session's profile resolves to, the only place MCP secrets leave the server.</summary>
public static class CatalogEndpoints
{
    const string Purpose = "BoSoFa.McpServer.v1";

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
            Strings(server.Args), server.Url, Strings(server.SecretNames))).ToArray();
    }

    static async Task<Results<NoContent, NotFound, ForbidHttpResult, BadRequest<string>>> PublishServerAsync(PublishMcpServer request,
        BtdbNode node, IDataProtectionProvider protection, HttpContext http)
    {
        if (request.Transport == McpTransport.Stdio ? string.IsNullOrWhiteSpace(request.Command) : !Uri.TryCreate(request.Url, UriKind.Absolute, out _))
            return TypedResults.BadRequest("A stdio server needs a command, a remote server a URL.");
        var secrets = request.Secrets ?? [];
        var secretId = Guid.NewGuid().ToString("N");
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        return Result(await publisher.PublishAndWaitAsync(new McpServerPublished
        {
            UserId = http.User.UserId() ?? 0, Name = request.Name, Transport = request.Transport, Command = request.Command,
            Args = Json(request.Args ?? []), Url = request.Url, SecretNames = Json(secrets.Keys.Order(StringComparer.Ordinal).ToArray()),
            SecretId = secretId,
            ProtectedSecrets = protection.CreateProtector(Purpose, secretId)
                .Protect(JsonSerializer.Serialize(secrets, ProtocolJsonContext.Default.DictionaryStringString))
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
            Strings(profile.McpServers), Skills(profile.Skills))).ToArray();
    }

    static async Task<Results<NoContent, NotFound, ForbidHttpResult, BadRequest<string>>> SaveProfileAsync(ProfileDto request,
        BtdbNode node, HttpContext http)
    {
        if (request.PermissionMode != null && !Harnesses.PermissionModes.Contains(request.PermissionMode))
            return TypedResults.BadRequest("Unknown permission mode.");
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        return Result(await publisher.PublishAndWaitAsync(new ProfileSaved
        {
            UserId = http.User.UserId() ?? 0, Name = request.Name, Model = request.Model, PermissionMode = request.PermissionMode,
            McpServers = Json(request.McpServers),
            Skills = JsonSerializer.Serialize(request.Skills, ProtocolJsonContext.Default.SkillRefArray)
        }, http.RequestAborted));
    }

    /// <summary>The profile of a session on this machine, secrets decrypted; never stored or logged on the way.</summary>
    static async Task<Results<Ok<SessionProfileResponse>, NotFound, ForbidHttpResult>> ResolveAsync(ulong id, BtdbNode node,
        IDataProtectionProvider protection, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using var query = publisher.StartQuery();
        var session = Ambient.Relation<ISessionTable>().FindByIdOrDefault(id);
        if (session == null) return TypedResults.NotFound();
        if (session.MachineId != http.User.MachineId() || session.Status == SessionStatus.Stopped) return TypedResults.Forbid();
        if (session.ProfileName is not { } name || Ambient.Relation<IProfileTable>().FindByIdOrDefault(name) is not { } profile)
            return TypedResults.NotFound();
        var servers = new List<ResolvedMcpServer>();
        foreach (var serverName in Strings(profile.McpServers))
            if (Ambient.Relation<IMcpServerTable>().FindByIdOrDefault(serverName) is { } server)
                servers.Add(new(server.Name, server.Transport, server.Command, Strings(server.Args), server.Url,
                    JsonSerializer.Deserialize(protection.CreateProtector(Purpose, server.SecretId).Unprotect(server.ProtectedSecrets),
                        ProtocolJsonContext.Default.DictionaryStringString)!));
        var skills = Skills(profile.Skills).Select(skill => Ambient.Relation<ISkillBundleTable>().FindByIdOrDefault(skill.Name, skill.Version))
            .Where(skill => skill != null).Select(skill => new ResolvedSkill(skill!.Name, skill.Version, skill.Sha256)).ToArray();
        return TypedResults.Ok(new SessionProfileResponse(servers.ToArray(), skills));
    }
}
