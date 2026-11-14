using System.Text.Json;
using BoSoFa.Protocol;
using BTDB;
using BTDB.BackendActions;
using BTDB.ODBLayer;
using BTDB.Replication.EventSourcing;

namespace BoSoFa.Server.Domain;

/// <summary>
/// What a person's sign-in through SkillShare granted this server (docs/skillshare.md §2.4): a refresh token, protected with Data
/// Protection under <see cref="GrantId"/>, that does not rotate. One per person; each sign-in replaces it.
/// </summary>
public class SkillShareGrant
{
    [PrimaryKey(1)] public ulong UserId { get; set; }
    public string GrantId { get; set; } = "";
    public string ProtectedRefreshToken { get; set; } = "";
    public string Scopes { get; set; } = "";
    public DateTime GrantedAt { get; set; }
    public SkillShareGrantStatus Status { get; set; }
}

public interface ISkillShareGrantTable : IRelation<SkillShareGrant>
{
    SkillShareGrant? FindByIdOrDefault(ulong userId);
}

/// <summary>A skill of SkillShare's catalog, mirrored by <see cref="SkillShareCatalogSync"/>. A version's archive is a blob of the
/// blob store under its SHA-256 once something needed it.</summary>
public class SkillShareSkill
{
    [PrimaryKey(1)] public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>The skill's folder on a machine: its name, made a catalog name.</summary>
    public string BundleName { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>JSON <c>string[]</c>.</summary>
    public string Tags { get; set; } = "[]";
    public string Owner { get; set; } = "";
    public bool Deprecated { get; set; }

    /// <summary>Gone from SkillShare; kept so that what names it can say so.</summary>
    public bool Deleted { get; set; }

    /// <summary>JSON <see cref="SkillShareSkillVersionDto"/>[].</summary>
    public string Versions { get; set; } = "[]";
    public DateTime ChangedAt { get; set; }
}

public interface ISkillShareSkillTable : IRelation<SkillShareSkill>
{
    SkillShareSkill? FindByIdOrDefault(string id);
}

/// <summary>Where the catalog's mirror stands: one row.</summary>
public class SkillShareSync
{
    [PrimaryKey(1)] public ulong Id { get; set; }

    /// <summary>SkillShare's time of the last read that succeeded, for <c>changedSince</c>; empty before the first.</summary>
    public string Cursor { get; set; } = "";
    public DateTime LastSyncAt { get; set; }
    public string LastError { get; set; } = "";
}

public interface ISkillShareSyncTable : IRelation<SkillShareSync>
{
    SkillShareSync? FindByIdOrDefault(ulong id);
}

/// <summary>The SkillShare MCP servers a person turned off: every other one they connected is on in their sessions by default.</summary>
public class SkillShareMcpChoice
{
    [PrimaryKey(1)] public ulong UserId { get; set; }

    /// <summary>JSON <c>string[]</c> of provider ids.</summary>
    public string Off { get; set; } = "[]";
}

public interface ISkillShareMcpChoiceTable : IRelation<SkillShareMcpChoice>
{
    SkillShareMcpChoice? FindByIdOrDefault(ulong userId);
}

/// <summary>SkillShare refused a person's grant; signing in again makes a new one.</summary>
public class SkillShareGrantExpired : EventWithResult<SessionCommandResult>
{
    public ulong UserId { get; set; }
    public string GrantId { get; set; } = "";
    public string Reason { get; set; } = "";
}

[Generate]
public class SyncedSkill
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string BundleName { get; set; } = "";
    public string Description { get; set; } = "";
    public string Tags { get; set; } = "[]";
    public string Owner { get; set; } = "";
    public bool Deprecated { get; set; }
    public bool Deleted { get; set; }
    public string Versions { get; set; } = "[]";
}

/// <summary>What changed in SkillShare's catalog since <see cref="SkillShareSync.Cursor"/>, or why it could not be read. Only
/// changes are published, so an unchanged catalog writes nothing to the log.</summary>
public class SkillShareCatalogSynced : EventWithResult<SessionCommandResult>
{
    public List<SyncedSkill> Skills { get; set; } = [];

    /// <summary>The cursor once these are applied; empty keeps the one there.</summary>
    public string Cursor { get; set; } = "";
    public string Error { get; set; } = "";
}

public class SkillShareMcpChoiceSaved : EventWithResult<SessionCommandResult>
{
    public ulong UserId { get; set; }
    public string Off { get; set; } = "[]";
}

/// <summary>Reads SkillShare's catalog every 10 minutes, on the node the leader picks; skipped while the server does not sign in
/// through SkillShare. Enqueued by hand for "Sync now".</summary>
[BackendAction(Name = "skillshare-catalog-sync", Cpu = 10, MaxConcurrent = 1)]
[RepeatedlyScheduled(10)]
public class SkillShareCatalogSync : ActionInput<SkillShareSyncResult>, IActionInputUnificator
{
    /// <summary>A sync queued while another waits merges into it.</summary>
    public byte[]? GetUniqueKey() => [];
}

[Generate]
public class SkillShareSyncResult
{
    public int Changed { get; set; }
}

public static class SkillShareCatalog
{
    public const ulong SyncRowId = 1;

    public static SkillShareSkillVersionDto[] Versions(string json) =>
        JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.SkillShareSkillVersionDtoArray) ?? [];

    public static string[] Strings(string json) => JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.StringArray) ?? [];

    /// <summary>The version a reference means now: its own, or the latest; null when there is none such.</summary>
    public static SkillShareSkillVersionDto? VersionOf(SkillShareSkill skill, string version)
    {
        var versions = Versions(skill.Versions);
        return version == SkillSources.Latest
            ? versions.FirstOrDefault(candidate => candidate.Latest) ?? versions.OrderByDescending(candidate => candidate.PublishedAt).FirstOrDefault()
            : versions.FirstOrDefault(candidate => candidate.Version == version);
    }

    /// <summary>A skill's name as a folder name on machines (<see cref="CatalogNames"/>).</summary>
    public static string BundleNameOf(string name, string id)
    {
        var chars = name.Trim().ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
        var result = new string(chars).Trim('-', '_');
        while (result.Contains("--")) result = result.Replace("--", "-");
        if (result.Length > 64) result = result[..64].TrimEnd('-', '_');
        if (!CatalogNames.IsValid(result)) result = CatalogNames.IsValid("skill-" + id.ToLowerInvariant()) ? "skill-" + id.ToLowerInvariant() : "skill";
        return result;
    }

    /// <summary>The daemon fetches what the session gets from its profile, own skills and SkillShare servers at each launch.</summary>
    public static bool HasLaunchProfile(Session session) => session.ProfileName != null || session.Skills.Length > 2 || session.SkillShareMcp.Length > 2;

    public static bool IsSkillShareMcp(string name) => name.StartsWith(SkillSources.McpPrefix, StringComparison.Ordinal);
}

public sealed class SkillShareGrantExpiredHandler : IEventHandler<SkillShareGrantExpired, SessionCommandResult>
{
    public SessionCommandResult? Validate(SkillShareGrantExpired ev) => null;

    public SessionCommandResult Consume(SkillShareGrantExpired ev)
    {
        var grants = Ambient.Relation<ISkillShareGrantTable>();
        if (grants.FindByIdOrDefault(ev.UserId) is not { Status: SkillShareGrantStatus.Active } grant || grant.GrantId != ev.GrantId)
            return new() { Outcome = CommandOutcome.NotFound };
        grant.Status = SkillShareGrantStatus.Expired;
        grants.Upsert(grant);
        Audit.Record(ev.When, ev.UserId, "skillshare.grant.expired", $"user {ev.UserId}", ev.Reason);
        return new() { Outcome = CommandOutcome.Ok };
    }
}

public sealed class SkillShareCatalogSyncedHandler : IEventHandler<SkillShareCatalogSynced, SessionCommandResult>
{
    public SessionCommandResult? Validate(SkillShareCatalogSynced ev) =>
        ev.Skills.All(skill => skill.Id.Length > 0 && CatalogNames.IsValid(skill.BundleName)) ? null : new() { Outcome = CommandOutcome.Invalid };

    public SessionCommandResult Consume(SkillShareCatalogSynced ev)
    {
        var skills = Ambient.Relation<ISkillShareSkillTable>();
        foreach (var skill in ev.Skills)
            skills.Upsert(new()
            {
                Id = skill.Id, Name = skill.Name, BundleName = skill.BundleName, Description = skill.Description, Tags = skill.Tags,
                Owner = skill.Owner, Deprecated = skill.Deprecated, Deleted = skill.Deleted, Versions = skill.Versions, ChangedAt = ev.When
            });
        var syncs = Ambient.Relation<ISkillShareSyncTable>();
        var sync = syncs.FindByIdOrDefault(SkillShareCatalog.SyncRowId) ?? new() { Id = SkillShareCatalog.SyncRowId };
        if (ev.Cursor.Length > 0) sync.Cursor = ev.Cursor;
        if (ev.Error.Length == 0) sync.LastSyncAt = ev.When;
        sync.LastError = ev.Error;
        syncs.Upsert(sync);
        return new() { Outcome = CommandOutcome.Ok };
    }
}

public sealed class SkillShareMcpChoiceSavedHandler : IEventHandler<SkillShareMcpChoiceSaved, SessionCommandResult>
{
    public SessionCommandResult? Validate(SkillShareMcpChoiceSaved ev) => ev.UserId == 0 ? new() { Outcome = CommandOutcome.Invalid } : null;

    public SessionCommandResult Consume(SkillShareMcpChoiceSaved ev)
    {
        Ambient.Relation<ISkillShareMcpChoiceTable>().Upsert(new() { UserId = ev.UserId, Off = ev.Off });
        return new() { Outcome = CommandOutcome.Ok };
    }
}
