using BoSoFa.Protocol;
using BoSoFa.Server.Domain;
using BTDB.Replication.EventSourcing;
using Microsoft.AspNetCore.DataProtection;

namespace BoSoFa.Server.Secrets;

/// <summary>References to secrets and variables are not set, or name the wrong kind; the message names each of them.</summary>
public sealed class MissingSecretsException(string message) : Exception(message);

/// <summary>
/// Resolves the references of a configuration for one person (docs/secrets.md §4.2): their own value of a name, else the shared
/// one. Collects what is missing, to name it all at once, and what was used, for the audit. Must run inside a query scope; the
/// values stay in memory on this node.
/// </summary>
public sealed class SecretResolver(ulong personId, IDataProtectionProvider protection)
{
    readonly SortedSet<string> _missing = new(StringComparer.Ordinal);
    readonly SortedDictionary<string, string> _used = new(StringComparer.Ordinal);
    readonly HashSet<string> _secrets = new(StringComparer.Ordinal);

    /// <summary>The value with its references replaced; a missing one is replaced by nothing and remembered.</summary>
    public string Resolve(string value) => SecretReferences.Replace(value, Lookup);

    public Dictionary<string, string> Resolve(IReadOnlyDictionary<string, string> values) =>
        values.ToDictionary(pair => pair.Key, pair => Resolve(pair.Value), StringComparer.Ordinal);

    string Lookup(SecretReference reference)
    {
        var table = Ambient.Relation<ISecretTable>();
        var entry = (personId == 0 ? null : table.FindByIdOrDefault(personId, reference.Name)) ?? table.FindByIdOrDefault(0, reference.Name);
        if (entry == null)
        {
            _missing.Add($"the {Word(reference.Kind)} {reference.Name} is not set");
            return "";
        }
        if (entry.Kind != reference.Kind)
        {
            _missing.Add($"{reference.Name} is a {Word(entry.Kind)}, so write {SecretNames.Reference(entry.Kind, reference.Name)}");
            return "";
        }
        _used[reference.Name] = $"{reference.Name} version {entry.Version} ({(entry.OwnerId == 0 ? "shared" : "personal")})";
        var value = SecretEndpoints.ValueOf(entry, protection);
        if (entry.Kind == SecretKind.Secret) _secrets.Add(value);
        return value;
    }

    /// <summary>Values that are secrets though no reference gave them (those an older MCP definition keeps in itself).</summary>
    public void Remember(IEnumerable<string> secrets) => _secrets.UnionWith(secrets);

    /// <summary>The secrets among the resolved values, for whoever gets them to mask; variables are not among them.</summary>
    public string[] Secrets => [.. _secrets];

    static string Word(SecretKind kind) => kind == SecretKind.Secret ? "secret" : "variable";

    /// <summary>Names what is missing: nothing is used with an empty value in its place.</summary>
    public void ThrowIfMissing()
    {
        if (_missing.Count == 0) return;
        var what = string.Join("; ", _missing);
        throw new MissingSecretsException(char.ToUpperInvariant(what[0]) + what[1..] +
            ". Set it under Secrets and variables, or ask an admin to share it.");
    }

    /// <summary>The names and versions used, never their values; null when nothing was.</summary>
    public string? Used => _used.Count == 0 ? null : string.Join(", ", _used.Values);
}
