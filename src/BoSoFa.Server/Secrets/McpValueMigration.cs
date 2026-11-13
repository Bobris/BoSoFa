using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using BoSoFa.Protocol;
using BoSoFa.Server.Accounts;
using BoSoFa.Server.Btdb;
using BoSoFa.Server.Domain;
using BTDB.Replication.EventSourcing;
using Microsoft.AspNetCore.DataProtection;

namespace BoSoFa.Server.Secrets;

/// <summary>
/// Once at start, moves the values that older MCP server definitions keep encrypted in themselves to shared secrets named
/// <c>&lt;SERVER&gt;_&lt;NAME&gt;</c>, and makes the definitions refer to them (docs/secrets.md §4.1). Shared, because every member's
/// sessions used those values before; a person may set a value of their own under the same name. Every node may run it: a
/// definition already moved is left alone, so the second is a no-op.
/// </summary>
public sealed partial class McpValueMigration(BtdbNode node, IDataProtectionProvider protection, ILogger<McpValueMigration> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var publisher = await node.Ready.WaitAsync(stoppingToken);
            var moved = await MoveAsync(publisher, protection, logger, stoppingToken);
            if (moved > 0) logger.LogInformation("Moved the values of {Count} MCP servers to shared secrets", moved);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            logger.LogWarning(exception, "Moving the values of MCP servers to shared secrets failed; they stay usable where they are");
        }
    }

    /// <summary>The shared secret a value of an older definition becomes.</summary>
    internal static string SecretNameOf(string server, string value)
    {
        var name = NotNameCharacter().Replace($"{server}_{value}", "_").ToUpperInvariant();
        if (char.IsAsciiDigit(name[0])) name = "_" + name;
        return name.Length > 64 ? name[..64] : name;
    }

    [GeneratedRegex("[^A-Za-z0-9_]")]
    private static partial Regex NotNameCharacter();

    /// <returns>How many definitions moved.</returns>
    internal static async Task<int> MoveAsync(EventPublisher publisher, IDataProtectionProvider protection, ILogger logger, CancellationToken cancellation)
    {
        List<McpServerDefinition> older;
        using (publisher.StartQuery())
            older = Ambient.Relation<IMcpServerTable>().Where(server => server.ProtectedSecrets.Length > 0).ToList();
        var moved = 0;
        foreach (var server in older)
        {
            Dictionary<string, string> values;
            try
            {
                values = JsonSerializer.Deserialize(protection.CreateProtector(CatalogEndpoints.Purpose, server.SecretId).Unprotect(server.ProtectedSecrets),
                    ProtocolJsonContext.Default.DictionaryStringString)!;
            }
            catch (CryptographicException exception)
            {
                logger.LogWarning(exception, "The values of the MCP server {Server} cannot be opened; it keeps them", server.Name);
                continue;
            }
            var secrets = new List<MovedSecret>();
            var references = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, value) in values)
            {
                var secretName = SecretNameOf(server.Name, name);
                references[name] = SecretNames.Reference(SecretKind.Secret, secretName);
                var secretId = Guid.NewGuid().ToString("N");
                secrets.Add(new()
                {
                    Name = secretName, SecretId = secretId, ProtectedValue = SecretEndpoints.Protect(protection, secretId, 1, value),
                    Hint = AccountSecrets.Hint(value)
                });
            }
            var result = await publisher.PublishAndWaitAsync(new McpValuesMoved { Name = server.Name, Values = CatalogEndpoints.Json(references), Secrets = secrets },
                cancellation);
            if (result.Outcome == CommandOutcome.Ok) moved++;
        }
        return moved;
    }
}
