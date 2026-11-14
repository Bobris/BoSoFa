using BoSoFa.Server.Btdb;
using BoSoFa.Server.Domain;
using BTDB.BackendActions;
using BTDB.Replication.EventSourcing;

namespace BoSoFa.Server.SkillShare;

/// <summary>
/// Mirrors SkillShare's skill catalog (docs/skillshare.md §4.1): reads what changed since the last read and publishes only that, in
/// batches, so an unchanged catalog writes nothing. A failure is recorded once, not at every attempt.
/// </summary>
public sealed class SkillShareCatalogSyncHandler(SkillShareClient skillShare, BtdbNode node, ILogger<SkillShareCatalogSyncHandler> logger)
    : IActionHandler<SkillShareCatalogSync, SkillShareSyncResult>, IScheduledActionGate
{
    const int Batch = 50;

    public bool CanStartScheduled(DateTime now) => skillShare.MayBeEnabled;

    public async Task<ActionResult<SkillShareSyncResult>> ExecuteAsync(SkillShareCatalogSync input, IActionContext context)
    {
        if (await skillShare.InfoAsync(context.Cancellation) == null) return new SkillShareSyncResult();
        var publisher = await node.Ready.WaitAsync(context.Cancellation);
        string cursor, lastError;
        using (publisher.StartQuery())
        {
            var sync = Ambient.Relation<ISkillShareSyncTable>().FindByIdOrDefault(SkillShareCatalog.SyncRowId);
            (cursor, lastError) = (sync?.Cursor ?? "", sync?.LastError ?? "");
        }
        SkillShareRemoteSkills read;
        try { read = await skillShare.ReadCatalogAsync(cursor, context.Cancellation); }
        catch (SkillShareException problem)
        {
            logger.LogWarning("SkillShare's catalog could not be read: {Message}", problem.Message);
            if (problem.Message != lastError)
                await publisher.PublishAndWaitAsync(new SkillShareCatalogSynced { Error = problem.Message }, context.Cancellation);
            return new SkillShareSyncResult();
        }
        List<SyncedSkill> changed;
        using (publisher.StartQuery())
        {
            var skills = Ambient.Relation<ISkillShareSkillTable>();
            changed = read.Skills.Where(skill => skills.FindByIdOrDefault(skill.Id) is not { } known || !Same(known, skill)).ToList();
        }
        if (changed.Count == 0)
        {
            // Nothing changed: an event only to clear an error, or to move the cursor at most hourly (asking again from an older
            // cursor costs a few skills, an event every ten minutes would be kept forever).
            DateTime lastSync;
            using (publisher.StartQuery())
                lastSync = Ambient.Relation<ISkillShareSyncTable>().FindByIdOrDefault(SkillShareCatalog.SyncRowId)?.LastSyncAt ?? DateTime.MinValue;
            if (lastError.Length > 0 || (cursor != read.Cursor && DateTime.UtcNow - lastSync > TimeSpan.FromHours(1)))
                await publisher.PublishAndWaitAsync(new SkillShareCatalogSynced { Cursor = read.Cursor ?? "" }, context.Cancellation);
            return new SkillShareSyncResult();
        }
        for (var start = 0; start < changed.Count; start += Batch)
            await publisher.PublishAndWaitAsync(new SkillShareCatalogSynced
            {
                Skills = changed.Skip(start).Take(Batch).ToList(), Cursor = start + Batch >= changed.Count ? read.Cursor ?? "" : ""
            }, context.Cancellation);
        return new SkillShareSyncResult { Changed = changed.Count };
    }

    static bool Same(SkillShareSkill known, SyncedSkill skill) =>
        known.Name == skill.Name && known.BundleName == skill.BundleName && known.Description == skill.Description && known.Tags == skill.Tags &&
        known.Owner == skill.Owner && known.Deprecated == skill.Deprecated && known.Deleted == skill.Deleted && known.Versions == skill.Versions;

    public void OnFinished(SkillShareCatalogSync input, ActionOutcome<SkillShareSyncResult> outcome) { }
}
