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
/// <param name="PromptCacheSeconds">How long the provider says its prompt cache lasts; zero when it does not.</param>
sealed record AgentEnvironment(IModelTurnExecutor Executor, AgentToolbox Tools, string SharedInstructions, string RuntimeContext, ModelInfo? Model, int? MaxOutputTokens,
    int PromptCacheSeconds);

/// <summary>The session cannot run at all; the person is told and the run ends.</summary>
sealed class AgentSetupException(string message) : Exception(message);

/// <summary>
/// The server's agent loop for one session (docs/server-agent.md §4). Each pass reads the transcript, does the one thing it says is
/// due, stores the outcome and reads again, so a later attempt, on any node, needs nothing but the transcript. A write naming an older
/// <see cref="AgentRunner.Version"/> or another attempt is refused by the domain, and the next read shows what really happened.
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

    TaskCompletionSource _pulse = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Dictionary<string, string> _blobs = [];
    EventPublisher Publisher => context.Publisher;

    Task NextPulse => Volatile.Read(ref _pulse).Task;

    /// <summary>The session changed; a waiting pass looks again.</summary>
    public void Pulse() => Interlocked.Exchange(ref _pulse, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    /// <summary>Runs until this attempt has nothing more to do, or <paramref name="stopping"/> ends it. True when the run waits only
    /// for a person: their answer starts the next action.</summary>
    public async Task<bool> RunAsync(CancellationToken stopping)
    {
        AgentEnvironment? environment = null;
        var firstPass = true;
        string? suspectCall = null;
        while (true)
        {
            stopping.ThrowIfCancellationRequested();
            if (Read() is not { } view) return false;
            await ResolveReplaysAsync(view, stopping);
            var state = AgentConversation.Rebuild(view.Items);
            if (firstPass)
            {
                // A loop that was cut off may have been running its first open call; one that stopped by itself had not started it.
                firstPass = false;
                if (!context.CleanStart && state.OpenCalls.Count > 0 && view.RequestOf(state.OpenCalls[0])?.Status != RuntimeRequestStatus.Pending)
                    suspectCall = state.OpenCalls[0].Id;
            }
            if (view.Runner.InterruptRequested)
            {
                await FinishAsync(view, state, "interrupted", false, null, true, stopping);
                continue;
            }
            // Asked for (/compact): once nothing else is left to do, and only when there is something to summarize.
            var compacting = view.Runner.CompactRequested && state.OpenCalls.Count == 0 && !state.NeedsModel && !state.JustCompacted &&
                             state.Conversation.Count > 0 && InstructionsOf(view) != null;
            if (state.OpenCalls.Count == 0 && !state.NeedsModel && !compacting)
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
            if (compacting)
            {
                await CompactAsync(view, state, environment, InstructionsOf(view)!, false, stopping);
                continue;
            }
            if (state.OpenCalls.Count > 0 && view.RequestOf(state.OpenCalls[0])?.Status == RuntimeRequestStatus.Pending) return true;
            if (state.OpenCalls.Count > 0) await RunToolAsync(view, state.OpenCalls[0], environment, suspectCall, stopping);
            else if (InstructionsOf(view) is { } instructions)
            {
                if (IsNearTheLimit(state, environment)) await CompactAsync(view, state, environment, instructions, false, stopping);
                else await CallModelAsync(view, state, environment, instructions, stopping);
            }
            else await RecordInstructionsAsync(view, environment, stopping);
        }
    }

    bool Holds(AgentRunner runner) => runner.ActionId == context.ActionId && runner.Attempt == context.Attempt;

    AgentView? Read()
    {
        using var query = Publisher.StartQuery();
        if (Ambient.Relation<IAgentRunnerTable>().FindByIdOrDefault(sessionId) is not { } runner || !Holds(runner) ||
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

    /// <summary>Below this many cached tokens, a lapsing cache is not worth showing.</summary>
    const long CacheWorthShowing = 10_000;

    Task RecordModelAsync(AgentView view, IEnumerable<AgentItem> items, TurnUsage usage, long contextTokens, AgentEnvironment environment, CancellationToken cancellation) =>
        RecordAsync(view, items, usage, cancellation, environment.Model?.CostOf(usage) ?? 0, contextTokens, environment.Model?.InputLimit ?? 0,
            usage.CachedInputTokens + usage.CacheWriteTokens >= CacheWorthShowing ? environment.PromptCacheSeconds : 0);

    Task RecordAsync(AgentView view, IEnumerable<AgentItem> items, TurnUsage? usage, CancellationToken cancellation, double costUsd = 0, long contextTokens = 0, long contextLimit = 0,
        int cacheSeconds = 0) =>
        Publisher.PublishAndWaitAsync(new AgentItemsRecorded
        {
            CostUsd = costUsd, ContextTokens = contextTokens, ContextLimit = contextLimit, CacheSeconds = cacheSeconds,
            SessionId = sessionId, NodeId = context.NodeId, Version = view.Runner.Version, ActionId = context.ActionId, Attempt = context.Attempt,
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
            SessionId = sessionId, NodeId = context.NodeId, Version = view.Runner.Version, ActionId = context.ActionId, Attempt = context.Attempt, SeenSeq = view.Session.LastSeq, Outcome = outcome,
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

    static AgentItem Result(ToolCall call, string text, bool isError, string? itemRef = null, PromptImage[]? images = null) =>
        new(SessionItemKind.ToolResult, text, call.Id, call.Name, IsError: isError, Ref: itemRef,
            Data: images is { Length: > 0 } ? JsonSerializer.Serialize(images, ProtocolJsonContext.Default.PromptImageArray) : null);

    /// <summary>Runs <paramref name="work"/> and cancels it when the person interrupts or this attempt no longer holds the run.</summary>
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
        return Ambient.Relation<IAgentRunnerTable>().FindByIdOrDefault(sessionId) is not { } runner || !Holds(runner) || runner.InterruptRequested;
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

    /// <summary>After a restart nothing says how large the last request was, so roughly four characters to a token will do, and an image
    /// counts as about the most the providers charge for one.</summary>
    static long EstimateTokens(IReadOnlyList<ConversationItem> conversation) => conversation.Sum(item => (long)(item switch
    {
        UserMessage user => user.Text.Length,
        AssistantMessage assistant => assistant.Text.Length,
        ToolCall call => call.Arguments.Length,
        ToolResult result => result.Text.Length,
        ReasoningItem reasoning => reasoning.ProviderData?.Length ?? reasoning.Text.Length,
        _ => 0
    }) / 4 + ImagesIn(item).Count() * ImageTokens);

    const long ImageTokens = 1600;

    /// <summary>Replaces the conversation so far with the model's summary. A conversation too large for even that is summarized with its tool results cut short.</summary>
    async Task CompactAsync(AgentView view, AgentConversationState state, AgentEnvironment environment, AgentInstructionsRecord instructions, bool shortened, CancellationToken stopping)
    {
        var (history, imageData) = shortened ? (Shortened(state.Conversation), null) : await WithImagesAsync(state.Conversation, environment, stopping);
        var request = new TurnRequest(view.Session.Model!, instructions.Shared, [..history, new UserMessage(CompactionInstruction)],
            environment.Tools.SpecsFor(view.Session.PermissionMode), sessionId.ToString(), instructions.Session, view.Session.ReasoningEffort, environment.MaxOutputTokens, NoToolUse: true,
            ImageData: imageData);
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
        await RecordModelAsync(view, [new(SessionItemKind.Compaction, summary, Replay: await StoredFormAsync(new UserMessage(CompactionPrefix + summary), stopping))],
            response.Usage, Math.Max(1, summary.Length / 4), environment, stopping);
    }

    const string SideQuestionInstruction = """
        The person asks this on the side, while the work goes on. Answer it from what you know so far, briefly. Do not call tools, and do not
        continue the work: this question and your answer are not kept in the conversation.

        """;

    const string StillRunning = "This call is still running.";

    /// <summary>Answers a side question (/btw) from the conversation before it, beside any run: one call without tools, which only the
    /// action's outcome records. What is expected to go wrong comes back as an answer marked as an error.</summary>
    public async Task<AgentSideAnswer> AnswerSideQuestionAsync(long questionSeq, CancellationToken stopping)
    {
        Session session;
        List<SessionItem> items;
        using (Publisher.StartQuery())
        {
            if (Ambient.Relation<ISessionTable>().FindByIdOrDefault(sessionId) is not { } found) return Unanswered("The session no longer exists.");
            session = found;
            items = Ambient.Relation<ISessionItemTable>().ListById(sessionId, new AdvancedEnumeratorParam<long>(EnumerationOrder.Ascending, 0,
                KeyProposition.Excluded, questionSeq, KeyProposition.Included)).ToList();
        }
        if (items.LastOrDefault() is not { Kind: SessionItemKind.SideQuestion } question || question.Seq != questionSeq) return Unanswered("The question is missing.");
        var view = new AgentView(session, new AgentRunner(), items, [], false);
        await ResolveReplaysAsync(view, stopping);
        var state = AgentConversation.Rebuild(items);
        AgentEnvironment environment;
        try { (environment, _) = await context.OpenEnvironmentAsync(session, stopping); }
        catch (AgentSetupException problem) { return Unanswered(problem.Message); }
        var instructions = InstructionsOf(view) ??
                           new AgentInstructionsRecord(environment.SharedInstructions, AgentInstructions.Session(environment.RuntimeContext, null, null, session.SystemPrompt));
        // A call that is still running has no result yet, and the providers want one before anything else is said.
        var conversation = state.Conversation.Concat(state.OpenCalls.Select(call => new ToolResult(call.Id, StillRunning)))
            .Append(new UserMessage(SideQuestionInstruction + question.Text)).ToList();
        var (history, imageData) = await WithImagesAsync(conversation, environment, stopping);
        var request = new TurnRequest(session.Model!, instructions.Shared, history, environment.Tools.SpecsFor(session.PermissionMode), sessionId.ToString(),
            instructions.Session, session.ReasoningEffort, environment.MaxOutputTokens, NoToolUse: true, ImageData: imageData);
        TurnResponse response;
        try { response = await environment.Executor.ExecuteAsync(request, _ => ValueTask.CompletedTask, stopping); }
        catch (ModelProviderException problem) { return Unanswered(problem.Message); }
        var answer = string.Concat(response.Output.OfType<AssistantMessage>().Select(message => message.Text)).Trim();
        var cost = environment.Model?.CostOf(response.Usage) ?? 0;
        return answer.Length == 0
            ? new() { Text = "The model gave no answer.", IsError = true, CostUsd = cost }
            : new() { Text = answer.Length <= ItemReport.InlineLimit ? answer : answer[..ItemReport.InlineLimit] + "…", CostUsd = cost };
    }

    static AgentSideAnswer Unanswered(string why) => new() { Text = why, IsError = true };

    /// <summary>Without reasoning and images, and with long tool results cut.</summary>
    static IReadOnlyList<ConversationItem> Shortened(IReadOnlyList<ConversationItem> conversation) => WithoutImages(conversation.Where(item => item is not ReasoningItem)
        .Select(item => item is ToolResult { Text.Length: > CompactedToolResult } result ? result with { Text = result.Text[..CompactedToolResult] + " [cut]" } : item).ToList(),
        _ => true, "the conversation had to be shortened");

    async Task CallModelAsync(AgentView view, AgentConversationState state, AgentEnvironment environment, AgentInstructionsRecord instructions, CancellationToken stopping)
    {
        var itemRef = $"agent-{view.Session.LastSeq}";
        var thinkingRef = $"thinking-{view.Session.LastSeq}";
        var (conversation, imageData) = await WithImagesAsync(state.Conversation, environment, stopping);
        var request = new TurnRequest(view.Session.Model!, instructions.Shared, conversation,
            environment.Tools.SpecsFor(view.Session.PermissionMode), sessionId.ToString(), instructions.Session, view.Session.ReasoningEffort, environment.MaxOutputTokens,
            ImageData: imageData);
        TurnResponse response;
        try
        {
            response = await WatchedAsync(cancellation => environment.Executor.ExecuteAsync(request, delta => StreamAsync(itemRef, thinkingRef, delta), cancellation), stopping);
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
        var thinkingRefUsed = false;
        foreach (var output in response.Output)
            switch (output)
            {
                case AssistantMessage message:
                    items.Add(new(SessionItemKind.Assistant, message.Text, Ref: refUsed ? null : itemRef, Replay: await StoredFormAsync(message, stopping)));
                    refUsed = true;
                    break;
                case ReasoningItem reasoning:
                    items.Add(new(SessionItemKind.Thinking, reasoning.Text, Ref: thinkingRefUsed ? null : thinkingRef, Replay: await StoredFormAsync(reasoning, stopping)));
                    thinkingRefUsed = true;
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
        await RecordModelAsync(view, items, response.Usage, _lastContextTokens.Value, environment, stopping);
    }

    ValueTask StreamAsync(string itemRef, string thinkingRef, TurnDelta delta)
    {
        // Tool arguments arrive with the finished step: they mean little until they are whole.
        switch (delta.Kind)
        {
            case TurnDeltaKind.Text:
                context.PublishDelta(new(sessionId, itemRef, delta.Text));
                break;
            case TurnDeltaKind.Reasoning:
                context.PublishDelta(new(sessionId, thinkingRef, delta.Text));
                break;
            case TurnDeltaKind.Restart:
                context.PublishDelta(new(sessionId, itemRef, "", true));
                context.PublishDelta(new(sessionId, thinkingRef, "", true));
                break;
        }
        return ValueTask.CompletedTask;
    }

    static string Summary(string arguments) => arguments.Length <= 120 ? arguments : arguments[..119] + "…";

    async Task RunToolAsync(AgentView view, ToolCall call, AgentEnvironment environment, string? suspectCall, CancellationToken stopping)
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
                    SessionId = sessionId, NodeId = context.NodeId, Version = view.Runner.Version, ActionId = context.ActionId, Attempt = context.Attempt, Key = ApprovalKey(view.SeqOf(call)), ToolUseId = call.Id, ToolName = call.Name,
                    Input = call.Arguments, Description = kind switch
                    {
                        AgentToolKind.Ask => Str(arguments["question"]),
                        AgentToolKind.MachineShell => Str(arguments["command"]),
                        AgentToolKind.MachineWrite or AgentToolKind.MachineUpload => Str(arguments["path"]),
                        _ => null
                    }
                }, stopping);
                return;
            }
            switch (request.Status)
            {
                case RuntimeRequestStatus.Pending:
                    return; // RunAsync stops before that
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
        if (kind == AgentToolKind.MachineUpload && !result.IsError && JsonSerializer.Deserialize(result.Text, ProtocolJsonContext.Default.UploadedFile) is { } file)
        {
            // The same notice daemon harnesses give, which the page turns into a download.
            await RecordAsync(view,
            [
                new AgentItem(SessionItemKind.Notice, $"Uploaded {file.Name} ({file.Size} bytes).", Data: new JsonObject { ["blob"] = file.Sha256, ["fileName"] = file.Name, ["size"] = file.Size }.ToJsonString()),
                Result(call, $"Uploaded {file.Name}.", false, toolCall.LiveRef)
            ], null, stopping);
            return;
        }
        await RecordAsync(view, [Result(call, result.Text, result.IsError, toolCall.LiveRef, await ImagesOfAsync(result, stopping))], null, stopping);
    }

    /// <summary>The result's images as blobs: a machine's tools stored them already, an MCP server over HTTP sent their content.</summary>
    async Task<PromptImage[]?> ImagesOfAsync(McpToolResult result, CancellationToken cancellation)
    {
        var images = new List<PromptImage>(result.Images ?? []);
        foreach (var content in (result.NewImages ?? []).Take(PromptImages.MaximumCount - images.Count))
        {
            using var stream = new MemoryStream(content, false);
            var (file, sha256, size) = await BlobHash.SpoolAsync(stream, PromptImages.MaximumBytes, cancellation);
            await context.Blobs.PutAsync(sha256, file, cancellation);
            images.Add(new(sha256, PromptImages.MediaTypeOf(content.AsSpan(0, Math.Min(content.Length, PromptImages.HeaderLength)))!, size));
        }
        return images.Count > 0 ? images.Take(PromptImages.MaximumCount).ToArray() : null;
    }

    /// <summary>The content of the images the last request had, in base64; read again only when the conversation gains one.</summary>
    Dictionary<string, string> _imageData = [];

    /// <summary>What all of a request's images may take. The APIs refuse requests far larger than this (Anthropic at 32 MB).</summary>
    const long ImageBudget = 20 * 1024 * 1024;

    /// <summary>The APIs take at most this many images in one request.</summary>
    const int MostImages = 100;

    /// <summary>The conversation as the model may see it, with its images' content. The newest images go first; older ones beyond what a request can carry,
    /// and all of them for a model that cannot read images, are replaced by a note.</summary>
    async Task<(IReadOnlyList<ConversationItem> Conversation, IReadOnlyDictionary<string, string>? Data)> WithImagesAsync(IReadOnlyList<ConversationItem> conversation,
        AgentEnvironment environment, CancellationToken cancellation)
    {
        var images = conversation.SelectMany(ImagesIn).Reverse().DistinctBy(image => image.Sha256).ToList();
        if (images.Count == 0) return (conversation, null);
        if (environment.Model is { Images: false }) return (WithoutImages(conversation, _ => true, "this model cannot read images"), null);
        var data = new Dictionary<string, string>();
        var leftOut = new HashSet<string>();
        long used = 0;
        foreach (var image in images)
        {
            if (data.Count == MostImages || used + (image.Size + 2) / 3 * 4 > ImageBudget) leftOut.Add(image.Sha256);
            else if (_imageData.TryGetValue(image.Sha256, out var content) || (content = await ReadImageAsync(image, cancellation)) != null)
            {
                data[image.Sha256] = content;
                used += content.Length;
            }
            // one that cannot be read stays, and the provider is told it is missing
        }
        _imageData = data;
        return (leftOut.Count == 0 ? conversation : WithoutImages(conversation, image => leftOut.Contains(image.Sha256), "older images are left out to keep the request within the provider's limits"), data);
    }

    async Task<string?> ReadImageAsync(PromptImage image, CancellationToken cancellation)
    {
        await using var stream = await context.Blobs.OpenAsync(image.Sha256, cancellation);
        if (stream == null || stream.Length > PromptImages.MaximumBytes) return null;
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellation);
        var bytes = memory.GetBuffer().AsSpan(0, (int)memory.Length);
        return PromptImages.MediaTypeOf(bytes[..Math.Min(bytes.Length, PromptImages.HeaderLength)]) == image.MediaType ? Convert.ToBase64String(bytes) : null;
    }

    static IEnumerable<PromptImage> ImagesIn(ConversationItem item) => item switch
    {
        UserMessage { Images: { } images } => images,
        ToolResult { Images: { } images } => images,
        _ => []
    };

    /// <summary>Replaces the images <paramref name="leaveOut"/> picks with a note saying why.</summary>
    static IReadOnlyList<ConversationItem> WithoutImages(IReadOnlyList<ConversationItem> conversation, Func<PromptImage, bool> leaveOut, string why)
    {
        PromptImage[]? Kept(PromptImage[]? images) => images?.Where(image => !leaveOut(image)).ToArray() is { Length: > 0 } kept ? kept : null;
        string Note(PromptImage[]? images) => images?.Count(leaveOut) is > 0 and var count ? $"\n[{count} image{(count == 1 ? "" : "s")} left out: {why}.]" : "";
        return conversation.Select(item => item switch
        {
            UserMessage { Images: { } images } user when images.Any(leaveOut) => user with { Text = user.Text + Note(images), Images = Kept(images) },
            ToolResult { Images: { } images } result when images.Any(leaveOut) => result with { Text = result.Text + Note(images), Images = Kept(images) },
            _ => item
        }).ToList();
    }

    static string Added(string? answer) => string.IsNullOrWhiteSpace(answer) ? "" : $" They added: {answer}";

    static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
