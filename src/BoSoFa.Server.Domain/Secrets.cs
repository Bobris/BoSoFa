using System.Text.Json;
using BoSoFa.Protocol;
using BTDB.ODBLayer;
using BTDB.Replication.EventSourcing;

namespace BoSoFa.Server.Domain;

/// <summary>
/// A secret or a variable (docs/secrets.md), one name per owner: a personal one of <see cref="OwnerId"/>, a shared one with
/// <see cref="OwnerId"/> 0, or one of an environment, whose <see cref="SecretEnvironment.Id"/> is the owner (ids of people and
/// environments come from the same allocator, so they never meet). A secret's value is only Data Protection ciphertext, made by the server before publishing with
/// <see cref="SecretId"/> and <see cref="Version"/> as purposes, so it opens for that secret and that version alone; a
/// variable's value is in <see cref="Value"/>, in the clear.
/// </summary>
public class SecretEntry
{
    [PrimaryKey(1)] public ulong OwnerId { get; set; }
    [PrimaryKey(2)] public string Name { get; set; } = "";
    public SecretKind Kind { get; set; }

    /// <summary>Stays the same across versions; a name deleted and set again gets a new one.</summary>
    public string SecretId { get; set; } = "";
    public int Version { get; set; }
    public string ProtectedValue { get; set; } = "";
    public string Value { get; set; } = "";
    public string Hint { get; set; } = "";
    public ulong SetById { get; set; }
    public DateTime SetAt { get; set; }

    /// <summary>Goes to MCP servers only, never to an agent's shell or a harness's environment (docs/secrets.md §6).</summary>
    public bool HostOnly { get; set; }

    /// <summary>The machines whose sessions get it, as a JSON array of ids; empty for any (docs/secrets.md §10.1).</summary>
    public string Machines { get; set; } = "[]";

    /// <summary>Due for rotation this many days after <see cref="SetAt"/>; 0 for never.</summary>
    public int RotateEveryDays { get; set; }

    /// <summary>Why this version should be rotated now (it was on a machine that was removed); null when nothing says so.</summary>
    public string? RotationReason { get; set; }
}

public interface ISecretTable : IRelation<SecretEntry>
{
    SecretEntry? FindByIdOrDefault(ulong ownerId, string name);
    IEnumerable<SecretEntry> ListById(ulong ownerId);
    bool RemoveById(ulong ownerId, string name);
}

/// <summary>A secret's value that a rotation or a deletion replaced, kept a while so that what a running session still prints
/// can be masked (docs/secrets.md §5.3). Never handed to a session.</summary>
public class SecretVersion
{
    [PrimaryKey(1)] public string SecretId { get; set; } = "";
    [PrimaryKey(2)] public int Version { get; set; }
    public string ProtectedValue { get; set; } = "";
    public DateTime RetiredAt { get; set; }
}

public interface ISecretVersionTable : IRelation<SecretVersion>
{
    IEnumerable<SecretVersion> ListById(string secretId);
    bool RemoveById(string secretId, int version);
}

/// <summary>The last time a value went to a machine (<see cref="MachineId"/> 0: it stayed on the server), for showing where it
/// is used and for knowing which values a removed machine held.</summary>
public class SecretUse
{
    [PrimaryKey(1)] public ulong OwnerId { get; set; }
    [PrimaryKey(2)] public string Name { get; set; } = "";
    [PrimaryKey(3)] public ulong MachineId { get; set; }
    public int Version { get; set; }
    public ulong SessionId { get; set; }
    public DateTime At { get; set; }
}

public interface ISecretUseTable : IRelation<SecretUse>
{
    IEnumerable<SecretUse> ListById(ulong ownerId, string name);
    bool RemoveById(ulong ownerId, string name, ulong machineId);
}

[BTDB.Generate]
public class SecretCommandResult
{
    public CommandOutcome Outcome { get; set; }

    /// <summary>Another change of the same name came first; read it again and retry.</summary>
    public bool Conflict { get; set; }
}

/// <summary>Sets a value: a new name with <see cref="Version"/> 1, or the next version of the name, which must still be
/// <see cref="Version"/> − 1 with the same <see cref="SecretId"/> (the ciphertext was made for exactly that).</summary>
public class SecretSet : EventWithResult<SecretCommandResult>
{
    public ulong UserId { get; set; }
    public ulong OwnerId { get; set; }
    public string Name { get; set; } = "";
    public SecretKind Kind { get; set; }
    public string SecretId { get; set; } = "";
    public int Version { get; set; }
    public string ProtectedValue { get; set; } = "";
    public string Value { get; set; } = "";
    public string Hint { get; set; } = "";
    public bool HostOnly { get; set; }

    /// <summary>JSON array of machine ids; empty in events from before it, which means any.</summary>
    public string Machines { get; set; } = "";
    public int RotateEveryDays { get; set; }
}

/// <summary>Changes how a value is used, keeping its version and value.</summary>
public class SecretSettingsChanged : EventWithResult<SecretCommandResult>
{
    public ulong UserId { get; set; }
    public ulong OwnerId { get; set; }
    public string Name { get; set; } = "";
    public bool HostOnly { get; set; }
    public string Machines { get; set; } = "[]";
    public int RotateEveryDays { get; set; }
}

public class SecretUseRecord
{
    public ulong OwnerId { get; set; }
    public string Name { get; set; } = "";
    public int Version { get; set; }
}

/// <summary>A session's start, or a server agent's run, was given these values (docs/secrets.md §4.2); never their values.</summary>
public class SecretsUsed : PureEvent
{
    public ulong SessionId { get; set; }
    public ulong MachineId { get; set; }
    public ulong PersonId { get; set; }
    public List<SecretUseRecord> Uses { get; set; } = [];

    /// <summary>The names, versions and scopes, as the audit shows them.</summary>
    public string Detail { get; set; } = "";
}

public class SecretDeleted : EventWithResult<SecretCommandResult>
{
    public ulong UserId { get; set; }
    public ulong OwnerId { get; set; }
    public string Name { get; set; } = "";
}

public static class Secrets
{
    /// <summary>How long, and how many, replaced values of one secret are kept for masking.</summary>
    public static readonly TimeSpan RetainedFor = TimeSpan.FromDays(30);
    public const int RetainedVersions = 16;

    public const int MaxRotateEveryDays = 3650;

    /// <summary>A person sets their own values; only an admin sets shared ones and those of environments.</summary>
    public static bool MayChange(ulong userId, ulong ownerId) =>
        ownerId == 0 || SecretEnvironments.Find(ownerId) != null ? Catalog.IsAdmin(userId) : ownerId == userId && userId != 0;

    public static string ScopeOf(ulong ownerId) =>
        ownerId == 0 ? "shared" : SecretEnvironments.Find(ownerId) is { } environment ? $"environment {environment.Name}" : "personal";

    public static ulong[] Ids(string? json) =>
        string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.UInt64Array) ?? [];

    public static string Json(IEnumerable<ulong> ids) =>
        JsonSerializer.Serialize(ids.Distinct().Order().ToArray(), ProtocolJsonContext.Default.UInt64Array);

    /// <summary>Whether a list of machines (empty: any) lets a session on <paramref name="machineId"/> have it. A session on no
    /// machine (a server agent with remote MCP servers only) keeps every value on the server, so it may.</summary>
    public static bool AllowedOn(string? machines, ulong machineId) => machineId == 0 || Ids(machines) is not { Length: > 0 } ids || ids.Contains(machineId);

    /// <summary>Forgets where a value went: it was deleted.</summary>
    public static void ForgetUses(ulong ownerId, string name)
    {
        var uses = Ambient.Relation<ISecretUseTable>();
        foreach (var use in uses.ListById(ownerId, name).ToList()) uses.RemoveById(use.OwnerId, use.Name, use.MachineId);
    }

    /// <summary>The values a removed machine had in their current version are flagged for rotation, and where it had them forgotten.</summary>
    public static void MachineRemoved(ulong machineId, string machineName)
    {
        var uses = Ambient.Relation<ISecretUseTable>();
        var table = Ambient.Relation<ISecretTable>();
        foreach (var use in uses.Where(use => use.MachineId == machineId).ToList())
        {
            if (table.FindByIdOrDefault(use.OwnerId, use.Name) is { Kind: SecretKind.Secret } secret && secret.Version == use.Version)
            {
                secret.RotationReason = $"It was given to {machineName}, which was removed.";
                table.Upsert(secret);
            }
            uses.RemoveById(use.OwnerId, use.Name, use.MachineId);
        }
    }

    /// <summary>Keeps the replaced value of a secret for masking, and forgets those too old or too many.</summary>
    public static void Retire(SecretEntry secret, DateTime when)
    {
        if (secret.Kind != SecretKind.Secret) return;
        var versions = Ambient.Relation<ISecretVersionTable>();
        versions.Upsert(new() { SecretId = secret.SecretId, Version = secret.Version, ProtectedValue = secret.ProtectedValue, RetiredAt = when });
        var kept = versions.ListById(secret.SecretId).OrderByDescending(version => version.Version).ToList();
        foreach (var old in kept.Where((version, index) => index >= RetainedVersions || when - version.RetiredAt > RetainedFor))
            versions.RemoveById(old.SecretId, old.Version);
    }
}

public sealed class SecretSetHandler(IDomainSignals signals) : IEventHandler<SecretSet, SecretCommandResult>
{
    public SecretCommandResult? Validate(SecretSet ev)
    {
        var valid = SecretNames.IsValid(ev.Name) && !string.IsNullOrEmpty(ev.SecretId) && ev.Version >= 1 && ev.Kind switch
        {
            SecretKind.Secret => ev.ProtectedValue.Length > 0 && ev.Value.Length == 0,
            SecretKind.Variable => ev.ProtectedValue.Length == 0 && ev.Value.Length is > 0 and <= SecretNames.MaxValueLength && !ev.HostOnly,
            _ => false
        } && ev.RotateEveryDays is >= 0 and <= Secrets.MaxRotateEveryDays;
        return valid ? null : new() { Outcome = CommandOutcome.Invalid };
    }

    public SecretCommandResult Consume(SecretSet ev)
    {
        if (!Secrets.MayChange(ev.UserId, ev.OwnerId)) return new() { Outcome = CommandOutcome.Forbidden };
        var table = Ambient.Relation<ISecretTable>();
        var current = table.FindByIdOrDefault(ev.OwnerId, ev.Name);
        if (current == null ? ev.Version != 1 : current.SecretId != ev.SecretId || current.Version != ev.Version - 1)
            return new() { Outcome = CommandOutcome.Invalid, Conflict = true };
        if (current != null && current.Kind != ev.Kind) return new() { Outcome = CommandOutcome.Invalid };
        if (current != null) Secrets.Retire(current, ev.When);
        table.Upsert(new()
        {
            OwnerId = ev.OwnerId, Name = ev.Name, Kind = ev.Kind, SecretId = ev.SecretId, Version = ev.Version,
            ProtectedValue = ev.ProtectedValue, Value = ev.Value, Hint = ev.Hint, SetById = ev.UserId, SetAt = ev.When, HostOnly = ev.HostOnly,
            Machines = ev.Machines.Length > 0 ? ev.Machines : "[]", RotateEveryDays = ev.RotateEveryDays
        });
        Audit.Record(ev.When, ev.UserId, current == null ? "secret.set" : "secret.rotated", ev.Name,
            $"{Secrets.ScopeOf(ev.OwnerId)} {(ev.HostOnly ? "host-only " : "")}{ev.Kind.ToString().ToLowerInvariant()}, version {ev.Version}");
        if (ev.Kind == SecretKind.Secret) signals.SecretsChanged();
        return new() { Outcome = CommandOutcome.Ok };
    }
}

public sealed class SecretDeletedHandler : IEventHandler<SecretDeleted, SecretCommandResult>
{
    public SecretCommandResult? Validate(SecretDeleted ev) => null;

    /// <summary>Removes the row, so nothing resolves it any more. Its ciphertext stays in the events, readable only with the key
    /// ring, and as a replaced value for masking.</summary>
    public SecretCommandResult Consume(SecretDeleted ev)
    {
        var table = Ambient.Relation<ISecretTable>();
        if (table.FindByIdOrDefault(ev.OwnerId, ev.Name) is not { } secret) return new() { Outcome = CommandOutcome.NotFound };
        if (!Secrets.MayChange(ev.UserId, ev.OwnerId)) return new() { Outcome = CommandOutcome.Forbidden };
        Secrets.Retire(secret, ev.When);
        table.RemoveById(ev.OwnerId, ev.Name);
        Secrets.ForgetUses(ev.OwnerId, ev.Name);
        Audit.Record(ev.When, ev.UserId, "secret.deleted", ev.Name, $"{Secrets.ScopeOf(ev.OwnerId)} {secret.Kind.ToString().ToLowerInvariant()}");
        return new() { Outcome = CommandOutcome.Ok };
    }
}

public sealed class SecretSettingsChangedHandler : IEventHandler<SecretSettingsChanged, SecretCommandResult>
{
    public SecretCommandResult? Validate(SecretSettingsChanged ev) =>
        ev.RotateEveryDays is >= 0 and <= Secrets.MaxRotateEveryDays ? null : new() { Outcome = CommandOutcome.Invalid };

    public SecretCommandResult Consume(SecretSettingsChanged ev)
    {
        var table = Ambient.Relation<ISecretTable>();
        if (table.FindByIdOrDefault(ev.OwnerId, ev.Name) is not { } secret) return new() { Outcome = CommandOutcome.NotFound };
        if (!Secrets.MayChange(ev.UserId, ev.OwnerId)) return new() { Outcome = CommandOutcome.Forbidden };
        if (ev.HostOnly && secret.Kind != SecretKind.Secret) return new() { Outcome = CommandOutcome.Invalid };
        secret.HostOnly = ev.HostOnly;
        secret.Machines = ev.Machines;
        secret.RotateEveryDays = ev.RotateEveryDays;
        table.Upsert(secret);
        var machines = Secrets.Ids(ev.Machines);
        Audit.Record(ev.When, ev.UserId, "secret.settings", ev.Name,
            $"{Secrets.ScopeOf(ev.OwnerId)}{(ev.HostOnly ? ", host-only" : "")}, {(machines.Length == 0 ? "any machine" : $"machines {string.Join(" ", machines)}")}" +
            (ev.RotateEveryDays > 0 ? $", rotate every {ev.RotateEveryDays} days" : ""));
        return new() { Outcome = CommandOutcome.Ok };
    }
}

public sealed class SecretsUsedHandler : IEventHandler<SecretsUsed>
{
    public void Validate(SecretsUsed ev) { }

    public void Consume(SecretsUsed ev)
    {
        var uses = Ambient.Relation<ISecretUseTable>();
        foreach (var use in ev.Uses)
            uses.Upsert(new() { OwnerId = use.OwnerId, Name = use.Name, MachineId = ev.MachineId, Version = use.Version, SessionId = ev.SessionId, At = ev.When });
        Audit.Record(ev.When, ev.PersonId, "secret.used", $"session {ev.SessionId}", $"on machine {ev.MachineId}: {ev.Detail}");
    }
}
