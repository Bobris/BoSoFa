using System.Security.Cryptography;
using System.Text.Json;
using BoSoFa.Protocol;
using BoSoFa.Server.Accounts;
using BoSoFa.Server.Btdb;
using BoSoFa.Server.Domain;
using BTDB.Replication.EventSourcing;
using Microsoft.AspNetCore.DataProtection;

namespace BoSoFa.Server.Secrets;

/// <summary>
/// The server's second line (docs/secrets.md §5.3): masks every secret this server holds (people's and shared secrets, their
/// values replaced in the last 30 days, harness accounts, and values older MCP definitions keep) in what it is about to publish,
/// since the event log is kept forever. Read again at the next use after the domain says secrets changed; a value read once
/// stays masked for as long as this node runs.
/// </summary>
public sealed class ServerSecretMasker(IServiceProvider services, IDataProtectionProvider protection, ILogger<ServerSecretMasker> logger)
{
    readonly SecretMasker _masker = new();
    readonly Lock _gate = new();
    volatile bool _stale = true;

    /// <summary>A secret, an account or an MCP definition changed.</summary>
    public void Invalidate() => _stale = true;

    /// <summary>The masker, with what the database holds now.</summary>
    public SecretMasker Masker
    {
        get
        {
            if (_stale) Reload();
            return _masker;
        }
    }

    void Reload()
    {
        lock (_gate)
        {
            // Resolved here: the node's dispatcher signals this masker, so it cannot be a constructor argument.
            if (!_stale || services.GetRequiredService<BtdbNode>().Publisher is not { } publisher) return;
            _stale = false; // before reading: a change while it reads makes it read again next time
            var values = new List<string>();
            using (publisher.StartQuery())
            {
                foreach (var secret in Ambient.Relation<ISecretTable>().Where(secret => secret.Kind == SecretKind.Secret))
                    Open(values, () => SecretEndpoints.ValueOf(secret, protection));
                foreach (var version in Ambient.Relation<ISecretVersionTable>())
                    Open(values, () => SecretEndpoints.Open(protection, version.SecretId, version.Version, version.ProtectedValue));
                foreach (var account in Ambient.Relation<IHarnessAccountTable>())
                    Open(values, () => AccountEndpoints.SecretOf(account, protection));
                foreach (var server in Ambient.Relation<IMcpServerTable>().Where(server => server.ProtectedSecrets.Length > 0))
                    Open(values, () => JsonSerializer.Deserialize(protection.CreateProtector(CatalogEndpoints.Purpose, server.SecretId)
                        .Unprotect(server.ProtectedSecrets), ProtocolJsonContext.Default.DictionaryStringString)!.Values);
            }
            _masker.Retain(values);
        }
    }

    void Open(List<string> values, Func<string> open) => Open(values, () => (IEnumerable<string>)[open()]);

    void Open(List<string> values, Func<IEnumerable<string>> open)
    {
        try { values.AddRange(open()); }
        catch (CryptographicException exception) { logger.LogWarning(exception, "A stored secret cannot be opened, so it is not masked"); }
    }
}
