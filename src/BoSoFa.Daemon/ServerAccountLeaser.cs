using System.Security.Cryptography;
using BoSoFa.Daemon.Sessions;
using BoSoFa.Protocol;

namespace BoSoFa.Daemon;

/// <summary>Leases account secrets from the server with a fresh machine token per lease (leases are rare: once per
/// harness start). The secret is never written to disk.</summary>
public sealed class ServerAccountLeaser(DaemonState state, ECDsa key) : IAccountLeaser
{
    public async Task<AccountLeaseResponse> LeaseAsync(ulong accountId, ulong sessionId, CancellationToken cancellation)
    {
        using var client = new ServerClient(state.Server);
        var token = await client.GetTokenAsync(state.MachineId, key, cancellation);
        return await client.LeaseAccountAsync(token.AccessToken, accountId, sessionId, cancellation);
    }
}
