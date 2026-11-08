using System.Net.WebSockets;
using System.Security.Cryptography;
using BoSoFa.Protocol;
using BoSoFa.Server.Btdb;
using BoSoFa.Server.Domain;
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

    static async Task<Results<Ok<MachineDto>, NotFound>> GetOwnMachineAsync(BtdbNode node, DaemonConnections connections,
        HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using var query = publisher.StartQuery();
        return http.User.MachineId() is { } id && Ambient.Relation<IMachineTable>().FindByIdOrDefault(id) is { } machine
            ? TypedResults.Ok(ToDto(machine, connections))
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
        DaemonConnections connections, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        var result = await publisher.PublishAndWaitAsync(
            new EnrollmentApproved { UserCode = NormalizeCode(userCode), ApproverId = http.User.UserId() ?? 0 },
            http.RequestAborted);
        if (result.Status != EnrollStatus.Approved) return TypedResults.Conflict($"The enrollment is {result.Status}.");
        using var query = publisher.StartQuery();
        return TypedResults.Ok(ToDto(Ambient.Relation<IMachineTable>().FindByIdOrDefault(result.MachineId)!, connections));
    }

    static async Task<MachineDto[]> ListMachinesAsync(BtdbNode node, DaemonConnections connections, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using var query = publisher.StartQuery();
        return Ambient.Relation<IMachineTable>().Select(machine => ToDto(machine, connections))
            .OrderBy(machine => machine.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    static MachineDto ToDto(Machine machine, DaemonConnections connections)
    {
        var owner = Ambient.Relation<IUserTable>().FindByIdOrDefault(machine.OwnerId);
        var connection = connections.Get(machine.Id);
        return new(machine.Id, machine.Name, machine.Os, machine.Arch, machine.OwnerId, owner?.Name ?? "",
            connection?.DaemonVersion ?? machine.DaemonVersion, connection != null, connection?.ConnectedAt);
    }

    /// <summary>The daemon's WebSocket: hello/welcome, then keepalive. Commands and the outbox come with sessions.</summary>
    static async Task ConnectAsync(HttpContext http, DaemonConnections connections, TimeProvider time,
        ILoggerFactory loggers)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var machineId = http.User.MachineId()!.Value;
        var logger = loggers.CreateLogger("BoSoFa.Server.Daemon");
        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        var cancellation = http.RequestAborted;
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
        logger.LogInformation("Machine {MachineId} connected ({HostName}, bosofad {Version})", machineId, hello.HostName,
            hello.DaemonVersion);
        try
        {
            await DaemonSocket.SendAsync(socket, new WelcomeMessage(machineId, Api.Version), cancellation);
            while (await DaemonSocket.ReceiveAsync(socket, cancellation) is { } message)
                if (message is PingMessage ping)
                    await DaemonSocket.SendAsync(socket, new PongMessage(ping.Sequence), cancellation);
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException) { }
        finally
        {
            connections.Remove(connection);
            logger.LogInformation("Machine {MachineId} disconnected", machineId);
        }
    }
}
