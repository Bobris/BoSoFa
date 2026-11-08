using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using BoSoFa.Protocol;
using BoSoFa.Server.Live;

namespace BoSoFa.Server.Machines;

/// <summary>Calls a machine and waits for its answer, through the node holding its daemon (plan §4.2).</summary>
public sealed class MachineRpcClient(DaemonConnections connections, LiveRelay relay)
{
    public const string PathPrefix = "/internal/machines/";
    static readonly RpcFailure Offline = new("The machine is offline.");
    static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Transfers move whole files through the blob store; everything else should be instant.</summary>
    static TimeSpan TimeoutOf(MachineRpc request) =>
        request is FileFetchRequest or FileStoreRequest ? TimeSpan.FromMinutes(15) : TimeSpan.FromSeconds(30);

    public async Task<MachineRpcResult> CallAsync(ulong machineId, MachineRpc request, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeoutOf(request));
        try
        {
            if (connections.Get(machineId) is { } connection) return await connection.CallAsync(request, timeout.Token);
            return relay.OwnerPeerUrl(machineId) is { } owner ? await ForwardAsync(owner, machineId, request, timeout.Token) : Offline;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return new RpcFailure("The machine did not answer in time.");
        }
        catch (Exception exception) when (exception is WebSocketException or HttpRequestException or IOException or JsonException)
        {
            return Offline;
        }
    }

    async Task<MachineRpcResult> ForwardAsync(string owner, ulong machineId, MachineRpc request, CancellationToken cancellation)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, owner.TrimEnd('/') + PathPrefix + machineId + "/rpc")
        {
            Content = JsonContent.Create(request, ProtocolJsonContext.Default.MachineRpc)
        };
        message.Headers.Authorization = new("Bearer", relay.PeerKey);
        using var response = await Http.SendAsync(message, cancellation);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(ProtocolJsonContext.Default.MachineRpcResult, cancellation) ?? Offline;
    }

    /// <summary>The internal endpoint: another node asks for a machine whose daemon is connected here.</summary>
    public async Task ServeAsync(HttpContext http, ulong machineId)
    {
        if (relay.PeerKey is not { } key || !LiveRelay.Authorized(http, key))
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        var request = await http.Request.ReadFromJsonAsync(ProtocolJsonContext.Default.MachineRpc, http.RequestAborted);
        MachineRpcResult result = request == null || connections.Get(machineId) is not { } connection
            ? Offline
            : await CallConnectionAsync(connection, request, http.RequestAborted);
        await http.Response.WriteAsJsonAsync(result, ProtocolJsonContext.Default.MachineRpcResult, cancellationToken: http.RequestAborted);
    }

    static async Task<MachineRpcResult> CallConnectionAsync(DaemonConnections.Connection connection, MachineRpc request, CancellationToken cancellation)
    {
        try { return await connection.CallAsync(request, cancellation); }
        catch (WebSocketException) { return Offline; }
    }
}
