using BoSoFa.Protocol;
using BoSoFa.Server.Btdb;
using BoSoFa.Server.Domain;
using BoSoFa.Server.Tokens;
using BTDB.Replication.EventSourcing;
using Microsoft.AspNetCore.Http.HttpResults;

namespace BoSoFa.Server.Sessions;

/// <summary>Sessions being written, saved as they are typed so they can be picked up again anywhere. Each person sees only their own.</summary>
public static class DraftEndpoints
{
    public static void MapDraftEndpoints(this WebApplication app)
    {
        var drafts = app.MapGroup("/api/v1/drafts").RequireAuthorization();
        drafts.MapGet("", ListDraftsAsync);
        drafts.MapPost("", CreateDraftAsync);
        drafts.MapGet("/{id}", GetDraftAsync);
        drafts.MapPut("/{id}", SaveDraftAsync);
        drafts.MapDelete("/{id}", DiscardDraftAsync);
        drafts.MapGet("/{id}/shell-commands", ListShellCommandsAsync);
        drafts.MapPost("/{id}/shell-commands", RunShellCommandAsync);
        drafts.MapPost("/{id}/shell-commands/{seq}/stop", StopShellCommandAsync);
    }

    /// <summary>The items of the draft's shell commands (docs/shell-commands.md), with the draft's own seqs.</summary>
    static async Task<Results<Ok<SessionItemDto[]>, NotFound>> ListShellCommandsAsync(ulong id, BtdbNode node, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using var query = publisher.StartQuery();
        return ShellItemsOf(id, http.User.UserId() ?? 0) is { } items ? TypedResults.Ok(items) : TypedResults.NotFound();
    }

    /// <summary>What the person sees of the draft's commands; null when it is not their draft.</summary>
    internal static SessionItemDto[]? ShellItemsOf(ulong draftId, ulong userId) =>
        Ambient.Relation<ISessionDraftTable>().FindByIdOrDefault(userId, draftId) is null
            ? null
            : DraftShellCommands.ItemsOf(draftId).Where(item => item.MovedToSessionId == 0).Select(item => item.ToDto()).ToArray();

    /// <summary>Runs a command from the prompt of the draft, in the folder of the machine it chose, with its profile's variables.</summary>
    static async Task<Results<Ok<SessionItemDto[]>, NotFound, ForbidHttpResult, BadRequest<string>>> RunShellCommandAsync(ulong id,
        RunDraftShellCommandRequest request, BtdbNode node, HttpContext http)
    {
        if (http.User.IsReadOnly()) return TypedResults.Forbid();
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        if (string.IsNullOrWhiteSpace(request.WorkingDirectory)) return TypedResults.BadRequest("Choose the folder to run the command in.");
        if (request.ProfileName != null)
            using (publisher.StartQuery())
                if (Ambient.Relation<IProfileTable>().FindByIdOrDefault(request.ProfileName) is { } profile &&
                    DraftShellCommands.Refusal(profile, request.MachineId) is { } refusal)
                    return TypedResults.BadRequest(refusal);
        var userId = http.User.UserId() ?? 0;
        var result = await publisher.PublishAndWaitAsync(new DraftShellCommandRun
        {
            UserId = userId, DraftId = id, MachineId = request.MachineId, WorkingDirectory = request.WorkingDirectory, ProfileName = request.ProfileName,
            Command = request.Command
        }, http.RequestAborted);
        switch (result.Outcome)
        {
            case CommandOutcome.NotFound: return TypedResults.NotFound();
            case CommandOutcome.Forbidden: return TypedResults.Forbid();
            case CommandOutcome.Invalid:
                return TypedResults.BadRequest(string.IsNullOrWhiteSpace(request.Command) || request.Command.Trim().Length > BoSoFa.Protocol.ShellCommands.MaximumCommandLength
                    ? "The command is empty or too long."
                    : $"At most {BoSoFa.Protocol.ShellCommands.MaximumRunning} commands run at once.");
        }
        using var query = publisher.StartQuery();
        return TypedResults.Ok(ShellItemsOf(id, userId) ?? []);
    }

    static async Task<Results<NoContent, NotFound, ForbidHttpResult>> StopShellCommandAsync(ulong id, long seq, BtdbNode node, HttpContext http)
    {
        if (http.User.IsReadOnly()) return TypedResults.Forbid();
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        var result = await publisher.PublishAndWaitAsync(new DraftShellCommandStopRequested { UserId = http.User.UserId() ?? 0, DraftId = id, Seq = seq },
            http.RequestAborted);
        return result.Outcome == CommandOutcome.Ok ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    static SessionDraftDto ToDto(SessionDraft draft) =>
        new(draft.Id, Drafts.TitleOf(draft), draft.Prompt, draft.Settings,
            draft.CreatedAt, draft.UpdatedAt);

    static async Task<SessionDraftDto[]> ListDraftsAsync(BtdbNode node, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using var query = publisher.StartQuery();
        return Ambient.Relation<ISessionDraftTable>().ListById(http.User.UserId() ?? 0)
            .OrderByDescending(draft => draft.UpdatedAt).Select(ToDto).ToArray();
    }

    static async Task<Results<Ok<SessionDraftDto>, NotFound>> GetDraftAsync(ulong id, BtdbNode node, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using var query = publisher.StartQuery();
        return Ambient.Relation<ISessionDraftTable>().FindByIdOrDefault(http.User.UserId() ?? 0, id) is { } draft
            ? TypedResults.Ok(ToDto(draft))
            : TypedResults.NotFound();
    }

    static Task<Results<Ok<SessionDraftDto>, NotFound, ForbidHttpResult, BadRequest<string>>> CreateDraftAsync(SaveSessionDraftRequest request,
        BtdbNode node, HttpContext http) => SaveAsync(0, request, node, http);

    static Task<Results<Ok<SessionDraftDto>, NotFound, ForbidHttpResult, BadRequest<string>>> SaveDraftAsync(ulong id, SaveSessionDraftRequest request,
        BtdbNode node, HttpContext http) => SaveAsync(id, request, node, http);

    static async Task<Results<Ok<SessionDraftDto>, NotFound, ForbidHttpResult, BadRequest<string>>> SaveAsync(ulong id, SaveSessionDraftRequest request,
        BtdbNode node, HttpContext http)
    {
        if (http.User.IsReadOnly()) return TypedResults.Forbid();
        if (request.Prompt.Length > Drafts.MaximumPromptLength || request.Settings.Length > Drafts.MaximumSettingsLength)
            return TypedResults.BadRequest("The draft is too long.");
        var userId = http.User.UserId() ?? 0;
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        var result = await publisher.PublishAndWaitAsync(new SessionDraftSaved
        {
            UserId = userId, DraftId = id, Prompt = request.Prompt, Settings = request.Settings
        }, http.RequestAborted);
        if (result.Outcome != CommandOutcome.Ok) return TypedResults.NotFound();
        using var query = publisher.StartQuery();
        return TypedResults.Ok(ToDto(Ambient.Relation<ISessionDraftTable>().FindByIdOrDefault(userId, result.SessionId)!));
    }

    static async Task<Results<NoContent, NotFound, ForbidHttpResult>> DiscardDraftAsync(ulong id, BtdbNode node, HttpContext http)
    {
        if (http.User.IsReadOnly()) return TypedResults.Forbid();
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        var result = await publisher.PublishAndWaitAsync(new SessionDraftDiscarded { UserId = http.User.UserId() ?? 0, DraftId = id },
            http.RequestAborted);
        return result.Outcome == CommandOutcome.Ok ? TypedResults.NoContent() : TypedResults.NotFound();
    }
}
