using System.Text.Json;
using BoSoFa.Protocol;
using BoSoFa.Server.Btdb;
using BoSoFa.Server.Domain;
using BTDB.Replication.EventSourcing;
using Microsoft.AspNetCore.Http.HttpResults;

namespace BoSoFa.Server.Machines;

/// <summary>Daemon releases and their rollouts (plan §5.7).</summary>
public static class ReleaseEndpoints
{
    const int DefaultFailAfterSeconds = 600;

    public static void MapReleaseEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization();
        api.MapGet("/daemon-releases", ListReleasesAsync);
        api.MapPost("/daemon-releases", PublishAsync);
        api.MapGet("/daemon-rollouts", ListRolloutsAsync);
        api.MapPost("/daemon-rollouts", StartAsync);
        api.MapPost("/daemon-rollouts/{id}/halt", HaltAsync);
        api.MapPut("/machines/{id}/update-policy", SetPolicyAsync);
    }

    static DaemonManifest? Parse(string manifest)
    {
        try { return JsonSerializer.Deserialize(manifest, ProtocolJsonContext.Default.DaemonManifest); }
        catch (JsonException) { return null; }
    }

    static async Task<DaemonReleaseDto[]> ListReleasesAsync(BtdbNode node, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using var query = publisher.StartQuery();
        return Ambient.Relation<IDaemonReleaseTable>().OrderByDescending(release => release.PublishedAt)
            .Select(release => new DaemonReleaseDto(release.Version, release.Channel, release.PublishedAt,
                Parse(release.Manifest)?.Artifacts.Select(artifact => artifact.Rid).ToArray() ?? [])).ToArray();
    }

    static async Task<Results<NoContent, NotFound, ForbidHttpResult, BadRequest<string>>> PublishAsync(PublishDaemonRelease request, IBlobStore blobs, BtdbNode node,
        HttpContext http)
    {
        var manifest = Parse(request.Manifest);
        if (manifest is not { Artifacts.Length: > 0 } || !DaemonReleases.IsValidVersion(manifest.Version) || !DaemonReleases.IsValidChannel(request.Channel))
            return TypedResults.BadRequest("The manifest needs a plain version and some artifacts, on the stable or beta channel.");
        foreach (var artifact in manifest.Artifacts)
            if (!BlobHash.IsValid(artifact.Sha256) || !await blobs.ExistsAsync(artifact.Sha256, http.RequestAborted))
                return TypedResults.BadRequest($"The artifact for {artifact.Rid} is not in the blob store.");
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        var result = await publisher.PublishAndWaitAsync(new DaemonReleasePublished
        {
            UserId = http.User.UserId() ?? 0, Version = manifest.Version, Channel = request.Channel, Manifest = request.Manifest, Signature = request.Signature
        }, http.RequestAborted);
        return Result(result.Outcome);
    }

    static Results<NoContent, NotFound, ForbidHttpResult, BadRequest<string>> Result(CommandOutcome outcome) => outcome switch
    {
        CommandOutcome.Ok => TypedResults.NoContent(),
        CommandOutcome.NotFound => TypedResults.NotFound(),
        CommandOutcome.Forbidden => TypedResults.Forbid(),
        _ => TypedResults.BadRequest("That is not possible now.")
    };

    static async Task<DaemonRolloutDto[]> ListRolloutsAsync(BtdbNode node, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        using var query = publisher.StartQuery();
        var progress = Ambient.Relation<IDaemonRolloutMachineTable>();
        return Ambient.Relation<IDaemonRolloutTable>().OrderByDescending(rollout => rollout.StartedAt).Take(50).Select(rollout =>
        {
            var rows = progress.ListById(rollout.Id).ToList();
            return new DaemonRolloutDto(rollout.Id, rollout.Version, rollout.Channel, rollout.Status, rollout.StartedAt, rows.Count(row => row.Updated),
                rows.Count(row => !row.Updated), rollout.Detail);
        }).ToArray();
    }

    static async Task<Results<NoContent, NotFound, ForbidHttpResult, BadRequest<string>>> StartAsync(StartDaemonRollout request, BtdbNode node, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        var result = await publisher.PublishAndWaitAsync(new DaemonRolloutStarted
        {
            UserId = http.User.UserId() ?? 0, Version = request.Version, WaveSize = request.WaveSize, FailAfterSeconds = request.FailAfterSeconds ?? DefaultFailAfterSeconds
        }, http.RequestAborted);
        return Result(result.Outcome);
    }

    static async Task<Results<NoContent, NotFound, ForbidHttpResult, BadRequest<string>>> HaltAsync(ulong id, BtdbNode node, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        return Result((await publisher.PublishAndWaitAsync(new DaemonRolloutHaltRequested { UserId = http.User.UserId() ?? 0, RolloutId = id }, http.RequestAborted)).Outcome);
    }

    static async Task<Results<NoContent, NotFound, ForbidHttpResult, BadRequest<string>>> SetPolicyAsync(ulong id, SetMachineUpdatePolicy request, BtdbNode node, HttpContext http)
    {
        var publisher = await node.Ready.WaitAsync(http.RequestAborted);
        return Result((await publisher.PublishAndWaitAsync(new MachineUpdatePolicySet
        {
            UserId = http.User.UserId() ?? 0, MachineId = id, Channel = request.Channel, PinnedVersion = request.PinnedVersion
        }, http.RequestAborted)).Outcome);
    }
}

/// <summary>Keeps rollouts moving while any runs. Every node does this: the handler is safe to run too often.</summary>
public sealed class DaemonRolloutTicker(BtdbNode node, ServerOptions options, TimeProvider time, ILogger<DaemonRolloutTicker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.RolloutTickSeconds), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (node.Publisher is not { } publisher) continue;
                bool running;
                using (publisher.StartQuery())
                    running = Ambient.Relation<IDaemonRolloutTable>().Any(rollout => rollout.Status == DaemonRolloutStatus.Running);
                if (!running) continue;
                try { await publisher.PublishAsync(new DaemonRolloutTicked(), stoppingToken); }
                catch (Exception exception) when (exception is IOException or InvalidOperationException)
                {
                    logger.LogDebug("Rollout tick failed: {Error}", exception.Message);
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
