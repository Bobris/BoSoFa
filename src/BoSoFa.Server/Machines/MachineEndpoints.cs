using System.Net.WebSockets;
using System.Security.Cryptography;
using BoSoFa.Protocol;
using BoSoFa.Server.Btdb;
using BoSoFa.Server.Domain;
using BoSoFa.Server.Live;
using BTDB.ODBLayer;
using System.Text.Json;
using BTDB.Replication.EventSourcing;
using Microsoft.AspNetCore.Http.HttpResults;

namespace BoSoFa.Server.Machines;

public static class MachineEndpoints
{
    // No vowels (no words) and no look-alikes (0/O, 1/I/L, 5/S, 8/B, 2/Z).
    const string UserCodeAlphabet = "CDFGHJKMNPQRTVWXY34679";

    public static void MapMachineEndpoints(this WebApplication app)
    {
        var daemon = app.MapGroup("/api/daemon/v1");
        daemon.MapPost("/enroll/start", StartEnrollmentAsync);
        daemon.MapPost("/enroll/poll", PollEnrollmentAsync);
        daemon.MapPost("/token", IssueTokenAsync);
        var authenticated = daemon.MapGroup("").RequireAuthorization(DaemonTokens.Scheme);
        authenticated.MapGet("/machine", GetOwnMachineAsync);
        authenticated.Map(DaemonSocket.Path["/api/daemon/v1".Length..], ConnectAsync);

        var api = app.MapGroup("/api/v1").RequireAuthorization();
        api.MapGet("/enrollments/{userCode}", GetEnrollmentAsync);
        api.MapPost("/enrollments/{userCode}/approve", ApproveEnrollmentAsync);
        api.MapGet("/machines", ListMachinesAsync);
    }

    static string NormalizeCode(string code) => new string(code.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray()) is
        { Length: 8 } plain ? $"{plain[..4]}-{plain[4..]}" : code;

    static string NewUserCode()
    {
        Span<char> code = stackalloc char[9];
        for (var i = 0; i < 9; i++)
            code[i] = i == 4 ? '-' : UserCodeAlphabet[RandomNumberGenerator.GetInt32(UserCodeAlphabet.Length)];
        return new(code);
    }

    static async Task<Results<Ok<EnrollStartResponse>, BadRequest<string>>> StartEnrollmentAsync(
        EnrollStartRequest request, BtdbNode node, HttpContext http)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 100) return TypedResults.BadRequest("Invalid machine name.");
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(request.PublicKey), out _);
            if (key.KeySize != 256) return TypedResults.BadRequest("The machine key must be ECDSA P-256.");
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            return TypedResults.BadRequest("Invalid public key.");
        }
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        for (var attempt = 0; ; attempt++)
        {
            var started = new EnrollmentStarted
            {
                Key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
                UserCode = NewUserCode(), Name = request.Name.Trim(), Os = request.Os, Arch = request.Arch,
                DaemonVersion = request.DaemonVersion, PublicKey = request.PublicKey
            };
            var result = await publisher.PublishAndWaitAsync(started, http.RequestAborted);
            if (result.Accepted)
                return TypedResults.Ok(new EnrollStartResponse(started.Key, started.UserCode,
                    $"{http.Request.Scheme}://{http.Request.Host}/enroll?code={started.UserCode}", result.ExpiresAt, 2));
            if (attempt == 5) throw new InvalidOperationException("Could not allocate a unique user code.");
        }
    }

    static async Task<Results<Ok<EnrollPollResponse>, NotFound>> PollEnrollmentAsync(EnrollPollRequest request,
        BtdbNode node, TimeProvider time, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using var query = publisher.StartQuery();
        if (Ambient.Relation<IEnrollmentTable>().FindByKeyOrDefault(request.EnrollmentId) is not { } enrollment)
            return TypedResults.NotFound();
        var status = enrollment.StatusAt(time.GetUtcNow().UtcDateTime);
        return TypedResults.Ok(new EnrollPollResponse(status, status == EnrollStatus.Approved ? enrollment.MachineId : null));
    }

    static async Task<Results<Ok<DaemonTokenResponse>, UnauthorizedHttpResult>> IssueTokenAsync(DaemonTokenRequest request,
        BtdbNode node, DaemonTokens tokens, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        string? publicKey;
        using (publisher.StartQuery())
            publicKey = Ambient.Relation<IMachineTable>().FindByIdOrDefault(request.MachineId)?.PublicKey;
        return publicKey != null && tokens.Issue(request, publicKey) is { } token
            ? TypedResults.Ok(token)
            : TypedResults.Unauthorized();
    }

    static async Task<Results<Ok<MachineDto>, NotFound>> GetOwnMachineAsync(BtdbNode node, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using var query = publisher.StartQuery();
        return http.User.MachineId() is { } id && Ambient.Relation<IMachineTable>().FindByIdOrDefault(id) is { } machine
            ? TypedResults.Ok(ToDto(machine))
            : TypedResults.NotFound();
    }

    static async Task<Results<Ok<EnrollmentDto>, NotFound>> GetEnrollmentAsync(string userCode, BtdbNode node,
        TimeProvider time, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using var query = publisher.StartQuery();
        if (Ambient.Relation<IEnrollmentTable>().FindByUserCodeOrDefault(NormalizeCode(userCode)) is not { } enrollment)
            return TypedResults.NotFound();
        return TypedResults.Ok(new EnrollmentDto(enrollment.UserCode, enrollment.Name, enrollment.Os, enrollment.Arch,
            enrollment.DaemonVersion, enrollment.ExpiresAt, enrollment.StatusAt(time.GetUtcNow().UtcDateTime)));
    }

    static async Task<Results<Ok<MachineDto>, Conflict<string>>> ApproveEnrollmentAsync(string userCode, BtdbNode node,
        HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        var result = await publisher.PublishAndWaitAsync(
            new EnrollmentApproved { UserCode = NormalizeCode(userCode), ApproverId = http.User.UserId() ?? 0 },
            http.RequestAborted);
        if (result.Status != EnrollStatus.Approved) return TypedResults.Conflict($"The enrollment is {result.Status}.");
        using var query = publisher.StartQuery();
        return TypedResults.Ok(ToDto(Ambient.Relation<IMachineTable>().FindByIdOrDefault(result.MachineId)!));
    }

    static async Task<MachineDto[]> ListMachinesAsync(BtdbNode node, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using var query = publisher.StartQuery();
        return Ambient.Relation<IMachineTable>().Select(machine => ToDto(machine))
            .OrderBy(machine => machine.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Must run inside a query scope. Online means some node holds the daemon's connection.</summary>
    static MachineDto ToDto(Machine machine)
    {
        var owner = Ambient.Relation<IUserTable>().FindByIdOrDefault(machine.OwnerId);
        var presence = Ambient.Relation<IMachinePresenceTable>().FindByIdOrDefault(machine.Id);
        return new(machine.Id, machine.Name, machine.Os, machine.Arch, machine.OwnerId, owner?.Name ?? "",
            presence?.DaemonVersion ?? machine.DaemonVersion, presence != null, presence?.ConnectedAt);
    }

    /// <summary>The daemon's WebSocket: hello/welcome, then every unacknowledged inbox command (and new ones as they
    /// are issued), outbox batches with their acknowledgements, deltas, and keepalive.</summary>
    static async Task ConnectAsync(HttpContext http, DaemonConnections connections, BtdbNode node, LiveHub hub,
        MachinePresenceService presence, LiveRelay relay, TimeProvider time, ILoggerFactory loggers, IHostApplicationLifetime lifetime)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var machineId = http.User.MachineId()!.Value;
        var logger = loggers.CreateLogger("BoSoFa.Server.Daemon");
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        await presence.Ready.WaitAsync(http.RequestAborted); // this node's older connections are expired first
        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        // Kestrel does not abort upgraded connections on a graceful stop; without this the host waits its whole
        // shutdown timeout. The daemon reconnects (to another pod in a cluster) and resends what is unacknowledged.
        using var connectionScope = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, lifetime.ApplicationStopping);
        var cancellation = connectionScope.Token;
        if (await DaemonSocket.ReceiveAsync(socket, cancellation) is not HelloMessage hello)
        {
            await socket.CloseAsync(WebSocketCloseStatus.ProtocolError, "Expected hello.", cancellation);
            return;
        }
        if (hello.ProtocolVersion != ProtocolVersion.Current)
        {
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation,
                $"Protocol {hello.ProtocolVersion} is not supported; the server speaks {ProtocolVersion.Current}.", cancellation);
            return;
        }
        var connection = new DaemonConnections.Connection(machineId, hello.DaemonVersion, hello.HostName,
            time.GetUtcNow().UtcDateTime, socket);
        connections.Add(connection);
        // Recorded as data, so every node knows the machine is online here; a newer connection elsewhere closes this one.
        await publisher.PublishAsync(new MachineConnected
        {
            MachineId = machineId, NodeId = presence.NodeId, ConnectionId = connection.ConnectionId, PeerUrl = presence.PeerUrl,
            DaemonVersion = hello.DaemonVersion, HostName = hello.HostName
        }, cancellation);
        connection.Registered = true;
        logger.LogInformation("Machine {MachineId} connected ({HostName}, bosofad {Version})", machineId, hello.HostName,
            hello.DaemonVersion);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task? commands = null;
        try
        {
            await connection.SendAsync(new WelcomeMessage(machineId, Api.Version), cancellation);
            commands = SendCommandsAsync(connection, publisher, stop.Token);
            while (await DaemonSocket.ReceiveAsync(socket, cancellation) is { } message)
                switch (message)
                {
                    case PingMessage ping:
                        await connection.SendAsync(new PongMessage(ping.Sequence), cancellation);
                        break;
                    case CommandAckMessage ack:
                        await publisher.PublishAsync(new MachineCommandsAcked { MachineId = machineId, CommandIds = [..ack.Ids] },
                            cancellation);
                        break;
                    case OutboxMessage outbox:
                        await connection.SendAsync(await StoreReportsAsync(publisher, machineId, outbox), cancellation);
                        break;
                    case DeltaMessage delta:
                        if (SessionBelongsTo(publisher, delta.SessionId, machineId))
                        {
                            var live = new LiveDelta(delta.SessionId, delta.Ref, delta.Text);
                            hub.Delta(live);
                            relay.Publish(machineId, live); // to nodes whose clients watch this machine's sessions
                        }
                        break;
                }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException) { }
        finally
        {
            connections.Remove(connection);
            try
            {
                await publisher.PublishAsync(
                    new MachineDisconnected { MachineId = machineId, ConnectionId = connection.ConnectionId });
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or IOException)
            {
                // The node is stopping; it expires its connections when it starts again (MachinePresenceService).
            }
            await stop.CancelAsync();
            if (commands != null)
                try { await commands; }
                catch (Exception) { /* the connection is gone */ }
            logger.LogInformation("Machine {MachineId} disconnected", machineId);
        }
    }

    /// <summary>Sends inbox commands in ID order: all of them after connecting, then each new one once. The daemon
    /// acknowledges what it applied; the rest is sent again on the next connection.</summary>
    static async Task SendCommandsAsync(DaemonConnections.Connection connection, EventPublisher publisher,
        CancellationToken cancellation)
    {
        ulong sent = 0;
        while (true)
        {
            List<CommandMessage> pending;
            using (publisher.StartQuery())
                pending = Ambient.Relation<IMachineCommandTable>()
                    .ListById(connection.MachineId, new AdvancedEnumeratorParam<ulong>(EnumerationOrder.Ascending, sent,
                        KeyProposition.Excluded, 0, KeyProposition.Ignored))
                    .Select(entry => new CommandMessage(entry.Id,
                        JsonSerializer.Deserialize(entry.Command, ProtocolJsonContext.Default.MachineCommand)!))
                    .ToList();
            foreach (var command in pending)
            {
                await connection.SendAsync(command, cancellation);
                sent = command.Id;
            }
            await connection.WaitForCommandsAsync(TimeSpan.FromSeconds(30), cancellation);
        }
    }

    static async Task<OutboxAckMessage> StoreReportsAsync(EventPublisher publisher, ulong machineId, OutboxMessage outbox)
    {
        await publisher.PublishAsync(new DaemonReportsReceived
        {
            MachineId = machineId,
            RecordsJson = JsonSerializer.Serialize(outbox.Records, ProtocolJsonContext.Default.OutboxRecordArray)
        });
        using var query = publisher.StartQuery();
        var sessions = Ambient.Relation<ISessionTable>();
        return new(outbox.Records.Select(record => record.SessionId).Distinct().Select(sessionId =>
            new OutboxAck(sessionId, sessions.FindByIdOrDefault(sessionId) is { } session && session.MachineId == machineId
                // A session the server does not know (or not of this machine) can never apply: acknowledge it all.
                ? session.LastReportSeq
                : outbox.Records.Where(record => record.SessionId == sessionId).Max(record => record.Seq))).ToArray());
    }

    static bool SessionBelongsTo(EventPublisher publisher, ulong sessionId, ulong machineId)
    {
        using var query = publisher.StartQuery();
        return Ambient.Relation<ISessionTable>().FindByIdOrDefault(sessionId)?.MachineId == machineId;
    }
}
