using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BoSoFa.Protocol;
using BoSoFa.Server.Btdb;
using BoSoFa.Server.Domain;
using BTDB.ODBLayer;
using BTDB.Replication.EventSourcing;

namespace BoSoFa.Server.Agents;

/// <summary>What one pass of the loop reads.</summary>
/// <param name="MachineUsable">The creator may still use the session's machine.</param>
sealed record AgentView(Session Session, AgentRunner Runner, List<SessionItem> Items, List<RuntimeRequest> Requests, bool MachineUsable)
{
    /// <summary>The call's place in the transcript. Providers' ids are not enough: some models use <c>call_0</c> at every step.</summary>
    public long SeqOf(ToolCall call) => Items.Last(item => item.Kind == SessionItemKind.ToolUse && item.ToolUseId == call.Id).Seq;

    public RuntimeRequest? RequestOf(ToolCall call)
    {
        var key = AgentLoop.ApprovalKey(SeqOf(call));
        return Requests.FirstOrDefault(request => request.Key == key);
    }
}

/// <summary>Credentials and tools of one loop instance, which end with it.</summary>
/// <param name="SharedInstructions">The built-in prompt and the profile's.</param>
/// <param name="RuntimeContext">Date, machine and tools, as of now.</param>
/// <param name="Model">What is known about the model; null when the snapshot does not have it.</param>
/// <param name="MaxOutputTokens">The output limit to ask for; null for the API's own default.</param>
sealed record AgentEnvironment(IModelTurnExecutor Executor, AgentToolbox Tools, string SharedInstructions, string RuntimeContext, ModelInfo? Model, int? MaxOutputTokens);

/// <summary>The session cannot run at all; the person is told and the run ends.</summary>
sealed class AgentSetupException(string message) : Exception(message);

/// <summary>
/// The server's agent loop for one session (docs/server-agent.md §4). Each pass reads the transcript, does the one thing it says is
/// due, stores the outcome and reads again, so a node taking over needs nothing but the transcript. A write naming an older
/// <see cref="AgentRunner.Version"/> is refused by the domain, and the next read shows what really happened.
/// </summary>
sealed class AgentLoop(AgentLoopContext context, ulong sessionId)
{
    public static string ApprovalKey(long seq) => $"item-{seq}";

    /// <summary>The share of the model's input limit at which a conversation is summarized.</summary>
    const double CompactionThreshold = 0.9;

    const string CompactionInstruction = """
        The conversation is about to be replaced by your summary, so write one the work can go on from without anything else. Do not call tools.
        Cover: what the person wants and any constraints they gave; what has been done and what was found; files, commands and exact names that matter;
        decisions and why; what failed; and what is left to do next. Be specific, and leave out what no longer matters.
        """;

    const string CompactionPrefix = "This conversation was summarized to save space. The summary of everything before this point:\n\n";
    const int CompactedToolResult = 1000;

    /// <summary>The size of the latest request as the provider counted it; unknown after a restart or a summary.</summary>
    long? _lastContextTokens;

    const string UnknownOutcome = "The server restarted while this ran; whether it took effect is unknown.";
    const string InterruptedResult = "The person interrupted this before it finished.";
    static readonly TimeSpan PendingRecheck = TimeSpan.FromSeconds(30);

    TaskCompletionSource _pulse = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Dictionary<string, string> _blobs = [];
    EventPublisher Publisher => context.Publisher;

    Task NextPulse => Volatile.Read(ref _pulse).Task;

    /// <summary>The session changed; a waiting pass looks again.</summary>
    public void Pulse() => Interlocked.Exchange(ref _pulse, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    /// <summary>Runs until the session has no more work here, or <paramref name="stopping"/> ends it.</summary>
    public async Task RunAsync(CancellationToken stopping)
    {
        AgentEnvironment? environment = null;
        var firstPass = true;
        string? suspectCall = null;
        while (true)
        {
            stopping.ThrowIfCancellationRequested();
            var pulse = NextPulse; // taken before reading, so a change after the read still wakes the wait below
            if (Read() is not { } view) return;
            await ResolveReplaysAsync(view, stopping);
            var state = AgentConversation.Rebuild(view.Items);
            if (firstPass)
            {
                // Whatever is open when a loop starts belongs to a loop that ended: its first open call may have been running.
                firstPass = false;
                if (state.OpenCalls.Count > 0 && view.RequestOf(state.OpenCalls[0])?.Status != RuntimeRequestStatus.Pending)
                    suspectCall = state.OpenCalls[0].Id;
            }
            if (view.Runner.InterruptRequested)
            {
                await FinishAsync(view, state, "interrupted", false, null, true, stopping);
                continue;
            }
            if (state.OpenCalls.Count == 0 && !state.NeedsModel)
            {
                await FinishAsync(view, state, "completed", false, null, false, stopping);
                continue;
            }
            if (environment == null)
            {
                try
                {
                    (environment, var problems) = await context.OpenEnvironmentAsync(view.Session, stopping);
                    if (problems.Count > 0)
                    {
                        await RecordAsync(view, problems.Select(problem => new AgentItem(SessionItemKind.Notice, problem, IsError: true)), null, stopping);
                        continue;
                    }
                }
                catch (AgentSetupException problem)
                {
                    await FinishAsync(view, state, "failed", true, problem.Message, false, stopping);
                    continue;
                }
            }
            if (state.OpenCalls.Count > 0) await RunToolAsync(view, state.OpenCalls[0], environment, suspectCall, pulse, stopping);
            else if (InstructionsOf(view) is { } instructions)
            {
                if (IsNearTheLimit(state, environment)) await CompactAsync(view, state, environment, instructions, false, stopping);
                else await CallModelAsync(view, state, environment, instructions, stopping);
            }
            else await RecordInstructionsAsync(view, environment, stopping);
        }
    }

    AgentView? Read()
    {
        using var query = Publisher.StartQuery();
        if (Ambient.Relation<IAgentRunnerTable>().FindByIdOrDefault(sessionId) is not { } runner || runner.NodeId != context.NodeId ||
            Ambient.Relation<ISessionTable>().FindByIdOrDefault(sessionId) is not { } session)
            return null;
        var items = Ambient.Relation<ISessionItemTable>().ListById(sessionId, new AdvancedEnumeratorParam<long>(EnumerationOrder.Ascending, 0,
            KeyProposition.Excluded, 0, KeyProposition.Ignored)).ToList();
        var usable = session.MachineId != 0 && Ambient.Relation<IMachineTable>().FindByIdOrDefault(session.MachineId) is { } machine &&
                     MachineAccess.CanControl(session.CreatedById, machine);
        return new(session, runner, items, Ambient.Relation<IRuntimeRequestTable>().ListById(sessionId).ToList(), usable);
    }

    /// <summary>Puts the stored form of large items back in place.</summary>
    async Task ResolveReplaysAsync(AgentView view, CancellationToken cancellation)
    {
        foreach (var item in view.Items)
        {
            if (item.Replay is not { } replay || !replay.StartsWith(BlobPrefix, StringComparison.Ordinal)) continue;
            var sha256 = replay[BlobPrefix.Length..];
            if (!_blobs.TryGetValue(sha256, out var content))
            {
                await using var stream = await context.Blobs.OpenAsync(sha256, cancellation) ??
                                         throw new InvalidOperationException($"The blob {sha256} of session {sessionId} is missing.");
                using var reader = new StreamReader(stream, Encoding.UTF8);
                _blobs[sha256] = content = await reader.ReadToEndAsync(cancellation);
            }
            item.Replay = content;
        }
    }

    const string BlobPrefix = "blob:";

    Task<string> StoredFormAsync(ConversationItem item, CancellationToken cancellation) => StoredFormAsync(AgentConversation.Serialize(item), cancellation);

    async Task<string> StoredFormAsync(string json, CancellationToken cancellation)
    {
        if (json.Length <= ItemReport.InlineLimit) return json;
        using var content = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var (file, sha256, _) = await BlobHash.SpoolAsync(content, long.MaxValue, cancellation);
        await context.Blobs.PutAsync(sha256, file, cancellation);
        _blobs[sha256] = json;
        return BlobPrefix + sha256;
    }

    Task RecordAsync(AgentView view, IEnumerable<AgentItem> items, TurnUsage? usage, CancellationToken cancellation, double costUsd = 0) =>
        Publisher.PublishAndWaitAsync(new AgentItemsRecorded
        {
            CostUsd = costUsd,
            SessionId = sessionId, NodeId = context.NodeId, Version = view.Runner.Version,
            ItemsJson = JsonSerializer.Serialize(items.ToArray(), ProtocolJsonContext.Default.AgentItemArray),
            InputTokens = usage?.InputTokens ?? 0, CachedInputTokens = usage?.CachedInputTokens ?? 0,
            CacheWriteTokens = usage?.CacheWriteTokens ?? 0, OutputTokens = usage?.OutputTokens ?? 0,
            ReasoningTokens = usage?.ReasoningTokens ?? 0
        }, cancellation);

    async Task FinishAsync(AgentView view, AgentConversationState state, string outcome, bool isError, string? error, bool interrupted,
        CancellationToken cancellation)
    {
        var items = state.OpenCalls.Select(call => Result(call, InterruptedResult, true)).ToList();
        if (await RunDiffAsync(view, cancellation) is { } diff) items.Add(diff);
        await Publisher.PublishAndWaitAsync(new AgentRunFinished
        {
            SessionId = sessionId, NodeId = context.NodeId, Version = view.Runner.Version, SeenSeq = view.Session.LastSeq, Outcome = outcome,
            IsError = isError, Error = error, Interrupted = interrupted, HasQueued = state.HasQueued,
            ItemsJson = JsonSerializer.Serialize(items.ToArray(), ProtocolJsonContext.Default.AgentItemArray)
        }, cancellation);
    }

    static int RunNumberOf(AgentView view) => view.Items.Count(item => item.Kind == SessionItemKind.RunEnded) + 1;

    /// <summary>What this run changed on the machine, if it ran anything that could change files. A machine that cannot say costs only the diff.</summary>
    async Task<AgentItem?> RunDiffAsync(AgentView view, CancellationToken cancellation)
    {
        if (context.MachineOf(view.Session) is not { } machine) return null;
        var changing = false;
        for (var i = view.Items.Count - 1; i >= 0 && view.Items[i].Kind != SessionItemKind.RunEnded; i--)
            changing |= view.Items[i] is { Kind: SessionItemKind.ToolUse, ToolName: { } name } && MachineTools.Changes(name);
        return changing ? await machine.DiffAsync(RunNumberOf(view), cancellation) : null;
    }

    static AgentItem Result(ToolCall call, string text, bool isError, string? itemRef = null) =>
        new(SessionItemKind.ToolResult, text, call.Id, call.Name, IsError: isError, Ref: itemRef);

    /// <summary>Runs <paramref name="work"/> and cancels it when the person interrupts or this node no longer holds the session.</summary>
    async Task<T> WatchedAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken stopping)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var running = work(cancel.Token);
        while (!running.IsCompleted)
        {
            var pulse = NextPulse;
            if (IsCalledOff()) await cancel.CancelAsync();
            await Task.WhenAny(running, pulse);
        }
        return await running;
    }

    bool IsCalledOff()
    {
        using var query = Publisher.StartQuery();
        return Ambient.Relation<IAgentRunnerTable>().FindByIdOrDefault(sessionId) is not { } runner ||
               runner.NodeId != context.NodeId || runner.InterruptRequested;
    }

    static AgentInstructionsRecord? InstructionsOf(AgentView view) =>
        view.Items.LastOrDefault(item => item.Kind == SessionItemKind.Instructions)?.Replay is { } replay
            ? JsonSerializer.Deserialize(replay, ServerJsonContext.Default.AgentInstructionsRecord)
            : null;

    /// <summary>Works out what the model is told, once: the date and the project's files must not change under a running conversation.</summary>
    async Task RecordInstructionsAsync(AgentView view, AgentEnvironment environment, CancellationToken stopping)
    {
        var items = new List<AgentItem>();
        (string? File, string? Text, string? Problem) project = default;
        if (view.MachineUsable && context.MachineOf(view.Session) is { } machine) project = await machine.ProjectInstructionsAsync(stopping);
        if (project.Problem != null) items.Add(new(SessionItemKind.Notice, $"The project's instructions could not be read: {project.Problem}", IsError: true));
        var record = new AgentInstructionsRecord(environment.SharedInstructions,
            AgentInstructions.Session(environment.RuntimeContext, project.File, project.Text, view.Session.SystemPrompt));
        items.Insert(0, new(SessionItemKind.Instructions, record.Shared + "\n\n" + record.Session,
            Replay: await StoredFormAsync(JsonSerializer.Serialize(record, ServerJsonContext.Default.AgentInstructionsRecord), stopping)));
        await RecordAsync(view, items, null, stopping);
    }

    bool IsNearTheLimit(AgentConversationState state, AgentEnvironment environment)
    {
        if (state.JustCompacted || environment.Model?.InputLimit is not { } limit) return false;
        return (_lastContextTokens ?? EstimateTokens(state.Conversation)) >= limit * CompactionThreshold;
    }

    /// <summary>After a restart nothing says how large the last request was, so roughly four characters to a token will do.</summary>
    static long EstimateTokens(IReadOnlyList<ConversationItem> conversation) => conversation.Sum(item => (long)(item switch
    {
        UserMessage user => user.Text.Length,
        AssistantMessage assistant => assistant.Text.Length,
        ToolCall call => call.Arguments.Length,
        ToolResult result => result.Text.Length,
        ReasoningItem reasoning => reasoning.ProviderData?.Length ?? reasoning.Text.Length,
        _ => 0
    })) / 4;

    /// <summary>Replaces the conversation so far with the model's summary. A conversation too large for even that is summarized with its tool results cut short.</summary>
    async Task CompactAsync(AgentView view, AgentConversationState state, AgentEnvironment environment, AgentInstructionsRecord instructions, bool shortened, CancellationToken stopping)
    {
        IReadOnlyList<ConversationItem> history = shortened ? Shortened(state.Conversation) : state.Conversation;
        var request = new TurnRequest(view.Session.Model!, instructions.Shared, [..history, new UserMessage(CompactionInstruction)],
            environment.Tools.SpecsFor(view.Session.PermissionMode), sessionId.ToString(), instructions.Session, view.Session.ReasoningEffort, environment.MaxOutputTokens, NoToolUse: true);
        TurnResponse response;
        try
        {
            response = await WatchedAsync(cancellation => environment.Executor.ExecuteAsync(request, _ => ValueTask.CompletedTask, cancellation), stopping);
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested) { return; } // the next pass sees why
        catch (ModelProviderException problem) when (problem.Failure == ProviderFailure.ContextOverflow)
        {
            if (!shortened) await CompactAsync(view, state, environment, instructions, true, stopping);
            else await FinishAsync(view, state, "failed", true, "This conversation has grown too long to summarize. Start a new session.", false, stopping);
            return;
        }
        catch (ModelProviderException problem)
        {
            await FinishAsync(view, state, "failed", true, problem.Message, false, stopping);
            return;
        }
        var summary = string.Concat(response.Output.OfType<AssistantMessage>().Select(message => message.Text)).Trim();
        if (summary.Length == 0)
        {
            await FinishAsync(view, state, "failed", true, "The model did not summarize the conversation.", false, stopping);
            return;
        }
        _lastContextTokens = null;
        await RecordAsync(view, [new(SessionItemKind.Compaction, summary, Replay: await StoredFormAsync(new UserMessage(CompactionPrefix + summary), stopping))],
            response.Usage, stopping, environment.Model?.CostOf(response.Usage) ?? 0);
    }

    static List<ConversationItem> Shortened(IReadOnlyList<ConversationItem> conversation) => conversation.Where(item => item is not ReasoningItem)
        .Select(item => item is ToolResult { Text.Length: > CompactedToolResult } result ? result with { Text = result.Text[..CompactedToolResult] + " [cut]" } : item).ToList();

    async Task CallModelAsync(AgentView view, AgentConversationState state, AgentEnvironment environment, AgentInstructionsRecord instructions, CancellationToken stopping)
    {
        var itemRef = $"agent-{view.Session.LastSeq}";
        var request = new TurnRequest(view.Session.Model!, instructions.Shared, state.Conversation,
            environment.Tools.SpecsFor(view.Session.PermissionMode), sessionId.ToString(), instructions.Session, view.Session.ReasoningEffort, environment.MaxOutputTokens);
        TurnResponse response;
        try
        {
            response = await WatchedAsync(cancellation => environment.Executor.ExecuteAsync(request, delta => StreamAsync(itemRef, delta), cancellation), stopping);
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested) { return; } // the next pass sees why
        catch (ModelProviderException problem) when (problem.Failure == ProviderFailure.ContextOverflow && !state.JustCompacted)
        {
            await CompactAsync(view, state, environment, instructions, false, stopping);
            return;
        }
        catch (ModelProviderException problem)
        {
            await FinishAsync(view, state, "failed", true, problem.Message, false, stopping);
            return;
        }
        catch (Exception problem) when (problem is not OperationCanceledException)
        {
            context.Logger.LogError(problem, "The model's answer for session {SessionId} could not be used", sessionId);
            await FinishAsync(view, state, "failed", true, "The model's answer could not be read.", false, stopping);
            return;
        }

        var items = new List<AgentItem>();
        var refUsed = false;
        foreach (var output in response.Output)
            switch (output)
            {
                case AssistantMessage message:
                    items.Add(new(SessionItemKind.Assistant, message.Text, Ref: refUsed ? null : itemRef, Replay: await StoredFormAsync(message, stopping)));
                    refUsed = true;
                    break;
                case ReasoningItem reasoning:
                    items.Add(new(SessionItemKind.Thinking, reasoning.Text, Replay: await StoredFormAsync(reasoning, stopping)));
                    break;
                case ToolCall call when response.Stop == StopReason.ToolCalls:
                    items.Add(new(SessionItemKind.ToolUse, Summary(call.Arguments), call.Id, call.Name, call.Arguments, Replay: await StoredFormAsync(call, stopping)));
                    break;
            }
        var stopNote = response.Stop switch
        {
            StopReason.MaxTokens => "The answer was cut off at the model's output limit.",
            StopReason.Refused => "The provider refused to answer" + (response.StopDetail is { Length: > 0 } detail ? $": {detail}" : "."),
            _ => null
        };
        if (!items.Any(item => item.Kind is SessionItemKind.Assistant or SessionItemKind.ToolUse))
        {
            await FinishAsync(view, state, "failed", true, stopNote ?? "The model gave no answer.", false, stopping);
            return;
        }
        if (stopNote != null) items.Add(new(SessionItemKind.Notice, stopNote, IsError: true));
        _lastContextTokens = response.Usage.InputTokens + response.Usage.OutputTokens;
        await RecordAsync(view, items, response.Usage, stopping, environment.Model?.CostOf(response.Usage) ?? 0);
    }

    ValueTask StreamAsync(string itemRef, TurnDelta delta)
    {
        // Only the answer's text streams for now; reasoning and tool arguments arrive with the finished step.
        if (delta.Kind == TurnDeltaKind.Text) context.PublishDelta(new(sessionId, itemRef, delta.Text));
        else if (delta.Kind == TurnDeltaKind.Restart) context.PublishDelta(new(sessionId, itemRef, "", true));
        return ValueTask.CompletedTask;
    }

    static string Summary(string arguments) => arguments.Length <= 120 ? arguments : arguments[..119] + "…";

    async Task RunToolAsync(AgentView view, ToolCall call, AgentEnvironment environment, string? suspectCall, Task pulse, CancellationToken stopping)
    {
        var mode = view.Session.PermissionMode;
        var kind = environment.Tools.KindOf(call.Name, mode);
        if (kind == AgentToolKind.Unknown)
        {
            await RecordAsync(view, [Result(call, $"There is no tool named {call.Name}.", true)], null, stopping);
            return;
        }
        if (environment.Tools.RunsOnMachine(call.Name) && !view.MachineUsable)
        {
            await RecordAsync(view, [Result(call, "The person who started this session can no longer use its machine.", true)], null, stopping);
            return;
        }
        JsonObject arguments;
        try { arguments = JsonNode.Parse(call.Arguments.Length == 0 ? "{}" : call.Arguments) as JsonObject ?? throw new JsonException("Not an object."); }
        catch (JsonException problem)
        {
            await RecordAsync(view, [Result(call, $"The arguments are not a valid JSON object: {problem.Message}", true)], null, stopping);
            return;
        }

        var request = view.RequestOf(call);
        var needsApproval = AgentToolbox.NeedsApproval(kind, mode);
        if (needsApproval)
        {
            if (request == null)
            {
                await Publisher.PublishAndWaitAsync(new AgentApprovalRaised
                {
                    SessionId = sessionId, NodeId = context.NodeId, Version = view.Runner.Version, Key = ApprovalKey(view.SeqOf(call)), ToolUseId = call.Id, ToolName = call.Name,
                    Input = call.Arguments, Description = kind switch
                    {
                        AgentToolKind.Ask => Str(arguments["question"]),
                        AgentToolKind.MachineShell => Str(arguments["command"]),
                        AgentToolKind.MachineWrite => Str(arguments["path"]),
                        _ => null
                    }
                }, stopping);
                return;
            }
            switch (request.Status)
            {
                case RuntimeRequestStatus.Pending:
                    await pulse.WaitAsync(PendingRecheck, stopping).ContinueWith(_ => { }, TaskContinuationOptions.ExecuteSynchronously);
                    stopping.ThrowIfCancellationRequested();
                    return;
                case RuntimeRequestStatus.Cancelled:
                    await RecordAsync(view, [Result(call, "Nobody answered before the turn ended.", true)], null, stopping);
                    return;
                case RuntimeRequestStatus.Denied when kind != AgentToolKind.Ask:
                    await RecordAsync(view, [Result(call, "The person did not allow this." + Added(request.Answer), true)], null, stopping);
                    return;
            }
            if (kind == AgentToolKind.Ask)
            {
                var yes = request.Status == RuntimeRequestStatus.Allowed;
                await RecordAsync(view, [Result(call, (yes ? "The person answered yes." : "The person answered no.") + Added(request.Answer), false)], null, stopping);
                return;
            }
        }
        // A previous loop may have started this call: running it again could repeat its effect. Reading has none.
        if (call.Id == suspectCall && kind is not (AgentToolKind.MachineRead or AgentToolKind.Web))
        {
            await RecordAsync(view, [Result(call, UnknownOutcome, true)], null, stopping);
            return;
        }

        if (kind == AgentToolKind.Notify)
        {
            var message = Str(arguments["message"]) ?? "";
            await RecordAsync(view, [new AgentItem(SessionItemKind.Notice, message), Result(call, "The person was told.", false)], null, stopping);
            return;
        }

        var seq = view.SeqOf(call);
        var toolCall = new AgentToolCall($"{sessionId}:{seq}", $"tool-{seq}", RunNumberOf(view));
        McpToolResult result;
        try
        {
            result = await WatchedAsync(cancellation => environment.Tools.CallAsync(call.Name, arguments, toolCall, cancellation), stopping);
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested) { return; } // the next pass reports it as interrupted
        catch (Exception problem) when (problem is McpException or HttpRequestException or IOException or JsonException)
        {
            result = new(problem.Message, true);
        }
        await RecordAsync(view, [Result(call, result.Text, result.IsError, toolCall.LiveRef)], null, stopping);
    }

    static string Added(string? answer) => string.IsNullOrWhiteSpace(answer) ? "" : $" They added: {answer}";

    static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
