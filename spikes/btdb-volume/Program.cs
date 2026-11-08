using System.Diagnostics;
using System.Text.Json;
using BTDB.ODBLayer;
using BTDB.Replication;
using BTDB.Replication.EventLog;
using BTDB.Replication.EventSourcing;
using BTDB.Replication.Http;
using BtdbVolume;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Spike 01: BTDB EventSourcing volume for session transcript items. Modes:
//   write [--scale f]                     fresh data directory, publish phases, settle, measure disk
//   restart [--drop cache|rebuild] [--label x]  start on existing data, time readiness, query sessions
//   retention                             EventLogOptions.Retention on a separate test topic (own directory)
//   rawlog                                EventLog alone, no EventPublisher (own directory)
// Options: --data <dir> (default artifacts/spikes/btdb-volume-data), --results <dir>, --offload-writes
// (wrap the event-log storage in OffloadingEventLogStorage). run.sh runs the whole spike.

var mainStarted = Stopwatch.GetTimestamp();
var sinceProcessStart = DateTime.Now - Process.GetCurrentProcess().StartTime;
var mode = args.Length > 0 ? args[0] : "write";
string Arg(string name, string fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}
var data = Path.GetFullPath(Arg("--data", "artifacts/spikes/btdb-volume-data"));
var results = Path.GetFullPath(Arg("--results", "artifacts/spikes/btdb-volume-results"));
Directory.CreateDirectory(results);
var report = new Dictionary<string, object?>
{
    ["mode"] = mode, ["startedAt"] = DateTime.Now.ToString("O"), ["data"] = data,
    ["processStartToMainMs"] = sinceProcessStart.TotalMilliseconds,
    ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    ["serverGc"] = System.Runtime.GCSettings.IsServerGC
};
var label = mode;
Bench.OffloadWrites = args.Contains("--offload-writes");
report["offloadWrites"] = Bench.OffloadWrites;
var suffix = Bench.OffloadWrites ? "-offload" : "";
try
{
    switch (mode)
    {
        case "write":
            label = "write";
            await Bench.WriteAsync(data, double.Parse(Arg("--scale", "1"), System.Globalization.CultureInfo.InvariantCulture), report);
            break;
        case "restart":
            label = "restart-" + Arg("--label", Arg("--drop", "warm"));
            await Bench.RestartAsync(data, Arg("--drop", "none"), mainStarted, report);
            break;
        case "rawlog":
            await Bench.RawLogAsync(Path.Combine(data, "rawlog"), report);
            break;
        case "retention":
            await Bench.RetentionAsync(Path.Combine(data, "retention"), report);
            break;
        default:
            throw new ArgumentException($"Unknown mode {mode}");
    }
}
catch (Exception error)
{
    report["failure"] = error.ToString();
    Console.Error.WriteLine($"FAILED: {error}");
    Environment.ExitCode = 1;
}
var json = Path.Combine(results, label + suffix + ".json");
await File.WriteAllTextAsync(json, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Results written to {json}");

static class Bench
{
    static double Ms(long from, long to) => Stopwatch.GetElapsedTime(from, to).TotalMilliseconds;

    public static void Log(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");

    // ---------------------------------------------------------------- host

    public sealed record Tuning
    {
        public TimeSpan MergeInterval { get; init; } = TimeSpan.FromSeconds(5);
        public TimeSpan EventLogDeletionDelay { get; init; } = TimeSpan.FromSeconds(20);
        public TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromSeconds(30);
        public TimeSpan ReplicationDeletionDelay { get; init; } = TimeSpan.FromSeconds(20);
        public TimeSpan CompactionInterval { get; init; } = TimeSpan.FromSeconds(30);
        public int MaximumBatchSize { get; init; } = 32;
    }

    static readonly Tuning DefaultTuning = new();

    /// <summary>--offload-writes wraps the event log storage in <see cref="OffloadingEventLogStorage"/>.</summary>
    public static bool OffloadWrites;

    static IEventLogStorage Storage(FileSystemEventLogStorage storage) =>
        OffloadWrites ? new OffloadingEventLogStorage(storage) : storage;

    const string InitialLeaderJson =
        """{"format":1,"clusterId":"bosofa","term":0,"revision":0,"applicationGeneration":0,"databaseNames":[]} """;

    static (IHost Host, SpikeNode Node) BuildHost(string data, Tuning tuning)
    {
        var shared = Path.Combine(data, "shared");
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; });
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Services.AddSingleton<IReplicationScheduler>(new SystemReplicationScheduler());
        builder.Services.AddSingleton<IReplicationLeaderStorage>(
            new FileSystemLeaderStorage(Path.Combine(shared, "leader"), TimeSpan.FromSeconds(15), InitialLeaderJson, flushToDisk: Fsync.Enabled));
        builder.Services.AddSingleton<IEventLogStorage>(Storage(new FileSystemEventLogStorage(Path.Combine(shared, "eventlog"), flushToDisk: Fsync.Enabled)));
        var nodeOptions = new BtdbNodeOptions("bosofa", "single-node", "single-node", 1, Path.Combine(data, "cache"),
            new FileSystemReplicationStorage(Path.Combine(shared, "db", SpikeNode.Database), flushToDisk: Fsync.Enabled))
        {
            CheckpointInterval = tuning.CheckpointInterval, DeletionDelay = tuning.ReplicationDeletionDelay,
            MaximumBatchSize = tuning.MaximumBatchSize
        };
        builder.Services.AddSingleton(nodeOptions);
        builder.Services.AddSingleton<SpikeNode>();
        builder.Services.AddSingleton<IReplicationNodeHost>(sp => sp.GetRequiredService<SpikeNode>());
        builder.Services.AddBTDBReplicationSingleNode(new ReplicationNodeOptions("bosofa", "single-node",
                TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(1), 1, CompactionInterval: tuning.CompactionInterval), 1000,
            TimeSpan.FromMilliseconds(250));
        builder.Services.AddBTDBEventLogSingleNode(new EventLogOptions
        {
            MergeInterval = tuning.MergeInterval, DeletionDelay = tuning.EventLogDeletionDelay
        });
        var host = builder.Build();
        var node = host.Services.GetRequiredService<SpikeNode>();
        node.Attach(host.Services);
        return (host, node);
    }

    static Dictionary<string, object?> TuningReport(Tuning t) => new()
    {
        ["eventLogMergeIntervalS"] = t.MergeInterval.TotalSeconds,
        ["eventLogDeletionDelayS"] = t.EventLogDeletionDelay.TotalSeconds,
        ["checkpointIntervalS"] = t.CheckpointInterval.TotalSeconds,
        ["replicationDeletionDelayS"] = t.ReplicationDeletionDelay.TotalSeconds,
        ["compactionIntervalS"] = t.CompactionInterval.TotalSeconds,
        ["publisherMaximumBatchSize"] = t.MaximumBatchSize
    };

    // ---------------------------------------------------------------- disk

    public static long DirBytes(string path) => !Directory.Exists(path) ? 0
        : new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f =>
        {
            try { return f.Length; }
            catch (FileNotFoundException) { return 0L; }
        });

    static int DirFiles(string path) => !Directory.Exists(path) ? 0
        : Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Count();

    public sealed record Disk(long EventLog, long Db, long Cache, long Leader, int EventLogFiles, int DbFiles, int CacheFiles)
    {
        public long Total => EventLog + Db + Cache + Leader;

        public static Disk Of(string data)
        {
            var shared = Path.Combine(data, "shared");
            for (var attempt = 0; ; attempt++)
                try
                {
                    return new(DirBytes(Path.Combine(shared, "eventlog")), DirBytes(Path.Combine(shared, "db")),
                        DirBytes(Path.Combine(data, "cache")), DirBytes(Path.Combine(shared, "leader")),
                        DirFiles(Path.Combine(shared, "eventlog")), DirFiles(Path.Combine(shared, "db")),
                        DirFiles(Path.Combine(data, "cache")));
                }
                catch (DirectoryNotFoundException) when (attempt < 5) { Thread.Sleep(50); }
        }

        public override string ToString() =>
            $"eventlog {Mb(EventLog)} ({EventLogFiles} files), db {Mb(Db)} ({DbFiles}), cache {Mb(Cache)} ({CacheFiles}), total {Mb(Total)}";

        public Dictionary<string, object?> ToReport() => new()
        {
            ["eventLogBytes"] = EventLog, ["dbBytes"] = Db, ["cacheBytes"] = Cache, ["leaderBytes"] = Leader,
            ["eventLogFiles"] = EventLogFiles, ["dbFiles"] = DbFiles, ["cacheFiles"] = CacheFiles
        };
    }

    static string Mb(long bytes) => $"{bytes / 1048576.0:F1} MiB";

    /// <summary>Waits for merge, checkpoint, compaction and deletion passes; sizes are sampled every 5 s and the wait
    /// ends once they were unchanged for 20 s (after at least 75 s, more than two checkpoint intervals) or after
    /// 180 s.</summary>
    static async Task<(Disk Settled, Disk Peak, List<object> Samples)> SettleAsync(string data)
    {
        var started = Stopwatch.GetTimestamp();
        var samples = new List<object>();
        Disk? last = null;
        var peak = Disk.Of(data);
        var stableSince = Stopwatch.GetTimestamp();
        while (true)
        {
            await Task.Delay(5000);
            var now = Disk.Of(data);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
            samples.Add(new { t = Math.Round(elapsed), now.EventLog, now.Db, now.Cache, now.EventLogFiles, now.DbFiles, now.CacheFiles });
            if (now.Total > peak.Total) peak = now;
            if (last == null || now.EventLog != last.EventLog || now.Db != last.Db || now.Cache != last.Cache)
                stableSince = Stopwatch.GetTimestamp();
            last = now;
            Log($"  settle +{elapsed:F0}s: {now}");
            if (elapsed >= 75 && Stopwatch.GetElapsedTime(stableSince).TotalSeconds >= 20) break;
            if (elapsed >= 180) break;
        }
        return (last!, peak, samples);
    }

    // ---------------------------------------------------------------- write

    sealed record Phase(string Name, string Group, int PayloadBytes, int ItemsPerEvent, int Concurrency, long MaxEvents,
        TimeSpan MaxDuration);

    static readonly string[] Kinds = ["user_message", "assistant_message", "tool_call", "tool_result", "plan"];

    static string[] Payloads(int size)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 {}\":,.";
        var random = new Random(size);
        return Enumerable.Range(0, 256).Select(_ =>
            string.Create(size, random, (span, r) =>
            {
                for (var i = 0; i < span.Length; i++) span[i] = alphabet[r.Next(alphabet.Length)];
            })).ToArray();
    }

    const int SessionLength = 1000;
    static long _nextSession;

    public static async Task WriteAsync(string data, double scale, Dictionary<string, object?> report)
    {
        if (Directory.Exists(data)) Directory.Delete(data, true);
        Directory.CreateDirectory(data);
        var tuning = DefaultTuning;
        report["tuning"] = TuningReport(tuning);
        report["scale"] = scale;
        long N(long n) => Math.Max(100, (long)(n * scale));
        TimeSpan D(int seconds) => TimeSpan.FromSeconds(Math.Max(3, seconds * scale));
        var phases = new[]
        {
            new Phase("200B seq", "200B", 200, 1, 1, N(30_000), D(40)),
            new Phase("200B x32", "200B", 200, 1, 32, N(600_000), D(180)),
            new Phase("200B x256", "200B", 200, 1, 256, N(300_000), D(45)),
            new Phase("200B x32, 16 items/event", "200B", 200, 16, 32, N(25_000), D(60)),
            new Phase("2KiB seq", "2KiB", 2048, 1, 1, N(15_000), D(30)),
            new Phase("2KiB x32", "2KiB", 2048, 1, 32, N(150_000), D(90)),
            new Phase("16KiB seq", "16KiB", 16384, 1, 1, N(10_000), D(30)),
            new Phase("16KiB x32", "16KiB", 16384, 1, 32, N(40_000), D(90)),
        };

        var (host, node) = BuildHost(data, tuning);
        var t0 = Stopwatch.GetTimestamp();
        await host.StartAsync();
        var publisher = await node.Ready.WaitAsync(TimeSpan.FromMinutes(2));
        Log($"Ready on an empty database in {Ms(t0, Stopwatch.GetTimestamp()):F0} ms; {Disk.Of(data)}");
        report["emptyStartMs"] = Ms(t0, Stopwatch.GetTimestamp());
        var baseline = Disk.Of(data);
        report["baselineDisk"] = baseline.ToReport();

        // Warm-up: JIT, metadata topic registration of the event type.
        var warm = await RunPhaseAsync(publisher, new Phase("warm-up", "200B", 200, 1, 8, 2000, TimeSpan.FromSeconds(30)), []);
        Log($"Warm-up: {warm["events"]} events");

        var phaseReports = new List<object>();
        var groups = new List<object>();
        var sessions = new Dictionary<string, List<ulong>>();
        var groupStartDisk = baseline;
        long groupEvents = (long)warm["events"]!, groupItems = (long)warm["items"]!, groupPayload = (long)warm["payloadBytes"]!;
        long totalEvents = groupEvents, totalItems = groupItems;
        for (var i = 0; i < phases.Length; i++)
        {
            var phase = phases[i];
            var complete = new List<ulong>();
            var r = await RunPhaseAsync(publisher, phase, complete);
            sessions[phase.Name] = complete;
            r["diskAfter"] = Disk.Of(data).ToReport();
            phaseReports.Add(r);
            groupEvents += (long)r["events"]!;
            groupItems += (long)r["items"]!;
            groupPayload += (long)r["payloadBytes"]!;
            totalEvents += (long)r["events"]!;
            totalItems += (long)r["items"]!;
            Log($"{phase.Name}: {r["events"]} events in {r["seconds"]:F1} s = {r["eventsPerSecond"]:F0} ev/s, " +
                $"{r["itemsPerSecond"]:F0} items/s, {(double)r["mibPerSecond"]!:F1} MiB/s payload; latency ms p50 {r["p50Ms"]:F2} " +
                $"p95 {r["p95Ms"]:F2} p99 {r["p99Ms"]:F2} max {r["maxMs"]:F1}; cpu {r["cpuSeconds"]:F1} s; {Disk.Of(data)}");
            await File.WriteAllTextAsync(Path.Combine(data, "sessions.json"), JsonSerializer.Serialize(sessions));
            if (i + 1 < phases.Length && phases[i + 1].Group == phase.Group) continue;

            Log($"Settling after group {phase.Group} ...");
            var (settled, peak, samples) = await SettleAsync(data);
            var delta = new Disk(settled.EventLog - groupStartDisk.EventLog, settled.Db - groupStartDisk.Db,
                settled.Cache - groupStartDisk.Cache, 0, 0, 0, 0);
            groups.Add(new Dictionary<string, object?>
            {
                ["group"] = phase.Group, ["events"] = groupEvents, ["items"] = groupItems, ["payloadBytes"] = groupPayload,
                ["settled"] = settled.ToReport(), ["peak"] = peak.ToReport(), ["samples"] = samples,
                ["eventLogBytesPerItem"] = (double)delta.EventLog / groupItems,
                ["dbBytesPerItem"] = (double)delta.Db / groupItems,
                ["cacheBytesPerItem"] = (double)delta.Cache / groupItems,
                ["totalBytesPerItem"] = (double)delta.Total / groupItems,
                ["eventLogBytesPerPayloadByte"] = (double)delta.EventLog / groupPayload,
                ["totalBytesPerPayloadByte"] = (double)delta.Total / groupPayload
            });
            Log($"Group {phase.Group}: {groupItems} items, {Mb(groupPayload)} payload; per item: eventlog " +
                $"{(double)delta.EventLog / groupItems:F0} B, db {(double)delta.Db / groupItems:F0} B, cache " +
                $"{(double)delta.Cache / groupItems:F0} B; total/payload {(double)delta.Total / groupPayload:F2}; peak {peak}");
            groupStartDisk = settled;
            groupEvents = groupItems = groupPayload = 0;
        }
        report["phases"] = phaseReports;
        report["groups"] = groups;
        report["totalEvents"] = totalEvents;
        report["totalItems"] = totalItems;
        report["lastAppliedEventId"] = publisher.LastAppliedEventId;
        using (publisher.StartQuery()) report["sessionItemCount"] = Ambient.Relation<ISessionItemTable>().Count;
        report["finalDisk"] = Disk.Of(data).ToReport();
        report["gcHeapBytes"] = GC.GetTotalMemory(false);
        report["workingSetBytes"] = Environment.WorkingSet;
        var stop = Stopwatch.GetTimestamp();
        await host.StopAsync();
        host.Dispose();
        report["stopMs"] = Ms(stop, Stopwatch.GetTimestamp());
        Log($"Stopped in {report["stopMs"]:F0} ms; {Disk.Of(data)}");
    }

    static async Task<Dictionary<string, object?>> RunPhaseAsync(EventPublisher publisher, Phase phase, List<ulong> complete)
    {
        var payloads = Payloads(phase.PayloadBytes);
        long issued = 0;
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        var gen2 = GC.CollectionCount(2);
        var started = Stopwatch.GetTimestamp();
        var deadline = started + (long)(phase.MaxDuration.TotalSeconds * Stopwatch.Frequency);
        var errors = 0;
        string? firstError = null;
        var workers = Enumerable.Range(0, phase.Concurrency).Select(w => Task.Run(async () =>
        {
            var latencies = new List<long>(1024);
            var sessionId = (ulong)Interlocked.Increment(ref _nextSession);
            ulong seq = 0;
            var k = w * 7;
            while (Stopwatch.GetTimestamp() < deadline && Interlocked.Increment(ref issued) <= phase.MaxEvents)
            {
                var ev = new SessionItemsAppended { SessionId = sessionId };
                for (var j = 0; j < phase.ItemsPerEvent; j++, k++)
                    ev.Items.Add(new() { Seq = ++seq, Kind = Kinds[k % Kinds.Length], Payload = payloads[k & 255] });
                var before = Stopwatch.GetTimestamp();
                try
                {
                    await publisher.PublishAsync(ev);
                }
                catch (Exception error)
                {
                    if (Interlocked.Increment(ref errors) == 1) firstError = error.ToString();
                    continue;
                }
                latencies.Add(Stopwatch.GetTimestamp() - before);
                if (seq >= SessionLength)
                {
                    lock (complete) complete.Add(sessionId);
                    sessionId = (ulong)Interlocked.Increment(ref _nextSession);
                    seq = 0;
                }
            }
            return latencies;
        })).ToArray();
        var all = (await Task.WhenAll(workers)).SelectMany(l => l).ToArray();
        var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
        Array.Sort(all);
        double P(double q) => all.Length == 0 ? 0 : all[Math.Min(all.Length - 1, (int)(q * all.Length))] * 1000.0 / Stopwatch.Frequency;
        long events = all.Length, items = events * phase.ItemsPerEvent, payloadBytes = items * phase.PayloadBytes;
        if (firstError != null) Log($"{phase.Name}: {errors} publication errors, first: {firstError}");
        return new()
        {
            ["name"] = phase.Name, ["group"] = phase.Group, ["payloadBytes"] = payloadBytes,
            ["itemPayloadBytes"] = phase.PayloadBytes, ["itemsPerEvent"] = phase.ItemsPerEvent,
            ["concurrency"] = phase.Concurrency, ["events"] = events, ["items"] = items, ["errors"] = errors,
            ["firstError"] = firstError, ["seconds"] = elapsed,
            ["stoppedBy"] = events >= phase.MaxEvents ? "count" : "time",
            ["eventsPerSecond"] = events / elapsed, ["itemsPerSecond"] = items / elapsed,
            ["mibPerSecond"] = payloadBytes / elapsed / 1048576.0,
            ["p50Ms"] = P(0.50), ["p95Ms"] = P(0.95), ["p99Ms"] = P(0.99), ["p999Ms"] = P(0.999),
            ["maxMs"] = all.Length == 0 ? 0 : all[^1] * 1000.0 / Stopwatch.Frequency,
            ["cpuSeconds"] = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalSeconds,
            ["gen2Collections"] = GC.CollectionCount(2) - gen2, ["completeSessions"] = complete.Count
        };
    }

    // ---------------------------------------------------------------- restart

    public static async Task RestartAsync(string data, string drop, long mainStarted, Dictionary<string, object?> report)
    {
        if (!Directory.Exists(Path.Combine(data, "shared", "eventlog")))
            throw new InvalidOperationException("Run the write mode first.");
        var before = Disk.Of(data);
        report["diskBefore"] = before.ToReport();
        report["drop"] = drop;
        var dropStarted = Stopwatch.GetTimestamp();
        if (drop is "cache" or "rebuild") Directory.Delete(Path.Combine(data, "cache"), true);
        if (drop is "rebuild")
        {
            // Rebuild the database from the EventLog alone: no canonical checkpoint/TRL, no leader record.
            Directory.Delete(Path.Combine(data, "shared", "db"), true);
            Directory.Delete(Path.Combine(data, "shared", "leader"), true);
        }
        report["dropMs"] = Ms(dropStarted, Stopwatch.GetTimestamp());
        var tuning = DefaultTuning;
        report["tuning"] = TuningReport(tuning);

        var (host, node) = BuildHost(data, tuning);
        var built = Stopwatch.GetTimestamp();
        await host.StartAsync();
        var hostStarted = Stopwatch.GetTimestamp();
        var publisher = await node.Ready.WaitAsync(TimeSpan.FromMinutes(15));
        var ready = Stopwatch.GetTimestamp();
        var processToMain = (double)report["processStartToMainMs"]!;
        report["mainToHostBuiltMs"] = Ms(mainStarted, built);
        report["mainToHostStartedMs"] = Ms(mainStarted, hostStarted);
        report["restoreMs"] = node.RestoreFinished == 0 ? null : Ms(node.RestoreStarted, node.RestoreFinished);
        report["restoredHistory"] = node.Restored;
        report["publisherStartAsyncMs"] = Ms(node.PublisherStartBegan, node.PublisherStartFinished);
        report["mainToReadyMs"] = Ms(mainStarted, ready);
        report["processStartToReadyMs"] = processToMain + Ms(mainStarted, ready);
        report["lastAppliedEventId"] = publisher.LastAppliedEventId;
        Log($"Ready ({drop}): process start -> ready {report["processStartToReadyMs"]:F0} ms (restore " +
            $"{report["restoreMs"]:F0} ms, publisher StartAsync {report["publisherStartAsyncMs"]:F0} ms), " +
            $"last event {publisher.LastAppliedEventId}");

        var countStarted = Stopwatch.GetTimestamp();
        using (publisher.StartQuery()) report["sessionItemCount"] = Ambient.Relation<ISessionItemTable>().Count;
        report["countMs"] = Ms(countStarted, Stopwatch.GetTimestamp());

        var sessions = JsonSerializer.Deserialize<Dictionary<string, List<ulong>>>(
            await File.ReadAllTextAsync(Path.Combine(data, "sessions.json")))!;
        var queries = new List<object>();
        var random = new Random(42);
        foreach (var (phase, ids) in sessions)
        {
            if (ids.Count == 0) continue;
            var picked = ids.OrderBy(_ => random.Next()).Take(20).ToList();
            var cold = new List<double>();
            var warm = new List<double>();
            var page = new List<double>();
            var count = 0;
            foreach (var id in picked)
            {
                for (var pass = 0; pass < 2; pass++)
                {
                    var started = Stopwatch.GetTimestamp();
                    using (publisher.StartQuery())
                        count = Ambient.Relation<ISessionItemTable>().FindById(id).Count(i => i.Payload.Length > 0);
                    (pass == 0 ? cold : warm).Add(Ms(started, Stopwatch.GetTimestamp()));
                }
                var pageStarted = Stopwatch.GetTimestamp();
                using (publisher.StartQuery())
                    _ = Ambient.Relation<ISessionItemTable>()
                        .ListById(id, new AdvancedEnumeratorParam<ulong>(EnumerationOrder.Descending)).Take(50).ToList();
                page.Add(Ms(pageStarted, Stopwatch.GetTimestamp()));
            }
            var q = new Dictionary<string, object?>
            {
                ["phase"] = phase, ["sessions"] = picked.Count, ["itemsPerSession"] = count,
                ["firstQueryMs"] = cold[0], ["coldMedianMs"] = Median(cold), ["coldMaxMs"] = cold.Max(),
                ["warmMedianMs"] = Median(warm), ["last50MedianMs"] = Median(page)
            };
            queries.Add(q);
            Log($"Query {phase}: {count} items/session, first {cold[0]:F1} ms, cold median {Median(cold):F2} ms " +
                $"(max {cold.Max():F1}), warm median {Median(warm):F2} ms, last-50 page median {Median(page):F2} ms");
        }
        report["queries"] = queries;
        report["workingSetBytes"] = Environment.WorkingSet;
        var stop = Stopwatch.GetTimestamp();
        await host.StopAsync();
        host.Dispose();
        report["stopMs"] = Ms(stop, Stopwatch.GetTimestamp());
        report["diskAfter"] = Disk.Of(data).ToReport();
        Log($"Stopped in {report["stopMs"]:F0} ms; {Disk.Of(data)}");
    }

    static double Median(List<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }

    // ---------------------------------------------------------------- raw EventLog

    /// <summary>EventLog alone (no EventPublisher, no database): separates topic commit cost from consumption.</summary>
    public static async Task RawLogAsync(string directory, Dictionary<string, object?> report)
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
        builder.Services.AddSingleton<IEventLogStorage>(Storage(new FileSystemEventLogStorage(directory, flushToDisk: Fsync.Enabled)));
        builder.Services.AddBTDBEventLogSingleNode(new EventLogOptions());
        using var host = builder.Build();
        await host.StartAsync();
        var log = host.Services.GetRequiredService<IEventLog>();
        var runs = new List<object>();
        foreach (var (size, concurrency) in new[] { (200, 1), (200, 32), (200, 256), (2048, 32), (16384, 32) })
        {
            var topic = log.GetTopic($"raw-{size}-{concurrency}");
            var record = new byte[size];
            new Random(size).NextBytes(record);
            await topic.PublishAsync(record);
            var deadline = Stopwatch.GetTimestamp() + 10 * Stopwatch.Frequency;
            var started = Stopwatch.GetTimestamp();
            var latencies = (await Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => Task.Run(async () =>
            {
                var l = new List<long>();
                while (Stopwatch.GetTimestamp() < deadline)
                {
                    var before = Stopwatch.GetTimestamp();
                    await topic.PublishAsync(record);
                    l.Add(Stopwatch.GetTimestamp() - before);
                }
                return l;
            })))).SelectMany(l => l).Order().ToArray();
            var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            double P(double q) => latencies[Math.Min(latencies.Length - 1, (int)(q * latencies.Length))] * 1000.0 / Stopwatch.Frequency;
            runs.Add(new { size, concurrency, records = latencies.Length, perSecond = latencies.Length / seconds, p50Ms = P(0.5), p99Ms = P(0.99) });
            Log($"raw EventLog {size} B x{concurrency}: {latencies.Length / seconds:F0} rec/s, p50 {P(0.5):F2} ms, p99 {P(0.99):F2} ms");
        }
        report["runs"] = runs;
        await host.StopAsync();
    }

    // ---------------------------------------------------------------- retention

    public static async Task RetentionAsync(string directory, Dictionary<string, object?> report)
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        const string topicName = "test-retention";
        const long maxBytes = 16L << 20;
        const int recordBytes = 2048, records = 60_000, concurrency = 32;
        var options = new EventLogOptions
        {
            MergeInterval = TimeSpan.FromSeconds(2), DeletionDelay = TimeSpan.FromSeconds(5),
            Retention = topic => topic == topicName ? new EventLogRetention(MaxBytes: maxBytes) : null
        };
        report["retention"] = new { topicName, maxBytes, recordBytes, records, concurrency, mergeIntervalS = 2, deletionDelayS = 5 };
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
        builder.Services.AddSingleton<IEventLogStorage>(Storage(new FileSystemEventLogStorage(directory, flushToDisk: Fsync.Enabled)));
        builder.Services.AddBTDBEventLogSingleNode(options);
        using var host = builder.Build();
        await host.StartAsync();
        var topic = host.Services.GetRequiredService<IEventLog>().GetTopic(topicName);
        var record = new byte[recordBytes];
        new Random(1).NextBytes(record);
        var samples = new List<object>();
        long peak = 0, issued = 0;
        var started = Stopwatch.GetTimestamp();
        using var sampling = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                var bytes = DirBytes(directory);
                var bounds = await topic.GetBoundsAsync();
                peak = Math.Max(peak, bytes);
                samples.Add(new { t = Math.Round(Stopwatch.GetElapsedTime(started).TotalSeconds, 1), bytes, bounds.First, bounds.Next });
                try { await Task.Delay(1000, sampling.Token); }
                catch (OperationCanceledException) { }
            }
        });
        await Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => Task.Run(async () =>
        {
            while (Interlocked.Increment(ref issued) <= records) await topic.PublishAsync(record);
        })));
        var published = Stopwatch.GetElapsedTime(started).TotalSeconds;
        Log($"Retention: {records} x {recordBytes} B published in {published:F1} s ({records / published:F0} rec/s); size {Mb(DirBytes(directory))}");
        await Task.Delay(TimeSpan.FromSeconds(40));
        sampling.Cancel();
        await sampler;
        var finalBounds = await topic.GetBoundsAsync();
        var final = DirBytes(directory);
        report["publishSeconds"] = published;
        report["recordsPerSecond"] = records / published;
        report["peakBytes"] = peak;
        report["finalBytes"] = final;
        report["finalFirst"] = finalBounds.First;
        report["finalNext"] = finalBounds.Next;
        report["writtenBytes"] = (long)records * recordBytes;
        report["samples"] = samples;
        Log($"Retention: peak {Mb(peak)}, final {Mb(final)} for MaxBytes {Mb(maxBytes)}; bounds {finalBounds.First}..{finalBounds.Next}");
        await host.StopAsync();
    }
}

/// <summary>SPIKE_NO_FSYNC=1 runs with BTDB's development mode without disk flushes (BTDB 36.3.0).</summary>
static class Fsync
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("SPIKE_NO_FSYNC") != "1";
}
