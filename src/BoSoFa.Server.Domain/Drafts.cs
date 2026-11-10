using BTDB.ODBLayer;
using BTDB.Replication.EventSourcing;

namespace BoSoFa.Server.Domain;

/// <summary>A session someone began to write and has not started: the prompt and how it should start. Only its owner sees it; starting
/// the session or discarding the draft removes it.</summary>
public class SessionDraft
{
    [PrimaryKey(1)] public ulong OwnerId { get; set; }
    [PrimaryKey(2)] public ulong Id { get; set; }
    public string Prompt { get; set; } = "";

    /// <summary>How to start it, as the client's own JSON: the server keeps it without reading it.</summary>
    public string Settings { get; set; } = "";

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public interface ISessionDraftTable : IRelation<SessionDraft>
{
    void Insert(SessionDraft draft);
    void Update(SessionDraft draft);
    SessionDraft? FindByIdOrDefault(ulong ownerId, ulong id);
    bool RemoveById(ulong ownerId, ulong id);
    IEnumerable<SessionDraft> ListById(ulong ownerId);
}

/// <summary>Saves a draft: a new one when <see cref="DraftId"/> is zero.</summary>
public class SessionDraftSaved : EventWithResult<SessionCommandResult>
{
    public ulong UserId { get; set; }
    public ulong DraftId { get; set; }
    public string Prompt { get; set; } = "";
    public string Settings { get; set; } = "";
}

public class SessionDraftDiscarded : EventWithResult<SessionCommandResult>
{
    public ulong UserId { get; set; }
    public ulong DraftId { get; set; }
}

public static class Drafts
{
    public const int MaximumSettingsLength = 16 * 1024;
    public const int MaximumPromptLength = Sessions.MaximumPromptLength;

    /// <summary>Nobody keeps more; the oldest go first.</summary>
    public const int MaximumPerPerson = 200;

    /// <summary>The title the session will get; empty while nothing is written.</summary>
    public static string TitleOf(SessionDraft draft) => draft.Prompt.Trim().Length == 0 ? "" : Sessions.TitleOf(draft.Prompt);
}

public sealed class SessionDraftSavedHandler : IEventHandler<SessionDraftSaved, SessionCommandResult>
{
    public SessionCommandResult? Validate(SessionDraftSaved ev) =>
        ev.UserId == 0 || ev.Prompt.Length > Sessions.MaximumPromptLength || ev.Settings.Length > Drafts.MaximumSettingsLength
            ? new() { Outcome = CommandOutcome.Invalid }
            : null;

    public SessionCommandResult Consume(SessionDraftSaved ev)
    {
        var drafts = Ambient.Relation<ISessionDraftTable>();
        if (ev.DraftId != 0)
        {
            if (drafts.FindByIdOrDefault(ev.UserId, ev.DraftId) is not { } draft) return new() { Outcome = CommandOutcome.NotFound };
            (draft.Prompt, draft.Settings, draft.UpdatedAt) = (ev.Prompt, ev.Settings, ev.When);
            drafts.Update(draft);
            return new() { Outcome = CommandOutcome.Ok, SessionId = draft.Id };
        }
        foreach (var old in drafts.ListById(ev.UserId).OrderByDescending(draft => draft.UpdatedAt).Skip(Drafts.MaximumPerPerson - 1).ToList())
            drafts.RemoveById(old.OwnerId, old.Id);
        var id = Ambient.AllocateId();
        drafts.Insert(new() { OwnerId = ev.UserId, Id = id, Prompt = ev.Prompt, Settings = ev.Settings, CreatedAt = ev.When, UpdatedAt = ev.When });
        return new() { Outcome = CommandOutcome.Ok, SessionId = id };
    }
}

public sealed class SessionDraftDiscardedHandler : IEventHandler<SessionDraftDiscarded, SessionCommandResult>
{
    public SessionCommandResult? Validate(SessionDraftDiscarded ev) => null;

    public SessionCommandResult Consume(SessionDraftDiscarded ev) => Ambient.Relation<ISessionDraftTable>().RemoveById(ev.UserId, ev.DraftId)
        ? new() { Outcome = CommandOutcome.Ok }
        : new() { Outcome = CommandOutcome.NotFound };
}
