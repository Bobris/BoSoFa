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
        var poll = DaemonPoll.Path["/api/daemon/v1".Length..];
        authenticated.MapPost(poll, (HttpContext http, PollEndpoints endpoints) => endpoints.OpenAsync(http));
        authenticated.MapPost(poll + "/{connection}/send", (HttpContext http, string connection, PollEndpoints endpoints) => endpoints.SendAsync(http, connection));
        authenticated.MapGet(poll + "/{connection}/receive", (HttpContext http, string connection, PollEndpoints endpoints) => endpoints.ReceiveAsync(http, connection));

        var api = app.MapGroup("/api/v1").RequireAuthorization();
        api.MapGet("/enrollments/{userCode}", GetEnrollmentAsync);
        api.MapPost("/enrollments/{userCode}/approve", ApproveEnrollmentAsync);
        api.MapGet("/machines", ListMachinesAsync);
        api.MapPut("/machines/{id}/name", RenameMachineAsync);
        api.MapDelete("/machines/{id}", RemoveMachineAsync);
        api.MapGet("/machines/{id}/resources", MeasureMachineAsync);
        api.MapPost("/machines/{id}/harnesses", InstallHarnessAsync);
        api.MapGet("/harness-releases", ListReleasesAsync);
        api.MapPost("/harness-releases", PublishReleaseAsync);
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
                DaemonVersion = request.DaemonVersion, PublicKey = request.PublicKey, InstanceId = request.InstanceId ?? Guid.Empty,
                ResourcesJson = ResourcesJson(request.Resources)
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
            enrollment.DaemonVersion, enrollment.ExpiresAt, enrollment.StatusAt(time.GetUtcNow().UtcDateTime),
            enrollment.InstanceId == Guid.Empty ? null : enrollment.InstanceId, Resources(enrollment.ResourcesJson)));
    }

    /// <param name="name">What to call the machine; its host name when absent.</param>
    static async Task<Results<Ok<MachineDto>, Conflict<string>, BadRequest<string>>> ApproveEnrollmentAsync(string userCode,
        string? name, BtdbNode node, HttpContext http)
    {
        if (name != null && MachineNames.Clean(name) == null)
            return TypedResults.BadRequest($"A machine name has 1 to {MachineNames.MaximumLength} characters.");
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        var result = await publisher.PublishAndWaitAsync(
            new EnrollmentApproved { UserCode = NormalizeCode(userCode), ApproverId = http.User.UserId() ?? 0, Name = name },
            http.RequestAborted);
        if (result.Status != EnrollStatus.Approved) return TypedResults.Conflict($"The enrollment is {result.Status}.");
        using var query = publisher.StartQuery();
        return TypedResults.Ok(ToDto(Ambient.Relation<IMachineTable>().FindByIdOrDefault(result.MachineId)!));
    }

    static async Task<MachineDto[]> ListMachinesAsync(BtdbNode node, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using var query = publisher.StartQuery();
        var userId = http.User.UserId() ?? 0;
        return Ambient.Relation<IMachineTable>().Select(machine => ToDto(machine) with { CanControl = MachineAccess.CanControl(userId, machine) })
            .OrderBy(machine => machine.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    static async Task<Results<NoContent, NotFound, ForbidHttpResult, BadRequest<string>>> RenameMachineAsync(ulong id,
        RenameMachineRequest request, BtdbNode node, HttpContext http)
    {
        if (MachineNames.Clean(request.Name) == null)
            return TypedResults.BadRequest($"A machine name has 1 to {MachineNames.MaximumLength} characters.");
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        return CommandResult(await publisher.PublishAndWaitAsync(new MachineRenamed
        {
            MachineId = id, UserId = http.User.UserId() ?? 0, Name = request.Name
        }, http.RequestAborted));
    }

    /// <summary>Removes the machine, by its owner or an admin: its daemon is refused from now on and its sessions stop.</summary>
    static async Task<Results<NoContent, NotFound, ForbidHttpResult, BadRequest<string>>> RemoveMachineAsync(ulong id, BtdbNode node,
        HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        return CommandResult(await publisher.PublishAndWaitAsync(new MachineRemoved { MachineId = id, UserId = http.User.UserId() ?? 0 },
            http.RequestAborted));
    }

    /// <summary>Measures the machine now; its owner only. Nothing is stored: the figures change all the time.</summary>
    static async Task<Results<Ok<MachineResources>, NotFound, ForbidHttpResult, BadRequest<string>>> MeasureMachineAsync(ulong id,
        BtdbNode node, MachineRpcClient rpc, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using (publisher.StartQuery())
        {
            if (Ambient.Relation<IMachineTable>().FindByIdOrDefault(id) is not { } machine) return TypedResults.NotFound();
            if (!MachineAccess.CanControl(http.User.UserId() ?? 0, machine)) return TypedResults.Forbid();
        }
        return await rpc.CallAsync(id, new MachineResourcesRequest(), http.RequestAborted) switch
        {
            MachineResourcesResult measured => TypedResults.Ok(Clean(measured.Resources)),
            RpcFailure failure => TypedResults.BadRequest(failure.Message),
            _ => TypedResults.BadRequest("The daemon gave an unexpected answer.")
        };
    }

    /// <summary>What a daemon says about itself, bounded before it is stored or shown.</summary>
    static MachineResources Clean(MachineResources resources) => resources with
    {
        Platform = resources.Platform.Length > 200 ? resources.Platform[..200] : resources.Platform,
        DiskPath = resources.DiskPath.Length > 500 ? resources.DiskPath[..500] : resources.DiskPath,
        Processors = Math.Max(0, resources.Processors), MemoryBytes = Math.Max(0, resources.MemoryBytes),
        MemoryAvailableBytes = Math.Max(0, resources.MemoryAvailableBytes), DiskBytes = Math.Max(0, resources.DiskBytes),
        DiskFreeBytes = Math.Max(0, resources.DiskFreeBytes)
    };

    internal static string ResourcesJson(MachineResources? resources) =>
        resources == null ? "" : JsonSerializer.Serialize(Clean(resources), ProtocolJsonContext.Default.MachineResources);

    static MachineResources? Resources(string json)
    {
        if (json.Length == 0) return null;
        try { return JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.MachineResources); }
        catch (JsonException) { return null; }
    }

    static async Task<HarnessReleaseDto[]> ListReleasesAsync(BtdbNode node, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using var query = publisher.StartQuery();
        return Ambient.Relation<IHarnessReleaseTable>().Select(release => new HarnessReleaseDto(release.Harness, release.Version,
                JsonSerializer.Deserialize(release.Artifacts, ProtocolJsonContext.Default.HarnessArtifactArray)!, release.PublishedAt))
            .OrderBy(release => release.Harness, StringComparer.Ordinal).ThenByDescending(release => release.PublishedAt).ToArray();
    }

    static async Task<Results<NoContent, NotFound, ForbidHttpResult, BadRequest<string>>> PublishReleaseAsync(
        PublishHarnessRelease request, BtdbNode node, HttpContext http)
    {
        if (request.Artifacts.Length == 0 || request.Artifacts.Any(artifact =>
                artifact.Sha256.Length != 64 || !artifact.Sha256.All(Uri.IsHexDigit) || !IsAllowedUrl(artifact.Url) ||
                string.IsNullOrWhiteSpace(artifact.EntryPoint)))
            return TypedResults.BadRequest("Every artifact needs an https URL, a SHA-256 hash and an entry point.");
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        return CommandResult(await publisher.PublishAndWaitAsync(new HarnessReleasePublished
        {
            UserId = http.User.UserId() ?? 0, Harness = request.Harness, Version = request.Version,
            Artifacts = JsonSerializer.Serialize(request.Artifacts, ProtocolJsonContext.Default.HarnessArtifactArray)
        }, http.RequestAborted));
    }

    /// <summary>https only; plain http is for a server on this very machine (development and tests).</summary>
    static bool IsAllowedUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                                            (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);

    static async Task<Results<NoContent, NotFound, ForbidHttpResult, BadRequest<string>>> InstallHarnessAsync(ulong id,
        InstallHarnessRequest request, BtdbNode node, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        return CommandResult(await publisher.PublishAndWaitAsync(new HarnessInstallRequested
        {
            UserId = http.User.UserId() ?? 0, MachineId = id, Harness = request.Harness, Version = request.Version
        }, http.RequestAborted));
    }

    static Results<NoContent, NotFound, ForbidHttpResult, BadRequest<string>> CommandResult(SessionCommandResult result) =>
        result.Outcome switch
        {
            CommandOutcome.Ok => TypedResults.NoContent(),
            CommandOutcome.NotFound => TypedResults.NotFound(),
            CommandOutcome.Forbidden => TypedResults.Forbid(),
            _ => TypedResults.BadRequest("That cannot be done to this machine.")
        };

    /// <summary>Must run inside a query scope. Online means some node holds the daemon's connection.</summary>
    static MachineDto ToDto(Machine machine)
    {
        var owner = Ambient.Relation<IUserTable>().FindByIdOrDefault(machine.OwnerId);
        var presence = Ambient.Relation<IMachinePresenceTable>().FindByIdOrDefault(machine.Id);
        return new(machine.Id, machine.Name, machine.Os, machine.Arch, machine.OwnerId, owner?.Name ?? "",
            presence?.DaemonVersion ?? machine.DaemonVersion, presence != null, presence?.ConnectedAt,
            Ambient.Relation<IMachineHarnessTable>().ListById(machine.Id)
                .Select(harness => new InstalledHarness(harness.Harness, harness.Version, harness.Error)).ToArray(),
            InstanceId: machine.InstanceId == Guid.Empty ? null : machine.InstanceId, Resources: Resources(machine.ResourcesJson));
    }

    /// <summary>The daemon's WebSocket: hello/welcome, then every unacknowledged inbox command (and new ones as they
    /// are issued), outbox batches with their acknowledgements, deltas, and keepalive.</summary>
    static async Task ConnectAsync(HttpContext http, DaemonPump pump, ServerOptions options, IHostApplicationLifetime lifetime)
    {
        if (!http.WebSockets.IsWebSocketRequest || options.RefuseDaemonWebSockets)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var machineId = http.User.MachineId()!.Value;
        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        // Kestrel does not abort upgraded connections on a graceful stop; without this the host waits its whole
        // shutdown timeout. The daemon reconnects (to another pod in a cluster) and resends what is unacknowledged.
        using var connectionScope = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, lifetime.ApplicationStopping);
        var cancellation = connectionScope.Token;
        var channel = new WebSocketChannel(socket);
        if (await channel.ReceiveAsync(cancellation) is not HelloMessage hello)
        {
            await channel.CloseAsync(WebSocketCloseStatus.ProtocolError, "Expected hello.", cancellation);
            return;
        }
        if (DaemonPump.RefusalOf(hello) is { } refusal)
        {
            await channel.CloseAsync(WebSocketCloseStatus.PolicyViolation, refusal, cancellation);
            return;
        }
        await pump.RunAsync(channel, Guid.NewGuid().ToString("N"), machineId, hello, cancellation);
    }

    /// <summary>Sends inbox commands in ID order: all of them after connecting, then each new one once. The daemon
    /// acknowledges what it applied; the rest is sent again on the next connection.</summary>
    internal static async Task SendCommandsAsync(DaemonConnections.Connection connection, EventPublisher publisher,
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
                        JsonSerializer.Deserialize(entry.Command, ProtocolJsonContext.Default.MachineCommand)!, entry.IssuedAt))
                    .ToList();
            foreach (var command in pending)
            {
                await connection.SendAsync(command, cancellation);
                sent = command.Id;
            }
            await connection.WaitForCommandsAsync(TimeSpan.FromSeconds(30), cancellation);
        }
    }

    internal static async Task<OutboxAckMessage> StoreReportsAsync(EventPublisher publisher, ulong machineId, OutboxMessage outbox)
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

    internal static bool SessionBelongsTo(EventPublisher publisher, ulong sessionId, ulong machineId)
    {
        using var query = publisher.StartQuery();
        return Ambient.Relation<ISessionTable>().FindByIdOrDefault(sessionId)?.MachineId == machineId;
    }
}
