using BoSoFa.Protocol;
using BTDB.ODBLayer;
using BTDB.Replication.EventSourcing;

namespace BoSoFa.Server.Domain;

/// <summary>
/// A secret or a variable (docs/secrets.md), one name per owner: a personal one of <see cref="OwnerId"/>, or a shared one
/// with <see cref="OwnerId"/> 0. A secret's value is only Data Protection ciphertext, made by the server before publishing with
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

    /// <summary>A person sets their own values; only an admin sets shared ones.</summary>
    public static bool MayChange(ulong userId, ulong ownerId) => ownerId == 0 ? Catalog.IsAdmin(userId) : ownerId == userId;

    public static string ScopeOf(ulong ownerId) => ownerId == 0 ? "shared" : "personal";

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
            SecretKind.Variable => ev.ProtectedValue.Length == 0 && ev.Value.Length is > 0 and <= SecretNames.MaxValueLength,
            _ => false
        };
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
            ProtectedValue = ev.ProtectedValue, Value = ev.Value, Hint = ev.Hint, SetById = ev.UserId, SetAt = ev.When
        });
        Audit.Record(ev.When, ev.UserId, current == null ? "secret.set" : "secret.rotated", ev.Name,
            $"{Secrets.ScopeOf(ev.OwnerId)} {ev.Kind.ToString().ToLowerInvariant()}, version {ev.Version}");
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
        Audit.Record(ev.When, ev.UserId, "secret.deleted", ev.Name, $"{Secrets.ScopeOf(ev.OwnerId)} {secret.Kind.ToString().ToLowerInvariant()}");
        return new() { Outcome = CommandOutcome.Ok };
    }
}
