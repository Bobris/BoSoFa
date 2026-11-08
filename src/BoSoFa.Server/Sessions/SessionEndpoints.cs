using System.Net.WebSockets;
using System.Text.Json;
using BoSoFa.Protocol;
using BoSoFa.Server.Btdb;
using BoSoFa.Server.Domain;
using BoSoFa.Server.Live;
using BoSoFa.Server.Machines;
using BoSoFa.Server.Tokens;
using BTDB.ODBLayer;
using BTDB.Replication.EventSourcing;
using Microsoft.AspNetCore.Http.HttpResults;

namespace BoSoFa.Server.Sessions;

public static class SessionEndpoints
{
    const int PageSize = 500;

    public static void MapSessionEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization();
        api.MapGet("/sessions", ListSessionsAsync);
        api.MapPost("/sessions", CreateSessionAsync);
        api.MapGet("/sessions/{id}", GetSessionAsync);
        api.MapGet("/sessions/{id}/items", ListItemsAsync);
        api.MapGet("/sessions/{id}/requests", ListRequestsAsync);
        api.MapPost("/sessions/{id}/messages", SendMessageAsync);
        api.MapPost("/sessions/{id}/interrupt", InterruptAsync);
        api.MapPost("/sessions/{id}/stop", StopAsync);
        api.MapPost("/sessions/{id}/requests/{requestId}/answer", AnswerAsync);
        api.Map(LiveSocket.Path["/api/v1".Length..], LiveAsync);
    }

    /// <summary>Must run inside a query scope.</summary>
    public static SessionDto ToDto(Session session, ulong userId)
    {
        var machine = Ambient.Relation<IMachineTable>().FindByIdOrDefault(session.MachineId);
        var creator = Ambient.Relation<IUserTable>().FindByIdOrDefault(session.CreatedById);
        return new(session.Id, session.MachineId, machine?.Name ?? "", session.Harness, session.WorkingDirectory,
            session.Title, session.Status, session.CreatedById, creator?.Name ?? "", session.CreatedAt, session.UpdatedAt,
            session.LastSeq, session.Model, session.PermissionMode, session.CostUsd,
            machine != null && machine.OwnerId == userId, session.AccountId == 0 ? null : session.AccountId,
            session.AccountId == 0 ? null : Ambient.Relation<IHarnessAccountTable>().FindByIdOrDefault(session.AccountId)?.Label,
            session.Branch, session.ProfileName);
    }

    static RuntimeRequestDto ToDto(RuntimeRequest request) => new(request.Id, request.SessionId, request.ToolName,
        request.ToolUseId, request.Input, request.Description, request.Status, request.RaisedAt,
        request.AnsweredById == 0 ? null : Ambient.Relation<IUserTable>().FindByIdOrDefault(request.AnsweredById)?.Name,
        request.AnsweredAt);

    static SessionItemDto[] Items(ulong sessionId, long afterSeq, int count) => Ambient.Relation<ISessionItemTable>()
        .ListById(sessionId, new AdvancedEnumeratorParam<long>(EnumerationOrder.Ascending, afterSeq,
            KeyProposition.Excluded, 0, KeyProposition.Ignored))
        .Take(count).Select(item => item.ToDto()).ToArray();

    static RuntimeRequestDto[] PendingRequests(ulong sessionId) => Ambient.Relation<IRuntimeRequestTable>()
        .ListById(sessionId).Where(request => request.Status == RuntimeRequestStatus.Pending).Select(ToDto).ToArray();

    static async Task<SessionDto[]> ListSessionsAsync(BtdbNode node, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using var query = publisher.StartQuery();
        var userId = http.User.UserId() ?? 0;
        return Ambient.Relation<ISessionTable>().OrderByDescending(session => session.UpdatedAt).Take(200)
            .Select(session => ToDto(session, userId)).ToArray();
    }

    static async Task<Results<Ok<SessionDto>, NotFound>> GetSessionAsync(ulong id, BtdbNode node, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using var query = publisher.StartQuery();
        return Ambient.Relation<ISessionTable>().FindByIdOrDefault(id) is { } session
            ? TypedResults.Ok(ToDto(session, http.User.UserId() ?? 0))
            : TypedResults.NotFound();
    }

    /// <summary>A page of transcript items after <paramref name="after"/>, oldest first.</summary>
    static async Task<SessionItemDto[]> ListItemsAsync(ulong id, long? after, BtdbNode node, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using var query = publisher.StartQuery();
        return Items(id, after ?? 0, PageSize);
    }

    static async Task<RuntimeRequestDto[]> ListRequestsAsync(ulong id, BtdbNode node, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using var query = publisher.StartQuery();
        return PendingRequests(id);
    }

    static async Task<Results<Ok<SessionDto>, NotFound, ForbidHttpResult, BadRequest<string>>> CreateSessionAsync(
        CreateSessionRequest request, BtdbNode node, HttpContext http)
    {
        // The request's own choices win over the profile's defaults.
        string? model = request.Model, permissionMode = request.PermissionMode;
        if (request.ProfileName is { } name)
        {
            var publisher = await node.Ready.WaitAsync(http.RequestAborted);
            using var query = publisher.StartQuery();
            var profile = Ambient.Relation<IProfileTable>().FindByIdOrDefault(name);
            model ??= profile?.Model;
            permissionMode ??= profile?.PermissionMode;
        }
        return await RunAsync(node, http, new SessionCreated
        {
            MachineId = request.MachineId, UserId = http.User.UserId() ?? 0, Harness = request.Harness,
            WorkingDirectory = request.WorkingDirectory, Prompt = request.Prompt,
            PermissionMode = permissionMode ?? "default", Model = model, AccountId = request.AccountId ?? 0,
            UseWorktree = request.UseWorktree, ProfileName = request.ProfileName
        });
    }

    static Task<Results<Ok<SessionDto>, NotFound, ForbidHttpResult, BadRequest<string>>> SendMessageAsync(ulong id,
        SendMessageRequest request, BtdbNode node, HttpContext http) =>
        RunAsync(node, http, new SessionMessageSent
        {
            SessionId = id, UserId = http.User.UserId() ?? 0, Text = request.Text, Mode = request.Mode ?? DispatchMode.Queue
        });

    static Task<Results<Ok<SessionDto>, NotFound, ForbidHttpResult, BadRequest<string>>> InterruptAsync(ulong id,
        BtdbNode node, HttpContext http) =>
        RunAsync(node, http, new SessionInterruptRequested { SessionId = id, UserId = http.User.UserId() ?? 0 });

    static Task<Results<Ok<SessionDto>, NotFound, ForbidHttpResult, BadRequest<string>>> StopAsync(ulong id,
        BtdbNode node, HttpContext http) =>
        RunAsync(node, http, new SessionStopRequested { SessionId = id, UserId = http.User.UserId() ?? 0 });

    static Task<Results<Ok<SessionDto>, NotFound, ForbidHttpResult, BadRequest<string>>> AnswerAsync(ulong id,
        ulong requestId, AnswerRuntimeRequest answer, BtdbNode node, HttpContext http) =>
        RunAsync(node, http, new RuntimeRequestAnswered
        {
            SessionId = id, RequestId = requestId, UserId = http.User.UserId() ?? 0, Allow = answer.Allow,
            Message = answer.Message
        });

    static async Task<Results<Ok<SessionDto>, NotFound, ForbidHttpResult, BadRequest<string>>> RunAsync(BtdbNode node,
        HttpContext http, EventWithResult<SessionCommandResult> command)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        var result = await publisher.PublishAndWaitAsync(command, http.RequestAborted);
        switch (result.Outcome)
        {
            case CommandOutcome.NotFound: return TypedResults.NotFound();
            case CommandOutcome.Forbidden: return TypedResults.Forbid();
            case CommandOutcome.Invalid: return TypedResults.BadRequest("The request is not valid for this session.");
        }
        using var query = publisher.StartQuery();
        return TypedResults.Ok(ToDto(Ambient.Relation<ISessionTable>().FindByIdOrDefault(result.SessionId)!,
            http.User.UserId() ?? 0));
    }

    /// <summary>The UI's live connection: subscribe to sessions, receive their missing items and pending requests
    /// whenever they change, plus ephemeral deltas.</summary>
    static async Task LiveAsync(HttpContext http, BtdbNode node, LiveHub hub, LiveRelay relay, MachineRpcClient rpc,
        IHostApplicationLifetime lifetime)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        var subscriber = hub.Join(http.User.UserId() ?? 0);
        // Upgraded connections end with the host (see MachineEndpoints.ConnectAsync); clients reconnect with a cursor.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, lifetime.ApplicationStopping);
        var sending = SendLiveAsync(socket, publisher, subscriber, stop.Token);
        // Deltas of a session reach this node only while it follows the session's machine (LiveRelay).
        var relays = new Dictionary<ulong, IDisposable>();
        var terminalRelays = new Dictionary<string, IDisposable>();
        try
        {
            while (await ReceiveAsync(socket, stop.Token) is { } message)
                switch (message)
                {
                    case LiveSubscribe subscribe:
                        if (!relays.ContainsKey(subscribe.SessionId))
                        {
                            ulong? machineId;
                            using (publisher.StartQuery())
                                machineId = Ambient.Relation<ISessionTable>().FindByIdOrDefault(subscribe.SessionId)?.MachineId;
                            if (machineId is { } machine) relays[subscribe.SessionId] = relay.Follow(machine);
                        }
                        subscriber.Subscribe(subscribe.SessionId, subscribe.AfterSeq);
                        break;
                    case LiveUnsubscribe unsubscribe:
                        subscriber.Unsubscribe(unsubscribe.SessionId);
                        if (relays.Remove(unsubscribe.SessionId, out var lease)) lease.Dispose();
                        break;
                    case LiveTerminalSubscribe watch:
                        bool allowed;
                        using (publisher.StartQuery())
                            allowed = Ambient.Relation<IMachineTable>().FindByIdOrDefault(watch.MachineId) is { } machineToWatch &&
                                      MachineAccess.CanControl(subscriber.UserId, machineToWatch);
                        if (!allowed || http.User.IsReadOnly())
                        {
                            subscriber.Enqueue(new LiveTerminalExited(watch.TerminalId, null, "You cannot reach this machine."));
                            break;
                        }
                        // Watching first: output produced during the attach is not lost, the client drops what it has twice.
                        subscriber.WatchTerminal(watch.MachineId, watch.TerminalId);
                        if (!terminalRelays.ContainsKey(watch.TerminalId)) terminalRelays[watch.TerminalId] = relay.Follow(watch.MachineId);
                        subscriber.Enqueue(await rpc.CallAsync(watch.MachineId, new TerminalAttachRequest(watch.TerminalId), stop.Token) switch
                        {
                            TerminalAttachResult attached => new LiveTerminalSnapshot(watch.TerminalId, attached.EndOffset, attached.Data,
                                attached.Exited, attached.ExitCode),
                            RpcFailure failure => new LiveTerminalExited(watch.TerminalId, null, failure.Message),
                            _ => new LiveTerminalExited(watch.TerminalId, null, "The daemon gave an unexpected answer.")
                        });
                        break;
                    case LiveTerminalUnsubscribe unwatch:
                        subscriber.UnwatchTerminal(unwatch.TerminalId);
                        if (terminalRelays.Remove(unwatch.TerminalId, out var terminalLease)) terminalLease.Dispose();
                        break;
                    case LiveTerminalInput input:
                        if (subscriber.MachineOfTerminal(input.TerminalId) is { } inputMachine)
                            await rpc.CallAsync(inputMachine, new TerminalInputRequest(input.TerminalId, input.Data), stop.Token);
                        break;
                    case LiveTerminalResize resize:
                        if (subscriber.MachineOfTerminal(resize.TerminalId) is { } resizeMachine)
                            await rpc.CallAsync(resizeMachine, new TerminalResizeRequest(resize.TerminalId, resize.Cols, resize.Rows), stop.Token);
                        break;
                }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or JsonException) { }
        finally
        {
            foreach (var lease in relays.Values.Concat(terminalRelays.Values)) lease.Dispose();
            hub.Leave(subscriber);
            await stop.CancelAsync();
            try { await sending; }
            catch (Exception) { /* the connection is gone */ }
        }
    }

    static async Task SendLiveAsync(WebSocket socket, EventPublisher publisher, LiveHub.Subscriber subscriber,
        CancellationToken cancellation)
    {
        while (true)
        {
            await subscriber.WaitAsync(cancellation);
            foreach (var (sessionId, afterSeq) in subscriber.TakeDirty().ToList())
            {
                LiveServerMessage update;
                long lastSeq;
                bool more;
                using (publisher.StartQuery())
                {
                    if (Ambient.Relation<ISessionTable>().FindByIdOrDefault(sessionId) is not { } session)
                    {
                        await SendAsync(socket, new LiveError(sessionId, "Session not found."), cancellation);
                        subscriber.Unsubscribe(sessionId);
                        continue;
                    }
                    var items = Items(sessionId, afterSeq, PageSize);
                    lastSeq = items.Length > 0 ? items[^1].Seq : afterSeq;
                    more = lastSeq < session.LastSeq;
                    update = new LiveSessionUpdate(ToDto(session, subscriber.UserId), items, PendingRequests(sessionId));
                }
                await SendAsync(socket, update, cancellation);
                subscriber.Advance(sessionId, lastSeq, more);
            }
            while (subscriber.TryTakeDelta(out var delta)) await SendAsync(socket, delta, cancellation);
            while (subscriber.TryTakeTerminalMessage(out var terminal)) await SendAsync(socket, terminal, cancellation);
        }
    }

    static Task SendAsync(WebSocket socket, LiveServerMessage message, CancellationToken cancellation) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, ProtocolJsonContext.Default.LiveServerMessage),
            WebSocketMessageType.Text, true, cancellation);

    static async Task<LiveClientMessage?> ReceiveAsync(WebSocket socket, CancellationToken cancellation)
    {
        var buffer = new byte[64 * 1024];
        var length = 0;
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(length), cancellation);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            length += result.Count;
            if (result.EndOfMessage)
                return JsonSerializer.Deserialize(buffer.AsSpan(0, length), ProtocolJsonContext.Default.LiveClientMessage);
            if (length == buffer.Length) throw new WebSocketException(WebSocketError.Faulted, "Message too large.");
        }
    }
}
