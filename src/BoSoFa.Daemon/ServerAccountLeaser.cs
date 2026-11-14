using System.Security.Cryptography;
using BoSoFa.Daemon.Sessions;
using BoSoFa.Protocol;

namespace BoSoFa.Daemon;

/// <summary>Leases account secrets from the server with a fresh machine token per lease (leases are rare: once per
/// harness start). The secret is never written to disk.</summary>
public sealed class ServerAccountLeaser(DaemonState state, ECDsa key) : IAccountLeaser, IProfileSource, IBlobUploader
{
    public async Task<AccountLeaseResponse> LeaseAsync(ulong accountId, ulong sessionId, CancellationToken cancellation)
    {
        using var client = new ServerClient(state.Server);
        var token = await client.GetTokenAsync(state.MachineId, key, cancellation);
        return await client.LeaseAccountAsync(token.AccessToken, accountId, sessionId, cancellation);
    }

    public async Task<SessionProfileResponse> GetProfileAsync(ulong sessionId, CancellationToken cancellation)
    {
        using var client = new ServerClient(state.Server);
        var token = await client.GetTokenAsync(state.MachineId, key, cancellation);
        return await client.GetSessionProfileAsync(token.AccessToken, sessionId, cancellation);
    }

    public async Task<SessionSecretName[]> ListSecretNamesAsync(ulong sessionId, CancellationToken cancellation)
    {
        using var client = new ServerClient(state.Server);
        var token = await client.GetTokenAsync(state.MachineId, key, cancellation);
        return await client.GetSessionSecretNamesAsync(token.AccessToken, sessionId, cancellation);
    }

    public async Task<BlobRef> UploadAsync(string file, CancellationToken cancellation)
    {
        using var client = new ServerClient(state.Server);
        var token = await client.GetTokenAsync(state.MachineId, key, cancellation);
        return await client.UploadBlobAsync(token.AccessToken, file, cancellation);
    }

    public async Task DownloadBlobAsync(string sha256, string file, CancellationToken cancellation)
    {
        using var client = new ServerClient(state.Server);
        var token = await client.GetTokenAsync(state.MachineId, key, cancellation);
        await client.DownloadBlobAsync(token.AccessToken, sha256, file, cancellation);
    }
}
