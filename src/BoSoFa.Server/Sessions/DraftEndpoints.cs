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
