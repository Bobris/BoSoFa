using BoSoFa.Protocol;
using BoSoFa.Server.Domain;
using BTDB.Replication.EventSourcing;
using Microsoft.AspNetCore.DataProtection;

namespace BoSoFa.Server.Secrets;

/// <summary>References to secrets and variables are not set, or name the wrong kind; the message names each of them.</summary>
public sealed class MissingSecretsException(string message) : Exception(message);

/// <summary>
/// Resolves the references of a configuration for one person (docs/secrets.md §4.2): the environment's value of a name, if the
/// session has one, then their own, then the shared one, each only if it is allowed on the session's machine. Collects what is
/// missing, to name it all at once, and what was used, for the audit. Must run inside a query scope; the values stay in memory on
/// this node.
/// </summary>
public sealed class SecretResolver(ulong personId, IDataProtectionProvider protection, ulong machineId = 0, SecretEnvironment? environment = null)
{
    readonly SortedSet<string> _missing = new(StringComparer.Ordinal);
    readonly SortedSet<string> _refused = new(StringComparer.Ordinal);
    readonly SortedDictionary<string, (SecretUseRecord Use, string Text)> _used = new(StringComparer.Ordinal);
    readonly HashSet<string> _secrets = new(StringComparer.Ordinal);

    /// <summary>The value with its references replaced; a missing one is replaced by nothing and remembered.</summary>
    /// <param name="shell">The value goes to a harness's environment, and so to the agent's shell, where no host-only secret
    /// goes (docs/secrets.md §6).</param>
    public string Resolve(string value, bool shell = false) => SecretReferences.Replace(value, reference => Lookup(reference, shell));

    public Dictionary<string, string> Resolve(IReadOnlyDictionary<string, string> values, bool shell = false) =>
        values.ToDictionary(pair => pair.Key, pair => Resolve(pair.Value, shell), StringComparer.Ordinal);

    IEnumerable<ulong> Owners()
    {
        if (environment != null) yield return environment.Id;
        if (personId != 0) yield return personId;
        yield return 0;
    }

    string Lookup(SecretReference reference, bool shell)
    {
        var table = Ambient.Relation<ISecretTable>();
        SecretEntry? entry = null;
        var elsewhere = false;
        foreach (var owner in Owners())
        {
            if (table.FindByIdOrDefault(owner, reference.Name) is not { } found) continue;
            if (BoSoFa.Server.Domain.Secrets.AllowedOn(found.Machines, machineId))
            {
                entry = found;
                break;
            }
            elsewhere = true; // a value for other machines; a wider one may still be for this
        }
        if (entry == null)
        {
            _missing.Add(elsewhere
                ? $"the {Word(reference.Kind)} {reference.Name} is not for this machine"
                : $"the {Word(reference.Kind)} {reference.Name} is not set");
            return "";
        }
        if (entry.Kind != reference.Kind)
        {
            _missing.Add($"{reference.Name} is a {Word(entry.Kind)}, so write {SecretNames.Reference(entry.Kind, reference.Name)}");
            return "";
        }
        if (shell && entry.HostOnly)
        {
            _refused.Add($"{reference.Name} is for MCP servers only, so it cannot be in the environment of a session");
            return "";
        }
        var scope = entry.OwnerId == 0 ? "shared" : entry.OwnerId == environment?.Id ? $"environment {environment.Name}" : "personal";
        _used[reference.Name] = (new() { OwnerId = entry.OwnerId, Name = entry.Name, Version = entry.Version },
            $"{reference.Name} version {entry.Version} ({scope})");
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
        if (_missing.Count == 0 && _refused.Count == 0) return;
        var what = string.Join("; ", _missing.Concat(_refused));
        throw new MissingSecretsException(char.ToUpperInvariant(what[0]) + what[1..] +
            (_missing.Count > 0 ? ". Set it under Secrets and variables, or ask an admin to share it." : "."));
    }

    /// <summary>The names and versions used, never their values; null when nothing was.</summary>
    public string? Used => _used.Count == 0 ? null : string.Join(", ", _used.Values.Select(used => used.Text));

    /// <summary>What was used, to record where it went (<see cref="SecretsUsed"/>); null when nothing was.</summary>
    public SecretsUsed? UsesOf(Session session) => _used.Count == 0
        ? null
        : new() { SessionId = session.Id, MachineId = session.MachineId, PersonId = session.CreatedById, Uses = [.. _used.Values.Select(used => used.Use)], Detail = Used! };

    /// <summary>Records where the values went and audits it; a failure to is logged, never stops the session.</summary>
    public async Task RecordAsync(EventPublisher publisher, Session session, ILogger logger)
    {
        if (UsesOf(session) is not { } uses) return;
        try { await publisher.PublishAsync(uses); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException)
        {
            logger.LogWarning("Could not record the secrets session {Session} used: {Error}", session.Id, exception.Message);
        }
    }
}
