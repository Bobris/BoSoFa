using System.Net.WebSockets;
using System.Text.Json;
using BoSoFa.Protocol;
using BoSoFa.Server.Agents;
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
    sealed class BothLeases(IDisposable first, IDisposable second) : IDisposable
    {
        public void Dispose()
        {
            first.Dispose();
            second.Dispose();
        }
    }

    const int PageSize = 500;
    /// <summary>Refuses images for a model the snapshot knows cannot read them; one it does not know may try.</summary>
    static string? ImagesRefused(ulong accountId, string? model)
    {
        if (model == null || Ambient.Relation<IHarnessAccountTable>().FindByIdOrDefault(accountId) is not { } account) return null;
        return ModelCatalog.Find(account.Kind, model.Trim()) is { Images: false } ? $"{model} cannot read images." : null;
    }

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
        api.MapPost("/sessions/{id}/compact", CompactAsync);
        api.MapPost("/sessions/{id}/clear", ClearAsync);
        api.MapPost("/sessions/{id}/side-questions", AskSideQuestionAsync);
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
            session.Harness == Harnesses.Agent && session.MachineId == 0 ? SessionAccess.CanSee(userId, session) : machine != null && MachineAccess.CanControl(userId, machine),
            session.AccountId == 0 ? null : session.AccountId,
            session.AccountId == 0 ? null : Ambient.Relation<IHarnessAccountTable>().FindByIdOrDefault(session.AccountId)?.Label,
            session.Branch, session.ProfileName, session.ReasoningEffort, session.ToolGroup.Length == 0 ? null : session.ToolGroup,
            session.ContextTokens == 0 ? null : session.ContextTokens, session.ContextLimit == 0 ? null : session.ContextLimit, session.CacheExpiresAt,
            session.AccountId == 0 ? null : AccountUsages.Of(session.AccountId));
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
        return Ambient.Relation<ISessionTable>().Where(session => SessionAccess.CanSee(userId, session))
            .OrderByDescending(session => session.UpdatedAt).Take(200).Select(session => ToDto(session, userId)).ToArray();
    }

    static async Task<Results<Ok<SessionDto>, NotFound>> GetSessionAsync(ulong id, BtdbNode node, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using var query = publisher.StartQuery();
        return Visible(id, http) is { } session ? TypedResults.Ok(ToDto(session, http.User.UserId() ?? 0)) : TypedResults.NotFound();
    }

    /// <summary>The session if this user may see it. Must run inside a query.</summary>
    static Session? Visible(ulong id, HttpContext http) =>
        Ambient.Relation<ISessionTable>().FindByIdOrDefault(id) is { } session && SessionAccess.CanSee(http.User.UserId() ?? 0, session) ? session : null;

    /// <summary>A page of transcript items after <paramref name="after"/>, oldest first.</summary>
    static async Task<Results<Ok<SessionItemDto[]>, NotFound>> ListItemsAsync(ulong id, long? after, BtdbNode node, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using var query = publisher.StartQuery();
        return Visible(id, http) != null ? TypedResults.Ok(Items(id, after ?? 0, PageSize)) : TypedResults.NotFound();
    }

    static async Task<Results<Ok<RuntimeRequestDto[]>, NotFound>> ListRequestsAsync(ulong id, BtdbNode node, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using var query = publisher.StartQuery();
        return Visible(id, http) != null ? TypedResults.Ok(PendingRequests(id)) : TypedResults.NotFound();
    }

    /// <summary>Checks the prompt's images from their content, not from what the client says they are.</summary>
    static async Task<(string Json, string? Problem)> ImagesJsonAsync(string[]? hashes, IBlobStore blobs, CancellationToken cancellation)
    {
        if (hashes is not { Length: > 0 }) return ("", null);
        if (hashes.Length > PromptImages.MaximumCount) return ("", $"A message can have at most {PromptImages.MaximumCount} images.");
        var images = new List<PromptImage>();
        foreach (var sha256 in hashes.Distinct())
        {
            if (!PromptImages.IsSha256(sha256)) return ("", "An image's hash is not a SHA-256.");
            await using var content = await blobs.OpenAsync(sha256, cancellation);
            if (content == null) return ("", "An image was not uploaded.");
            if (content.Length > PromptImages.MaximumBytes)
                return ("", $"An image can be at most {PromptImages.MaximumBytes / 1024 / 1024} MB.");
            var header = new byte[PromptImages.HeaderLength];
            var read = await content.ReadAtLeastAsync(header, header.Length, false, cancellation);
            if (PromptImages.MediaTypeOf(header.AsSpan(0, read)) is not { } mediaType)
                return ("", "Only PNG, JPEG, GIF and WebP images can be attached.");
            images.Add(new(sha256, mediaType, content.Length));
        }
        return (JsonSerializer.Serialize(images.ToArray(), ProtocolJsonContext.Default.PromptImageArray), null);
    }

    static async Task<Results<Ok<SessionDto>, NotFound, ForbidHttpResult, BadRequest<string>>> CreateSessionAsync(
        CreateSessionRequest request, BtdbNode node, IBlobStore blobs, HttpContext http)
    {
        var (imagesJson, problem) = await ImagesJsonAsync(request.Images, blobs, http.RequestAborted);
        if (problem != null) return TypedResults.BadRequest(problem);
        var isAgent = request.Harness == Harnesses.Agent;
        if (!isAgent && (request.SystemPrompt != null || request.ToolGroup != null))
            return TypedResults.BadRequest("Only the server agent takes a system prompt or a tool group.");
        if (!isAgent && request.ReasoningEffort != null && !Harnesses.IsEffortName(request.ReasoningEffort)) return TypedResults.BadRequest("Unknown reasoning effort.");
        if (isAgent && request.ReasoningEffort != null && !Harnesses.ReasoningEfforts.Contains(request.ReasoningEffort)) return TypedResults.BadRequest("Unknown reasoning effort.");
        if (isAgent && request.ToolGroup != null && !AgentToolGroups.All.Contains(request.ToolGroup)) return TypedResults.BadRequest("Unknown tool group.");
        if (isAgent && request.UseWorktree) return TypedResults.BadRequest("The server agent cannot use a worktree yet.");
        if (isAgent && request.MachineId != 0 && string.IsNullOrWhiteSpace(request.WorkingDirectory))
            return TypedResults.BadRequest("A session on a machine needs a working directory.");
        // The request's own choices win over the profile's defaults.
        string? model = request.Model, permissionMode = request.PermissionMode;
        if (request.ProfileName is { } name)
        {
            var publisher = await node.Ready.WaitAsync(http.RequestAborted);
            using var query = publisher.StartQuery();
            var profile = Ambient.Relation<IProfileTable>().FindByIdOrDefault(name);
            model ??= profile?.Model;
            permissionMode ??= profile?.PermissionMode;
            if (isAgent && request.MachineId == 0 && profile != null && JsonSerializer.Deserialize(profile.McpServers, ProtocolJsonContext.Default.StringArray)!
                    .Any(server => Ambient.Relation<IMcpServerTable>().FindByIdOrDefault(server)?.Transport != McpTransport.Http))
                return TypedResults.BadRequest("This profile has MCP servers that run on a machine, and this session has none.");
        }
        if (isAgent && string.IsNullOrWhiteSpace(model)) return TypedResults.BadRequest("The server agent needs a model.");
        if (isAgent && request.AccountId is null or 0) return TypedResults.BadRequest("The server agent needs a provider account.");
        if (isAgent && imagesJson.Length > 0)
        {
            var publisher = await node.Ready.WaitAsync(http.RequestAborted);
            using var query = publisher.StartQuery();
            if (ImagesRefused(request.AccountId!.Value, model) is { } refused) return TypedResults.BadRequest(refused);
        }
        return await RunAsync(node, http, new SessionCreated
        {
            MachineId = request.MachineId, UserId = http.User.UserId() ?? 0, Harness = request.Harness,
            WorkingDirectory = request.WorkingDirectory, Prompt = request.Prompt,
            PermissionMode = permissionMode ?? "default", Model = model, AccountId = request.AccountId ?? 0,
            UseWorktree = request.UseWorktree, ProfileName = request.ProfileName, ImagesJson = imagesJson,
            SystemPrompt = request.SystemPrompt, ReasoningEffort = request.ReasoningEffort, ToolGroup = request.ToolGroup,
            DraftId = request.DraftId ?? 0
        });
    }

    static async Task<Results<Ok<SessionDto>, NotFound, ForbidHttpResult, BadRequest<string>>> SendMessageAsync(ulong id,
        SendMessageRequest request, BtdbNode node, IBlobStore blobs, HttpContext http)
    {
        var (imagesJson, problem) = await ImagesJsonAsync(request.Images, blobs, http.RequestAborted);
        if (problem != null) return TypedResults.BadRequest(problem);
        if (imagesJson.Length > 0)
        {
            var publisher = await node.Ready.WaitAsync(http.RequestAborted);
            using var query = publisher.StartQuery();
            if (Ambient.Relation<ISessionTable>().FindByIdOrDefault(id) is { Harness: Harnesses.Agent } session && ImagesRefused(session.AccountId, session.Model) is { } refused)
                return TypedResults.BadRequest(refused);
        }
        return await RunAsync(node, http, new SessionMessageSent
        {
            SessionId = id, UserId = http.User.UserId() ?? 0, Text = request.Text, Mode = request.Mode ?? DispatchMode.Queue,
            ImagesJson = imagesJson
        });
    }

    static Task<Results<Ok<SessionDto>, NotFound, ForbidHttpResult, BadRequest<string>>> InterruptAsync(ulong id,
        BtdbNode node, HttpContext http) =>
        RunAsync(node, http, new SessionInterruptRequested { SessionId = id, UserId = http.User.UserId() ?? 0 });

    /// <summary>Summarizes the conversation so far to free the context (/compact), after the running turn if one runs.</summary>
    static Task<Results<Ok<SessionDto>, NotFound, ForbidHttpResult, BadRequest<string>>> CompactAsync(ulong id,
        BtdbNode node, HttpContext http) =>
        RunAsync(node, http, new SessionCompactRequested { SessionId = id, UserId = http.User.UserId() ?? 0 });

    /// <summary>Starts the conversation over (/clear): the harness forgets everything so far, while the transcript stays. Only between turns.</summary>
    static Task<Results<Ok<SessionDto>, NotFound, ForbidHttpResult, BadRequest<string>>> ClearAsync(ulong id,
        BtdbNode node, HttpContext http) =>
        RunAsync(node, http, new SessionClearRequested { SessionId = id, UserId = http.User.UserId() ?? 0 });

    /// <summary>Asks a question on the side (/btw), answered from the conversation so far beside any running turn; neither becomes
    /// part of the conversation. The answer arrives as a SideAnswer item whose ref is the question's sequence number.</summary>
    static Task<Results<Ok<SessionDto>, NotFound, ForbidHttpResult, BadRequest<string>>> AskSideQuestionAsync(ulong id,
        SideQuestionRequest request, BtdbNode node, HttpContext http) =>
        RunAsync(node, http, new SessionSideQuestionAsked { SessionId = id, UserId = http.User.UserId() ?? 0, Text = request.Text.Trim() });

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
        ServerOptions options, IHostApplicationLifetime lifetime)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        if (!IsAllowedOrigin(http, options))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden; // another site's page must not ride on this user's sign-in
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
                        Session? followed;
                        using (publisher.StartQuery())
                            followed = Visible(subscribe.SessionId, http);
                        if (followed == null)
                        {
                            subscriber.Enqueue(new LiveError(subscribe.SessionId, "Session not found."));
                            break;
                        }
                        if (!relays.ContainsKey(subscribe.SessionId))
                            relays[subscribe.SessionId] = followed.Harness != Harnesses.Agent
                                ? relay.Follow(followed.MachineId)
                                : followed.MachineId == 0
                                    ? relay.FollowSession(followed.Id)
                                    : new BothLeases(relay.FollowSession(followed.Id), relay.Follow(followed.MachineId)); // the answer from its node, a shell's output from the daemon's
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

    /// <summary>Tools send no Origin; a page's must be this server (directly or through a proxy that keeps Host) or a desktop app.</summary>
    static bool IsAllowedOrigin(HttpContext http, ServerOptions options)
    {
        var origin = http.Request.Headers.Origin.ToString();
        return origin.Length == 0 || options.DesktopOrigins.Contains(origin) ||
               (Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Authority == http.Request.Host.Value);
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
                    if (Ambient.Relation<ISessionTable>().FindByIdOrDefault(sessionId) is not { } session || !SessionAccess.CanSee(subscriber.UserId, session))
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
