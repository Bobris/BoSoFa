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

    /// <summary>Environment variables or headers as JSON, as written: references to secrets and variables, or values that are
    /// not secret (docs/secrets.md §4.1). Null in rows from before them.</summary>
    public string? Values { get; set; }

    /// <summary>Names of the values of an older definition, kept in <see cref="ProtectedSecrets"/> until they move to secrets.</summary>
    public string SecretNames { get; set; } = "[]";

    /// <summary>The values of an older definition as Data Protection ciphertext, with <see cref="SecretId"/> as purpose (see
    /// HarnessAccount); empty once they moved to shared secrets (<see cref="McpValuesMoved"/>).</summary>
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
    public string? SystemPrompt { get; set; }
    public string? SystemPromptMode { get; set; }
    public string? MaxToolGroup { get; set; }

    /// <summary>Variables of the session's harness and its agent's commands as JSON, references allowed; null in older rows.</summary>
    public string? Environment { get; set; }
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

    /// <summary>The values as written (JSON); empty in events from before them, which carry encrypted ones instead.</summary>
    public string Values { get; set; } = "";
    public string SecretNames { get; set; } = "[]";
    public string SecretId { get; set; } = "";
    public string ProtectedSecrets { get; set; } = "";
}

/// <summary>One value of an older MCP server definition, become a shared secret; its ciphertext is made for version 1.</summary>
[BTDB.Generate]
public class MovedSecret
{
    public string Name { get; set; } = "";
    public string SecretId { get; set; } = "";
    public string ProtectedValue { get; set; } = "";
    public string Hint { get; set; } = "";
}

/// <summary>
/// The server moved the encrypted values of an older MCP server definition to shared secrets, which the definition now refers
/// to. Published by the server itself, never on a person's request. A shared secret of the same name that exists already is
/// kept, and referred to.
/// </summary>
public class McpValuesMoved : EventWithResult<SessionCommandResult>
{
    public string Name { get; set; } = "";
    public string Values { get; set; } = "";
    public List<MovedSecret> Secrets { get; set; } = [];
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
    public string? SystemPrompt { get; set; }
    public string? SystemPromptMode { get; set; }
    public string? MaxToolGroup { get; set; }
    public string Environment { get; set; } = "";
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
            Values = ev.Values.Length > 0 ? ev.Values : null, SecretNames = ev.SecretNames, SecretId = ev.SecretId,
            ProtectedSecrets = ev.ProtectedSecrets
        });
        Audit.Record(ev.When, ev.UserId, "mcp.published", ev.Name);
        return new() { Outcome = CommandOutcome.Ok };
    }
}

public sealed class McpValuesMovedHandler(IDomainSignals signals) : IEventHandler<McpValuesMoved, SessionCommandResult>
{
    public SessionCommandResult? Validate(McpValuesMoved ev) =>
        ev.Values.Length > 0 && ev.Secrets.All(secret => SecretNames.IsValid(secret.Name) && secret.ProtectedValue.Length > 0)
            ? null
            : new() { Outcome = CommandOutcome.Invalid };

    public SessionCommandResult Consume(McpValuesMoved ev)
    {
        var servers = Ambient.Relation<IMcpServerTable>();
        if (servers.FindByIdOrDefault(ev.Name) is not { ProtectedSecrets.Length: > 0 } server) return new() { Outcome = CommandOutcome.NotFound };
        var secrets = Ambient.Relation<ISecretTable>();
        foreach (var moved in ev.Secrets)
        {
            if (secrets.FindByIdOrDefault(0, moved.Name) != null) continue;
            secrets.Upsert(new()
            {
                OwnerId = 0, Name = moved.Name, Kind = SecretKind.Secret, SecretId = moved.SecretId, Version = 1,
                ProtectedValue = moved.ProtectedValue, Hint = moved.Hint, SetAt = ev.When
            });
            Audit.Record(ev.When, 0, "secret.set", moved.Name, $"shared secret, version 1, moved from the MCP server {ev.Name}");
        }
        server.Values = ev.Values;
        server.SecretNames = "[]";
        server.SecretId = "";
        server.ProtectedSecrets = "";
        servers.Upsert(server);
        signals.SecretsChanged();
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
        Audit.Record(ev.When, ev.UserId, "skill.published", ev.Name, ev.Version);
        return new() { Outcome = CommandOutcome.Ok };
    }
}

public sealed class ProfileSavedHandler : IEventHandler<ProfileSaved, SessionCommandResult>
{
    public SessionCommandResult? Validate(ProfileSaved ev) =>
        CatalogNames.IsValid(ev.Name) && (ev.SystemPrompt == null || ev.SystemPrompt.Length <= Sessions.MaximumPromptLength) &&
        (ev.SystemPromptMode == null || Harnesses.SystemPromptModes.Contains(ev.SystemPromptMode)) && (ev.MaxToolGroup == null || AgentToolGroups.All.Contains(ev.MaxToolGroup))
            ? null
            : new() { Outcome = CommandOutcome.Invalid };

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
            Name = ev.Name, Model = ev.Model, PermissionMode = ev.PermissionMode, McpServers = ev.McpServers, Skills = ev.Skills,
            SystemPrompt = string.IsNullOrWhiteSpace(ev.SystemPrompt) ? null : ev.SystemPrompt, SystemPromptMode = ev.SystemPromptMode, MaxToolGroup = ev.MaxToolGroup,
            Environment = ev.Environment.Length > 0 ? ev.Environment : null
        });
        Audit.Record(ev.When, ev.UserId, "profile.saved", ev.Name);
        return new() { Outcome = CommandOutcome.Ok };
    }
}
