using System.Net.Http.Json;
using System.Text.Json;
using BoSoFa.Protocol;
using BoSoFa.Server.Btdb;
using BoSoFa.Server.Live;
using BoSoFa.Server.Domain;
using BTDB.Replication.EventSourcing;

namespace BoSoFa.Server.Machines;

/// <summary>
/// The long-poll transport (plan §5.3). A channel lives on the node that opened it. Without sticky load balancing any
/// request can land on another node, which forwards it on the peer port to the node named by the machine's presence.
/// </summary>
public sealed class PollEndpoints(LongPollChannels channels, DaemonPump pump, LiveRelay relay, BtdbNode node, TimeProvider time,
    IHostApplicationLifetime lifetime, ILogger<PollEndpoints> logger) : BackgroundService
{
    public const string PeerPrefix = "/internal/poll/";
    static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public async Task OpenAsync(HttpContext http)
    {
        var hello = await http.Request.ReadFromJsonAsync(ProtocolJsonContext.Default.HelloMessage, http.RequestAborted);
        var refusal = hello == null ? "Expected hello." : DaemonPump.RefusalOf(hello);
        if (refusal != null)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsync(refusal, http.RequestAborted);
            return;
        }
        var channel = new LongPollChannel(http.User.MachineId()!.Value, time);
        channels.Add(channel);
        _ = Task.Run(async () =>
        {
            try { await pump.RunAsync(channel, channel.Id, channel.MachineId, hello!, lifetime.ApplicationStopping); }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException) { }
            catch (Exception exception) { logger.LogError(exception, "Long-poll connection of machine {MachineId} failed", channel.MachineId); }
            finally
            {
                channel.Abort();
                // Stays a moment longer, so the daemon can still collect the last messages and the reason.
                await Task.Delay(TimeSpan.FromSeconds(5));
                channels.Remove(channel);
            }
        });
        await http.Response.WriteAsJsonAsync(new PollOpened(channel.Id), ProtocolJsonContext.Default.PollOpened, cancellationToken: http.RequestAborted);
    }

    public Task SendAsync(HttpContext http, string connection) => ServeAsync(http, http.User.MachineId()!.Value, connection, "send");

    public Task ReceiveAsync(HttpContext http, string connection) => ServeAsync(http, http.User.MachineId()!.Value, connection, "receive");

    /// <summary>Another node's request for a channel held here.</summary>
    public Task ServePeerAsync(HttpContext http, ulong machineId, string connection, string action)
    {
        if (relay.PeerKey is { } key && LiveRelay.Authorized(http, key)) return ServeLocalAsync(http, machineId, connection, action, notHere: 404);
        http.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    async Task ServeAsync(HttpContext http, ulong machineId, string connection, string action)
    {
        if (channels.Get(connection) != null)
            await ServeLocalAsync(http, machineId, connection, action, notHere: 404);
        else if (OwnerOf(machineId, connection) is { } owner)
            await ForwardAsync(http, owner, machineId, connection, action);
        else
            http.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    async Task ServeLocalAsync(HttpContext http, ulong machineId, string connection, string action, int notHere)
    {
        if (channels.Get(connection) is not { } channel || channel.MachineId != machineId)
        {
            http.Response.StatusCode = notHere;
            return;
        }
        if (action == "send")
        {
            var messages = await JsonSerializer.DeserializeAsync(http.Request.Body, ProtocolJsonContext.Default.DaemonMessageArray, http.RequestAborted);
            channel.Accept(messages ?? []);
            http.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }
        long.TryParse(http.Request.Query["after"], out var after);
        var batch = await channel.PollAsync(after, DaemonPoll.Wait, http.RequestAborted);
        if (batch == null)
        {
            http.Response.StatusCode = StatusCodes.Status410Gone;
            if (channel.CloseReason is { } reason) await http.Response.WriteAsync(reason, http.RequestAborted);
            return;
        }
        await http.Response.WriteAsJsonAsync(batch, ProtocolJsonContext.Default.PollBatch, cancellationToken: http.RequestAborted);
    }

    string? OwnerOf(ulong machineId, string connection)
    {
        if (node.Publisher is not { } publisher) return null;
        using var query = publisher.StartQuery();
        return Ambient.Relation<IMachinePresenceTable>().FindByIdOrDefault(machineId) is { } presence &&
               presence.ConnectionId == connection && presence.PeerUrl.Length > 0
            ? presence.PeerUrl
            : null;
    }

    async Task ForwardAsync(HttpContext http, string owner, ulong machineId, string connection, string action)
    {
        using var request = new HttpRequestMessage(new HttpMethod(http.Request.Method),
            $"{owner.TrimEnd('/')}{PeerPrefix}{machineId}/{connection}/{action}{http.Request.QueryString}");
        request.Headers.Authorization = new("Bearer", relay.PeerKey);
        if (action == "send") request.Content = new StreamContent(http.Request.Body);
        try
        {
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, http.RequestAborted);
            http.Response.StatusCode = (int)response.StatusCode;
            http.Response.ContentType = response.Content.Headers.ContentType?.ToString();
            await response.Content.CopyToAsync(http.Response.Body, http.RequestAborted);
        }
        catch (HttpRequestException)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound; // that node is gone: the daemon opens a new channel
        }
    }

    /// <summary>Daemons that stopped polling.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                foreach (var channel in channels.All.Where(channel => !channel.IsClosed && channel.IsIdle(DaemonPoll.Idle)))
                    channel.Abort();
        }
        catch (OperationCanceledException) { }
    }
}
