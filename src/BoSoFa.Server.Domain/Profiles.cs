using System.Text.Json;
using BoSoFa.Protocol;
using BTDB.ODBLayer;
using BTDB.Replication.EventSourcing;

namespace BoSoFa.Server.Domain;

public class McpServerDefinition
{
    [PrimaryKey(1)] public string Name { get; set; } = "";
    public McpTransport Transport { get; set; }
    public string? Command { get; set; }
    public string Args { get; set; } = "[]";
    public string? Url { get; set; }

    /// <summary>Names of the secret values, for display.</summary>
    public string SecretNames { get; set; } = "[]";

    /// <summary>The secret values as Data Protection ciphertext, with <see cref="SecretId"/> as purpose (see HarnessAccount).</summary>
    public string SecretId { get; set; } = "";
    public string ProtectedSecrets { get; set; } = "";
}

public interface IMcpServerTable : IRelation<McpServerDefinition>
{
    McpServerDefinition? FindByIdOrDefault(string name);
}

public class SkillBundle
{
    [PrimaryKey(1)] public string Name { get; set; } = "";
    [PrimaryKey(2)] public string Version { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime PublishedAt { get; set; }
}

public interface ISkillBundleTable : IRelation<SkillBundle>
{
    SkillBundle? FindByIdOrDefault(string name, string version);
}

public class Profile
{
    [PrimaryKey(1)] public string Name { get; set; } = "";
    public string? Model { get; set; }
    public string? PermissionMode { get; set; }
    public string McpServers { get; set; } = "[]";
    public string Skills { get; set; } = "[]";
}

public interface IProfileTable : IRelation<Profile>
{
    Profile? FindByIdOrDefault(string name);
}

public class McpServerPublished : EventWithResult<SessionCommandResult>
{
    public ulong UserId { get; set; }
    public string Name { get; set; } = "";
    public McpTransport Transport { get; set; }
    public string? Command { get; set; }
    public string Args { get; set; } = "[]";
    public string? Url { get; set; }
    public string SecretNames { get; set; } = "[]";
    public string SecretId { get; set; } = "";
    public string ProtectedSecrets { get; set; } = "";
}

public class SkillBundlePublished : EventWithResult<SessionCommandResult>
{
    public ulong UserId { get; set; }
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Description { get; set; } = "";
}

public class ProfileSaved : EventWithResult<SessionCommandResult>
{
    public ulong UserId { get; set; }
    public string Name { get; set; } = "";
    public string? Model { get; set; }
    public string? PermissionMode { get; set; }
    public string McpServers { get; set; } = "[]";
    public string Skills { get; set; } = "[]";
}

static class Catalog
{
    public static bool IsAdmin(ulong userId) => Ambient.Relation<IUserTable>().FindByIdOrDefault(userId)?.Role == UserRole.Admin;
}

public sealed class McpServerPublishedHandler : IEventHandler<McpServerPublished, SessionCommandResult>
{
    public SessionCommandResult? Validate(McpServerPublished ev) =>
        CatalogNames.IsValid(ev.Name) ? null : new() { Outcome = CommandOutcome.Invalid };

    public SessionCommandResult Consume(McpServerPublished ev)
    {
        if (!Catalog.IsAdmin(ev.UserId)) return new() { Outcome = CommandOutcome.Forbidden };
        Ambient.Relation<IMcpServerTable>().Upsert(new()
        {
            Name = ev.Name, Transport = ev.Transport, Command = ev.Command, Args = ev.Args, Url = ev.Url,
            SecretNames = ev.SecretNames, SecretId = ev.SecretId, ProtectedSecrets = ev.ProtectedSecrets
        });
        return new() { Outcome = CommandOutcome.Ok };
    }
}

public sealed class SkillBundlePublishedHandler : IEventHandler<SkillBundlePublished, SessionCommandResult>
{
    public SessionCommandResult? Validate(SkillBundlePublished ev) =>
        CatalogNames.IsValid(ev.Name) && !string.IsNullOrWhiteSpace(ev.Version) ? null : new() { Outcome = CommandOutcome.Invalid };

    public SessionCommandResult Consume(SkillBundlePublished ev)
    {
        if (!Catalog.IsAdmin(ev.UserId)) return new() { Outcome = CommandOutcome.Forbidden };
        Ambient.Relation<ISkillBundleTable>().Upsert(new()
        {
            Name = ev.Name, Version = ev.Version, Sha256 = ev.Sha256, Description = ev.Description, PublishedAt = ev.When
        });
        return new() { Outcome = CommandOutcome.Ok };
    }
}

public sealed class ProfileSavedHandler : IEventHandler<ProfileSaved, SessionCommandResult>
{
    public SessionCommandResult? Validate(ProfileSaved ev) =>
        CatalogNames.IsValid(ev.Name) ? null : new() { Outcome = CommandOutcome.Invalid };

    public SessionCommandResult Consume(ProfileSaved ev)
    {
        if (!Catalog.IsAdmin(ev.UserId)) return new() { Outcome = CommandOutcome.Forbidden };
        // Everything a profile names must exist, so a session never fails on a dangling reference at start.
        var servers = Ambient.Relation<IMcpServerTable>();
        var skills = Ambient.Relation<ISkillBundleTable>();
        if (JsonSerializer.Deserialize(ev.McpServers, ProtocolJsonContext.Default.StringArray)!.Any(name => servers.FindByIdOrDefault(name) == null) ||
            JsonSerializer.Deserialize(ev.Skills, ProtocolJsonContext.Default.SkillRefArray)!.Any(skill => skills.FindByIdOrDefault(skill.Name, skill.Version) == null))
            return new() { Outcome = CommandOutcome.NotFound };
        Ambient.Relation<IProfileTable>().Upsert(new()
        {
            Name = ev.Name, Model = ev.Model, PermissionMode = ev.PermissionMode, McpServers = ev.McpServers, Skills = ev.Skills
        });
        return new() { Outcome = CommandOutcome.Ok };
    }
}
