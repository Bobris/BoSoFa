# Spike 01: session transcript items as BTDB events (M0 ①)

**Question.** Can BoSoFa persist agent-session transcript items as BTDB EventSourcing domain events, as planned in
[plan §4.2](../plan.md#42-what-goes-where)? This spike measures single-node mode on local file-system storage:
throughput, latency, disk growth, cold restart and query latency.

**Answer.** Yes, with three conditions.

1. Single-node mode as wired today (`FileSystemEventLogStorage` passed directly) commits **one record per storage
   write**. The whole node is then capped at **~83 events/s**, however many publishers there are. The cause is a
   BTDB problem with storage that completes synchronously. A small wrapper storage (below) lifts the cap 30x.
2. Every commit costs about 11 ms on this Mac (two `F_FULLFSYNC`s). One stream that awaits each publish therefore
   gets about 85 events/s. The way to get throughput is to put **many items in one event** and to have many
   concurrent publishers.
3. Disk use is about **3x the payload** on one node: event log, canonical database and local cache each hold about
   one copy. Merges add a temporary ~1.4x on top of the event log. The whole database is memory-mapped.

## Setup

| | |
|---|---|
| Machine | MacBook Pro, Apple M2 Max (12 cores), 64 GiB RAM, internal SSD, APFS, macOS 26.6.2 (25G83) |
| Disk sync | `F_FULLFSYNC` 4.2 ms, plain `fsync` 0.02 ms (200-byte write + sync, Python, 200 iterations). .NET `Flush(true)` uses `F_FULLFSYNC` on macOS. |
| Runtime | .NET SDK 10.0.403, runtime 10.0.14, Release build, server GC (like `Microsoft.NET.Sdk.Web`) |
| BTDB | 36.2.0 NuGet: `BTDB`, `BTDB.Replication`, `BTDB.Replication.EventSourcing`, `BTDB.Replication.Http`, `BTDB.SourceGenerator` |
| Hosting | Generic host, without Kestrel. `SpikeNode` is a copy of `src/BoSoFa.Server/Btdb/BtdbNode.cs`, with the same `ServerApp` registrations: `AddBTDBReplicationSingleNode`, `AddBTDBEventLogSingleNode`, `FileSystemLeaderStorage`/`FileSystemReplicationStorage`/`FileSystemEventLogStorage`, `ReplicationFileSet.RestoreAsync`, one `EventPublisher` over topics `data`/`metadata`, `NoCompressionStrategy`, `EventPublisherOptions.MaximumBatchSize` 32 (default) |
| Tuning (spike only, to let disk use settle) | EventLog `MergeInterval` 5 s, `DeletionDelay` 20 s. Replication checkpoint 30 s, deletion delay 20 s, compaction 30 s. Production defaults are 30 s / 1 day / 5 min (server) / 1 day / 5 min. |
| Event | `SessionItemsAppended : PureEvent { SessionId, List<SessionItemData{Seq, Kind, Payload string}> }`. The handler upserts `SessionItem` keyed `(SessionId, Seq)` with `Kind`, `Payload`, `When`, `EventId`. Payloads are random ASCII strings of exactly 200 / 2048 / 16384 chars. A session has 1,000 items (1,008 with 16 items/event). |
| Latency | Time until `PublishAsync` completes, which covers durable data-topic append plus local consumption. |

Code: `spikes/btdb-volume/` (`run.sh` runs everything in about 27 minutes). Raw results and logs:
`artifacts/spikes/btdb-volume-results/` (JSON per run, `logs/`). Data: `artifacts/spikes/btdb-volume-data/` (3.5 GiB
after the run).

## Results

### Publish throughput and latency

**A. As configured in the server today** (`write.json`). The run was shortened (`--scale 0.25`) because it is
rate-bound. 13,866 events and 32,721 items, no errors.

| Phase | Events | ev/s | items/s | p50 ms | p95 ms | p99 ms | max ms |
|---|---:|---:|---:|---:|---:|---:|---:|
| 200 B, sequential | 833 | 83 | 83 | 11.2 | 18.1 | 20.8 | 26 |
| 200 B, 32 publishers | 3,779 | 83 | 83 | 382 | 424 | 440 | 453 |
| 200 B, 256 publishers | 1,189 | 83 | 83 | 3,027 | 3,186 | 3,195 | 3,203 |
| 200 B, 32 publishers, 16 items/event | 1,257 | 82 | 1,306 | 391 | 424 | 483 | 498 |
| 2 KiB, sequential | 616 | 82 | 82 | 11.5 | 19.0 | 21.6 | 23 |
| 2 KiB, 32 publishers | 1,872 | 82 | 82 | 392 | 417 | 504 | 529 |
| 16 KiB, sequential | 591 | 79 | 79 | 11.7 | 19.4 | 25.2 | 73 |
| 16 KiB, 32 publishers | 1,729 | 75 | 75 | 415 | 553 | 643 | 657 |

**B. Event-log storage writes moved to the thread pool** (`--offload-writes`, `write-offload.json`, full scale).
959,074 events and 1,334,074 items (1.14 M of them 200 B items), no errors, about 10 minutes of publishing.

| Phase | Events | ev/s | items/s | payload MiB/s | p50 ms | p95 ms | p99 ms | max ms | CPU s |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 200 B, sequential | 3,290 | 82 | 82 | 0.02 | 11.5 | 18.3 | 21.7 | 32 | 11.9 |
| 200 B, 32 publishers | 438,003 | 2,433 | 2,433 | 0.5 | 12.0 | 19.9 | 28.1 | 128 | 139.5 |
| 200 B, 256 publishers | 300,000 | 11,835 | 11,835 | 2.3 | 20.5 | 37.7 | 76.1 | 199 | 27.8 |
| 200 B, 32 publishers, 16 items/event | 25,000 | 1,613 | **25,802** | 4.9 | 19.0 | 33.3 | 110.6 | 225 | 12.2 |
| 2 KiB, sequential | 2,368 | 79 | 79 | 0.2 | 11.7 | 19.0 | 23.9 | 137 | 8.9 |
| 2 KiB, 32 publishers | 150,000 | 1,792 | 1,792 | 3.5 | 12.4 | 32.8 | 86.9 | 238 | 56.5 |
| 16 KiB, sequential | 2,278 | 76 | 76 | 1.2 | 11.8 | 20.8 | 28.9 | 82 | 9.8 |
| 16 KiB, 32 publishers | 36,135 | 401 | 401 | 6.3 | 68.3 | 159.3 | 386.5 | 666 | 61.3 |

**C. EventLog alone, without EventPublisher** (`rawlog*.json`, 10 s per row, records/s):

| Record, publishers | As configured | Writes offloaded |
|---|---:|---:|
| 200 B, 1 | 94 (p50 10.6 ms) | 90 (p50 11.0 ms) |
| 200 B, 32 | 91 (p50 340 ms) | 2,784 (p50 11.2 ms) |
| 200 B, 256 | 91 (p50 2.8 s) | 20,119 (p50 12.0 ms) |
| 2 KiB, 32 | 93 | 2,520 |
| 16 KiB, 32 | 90 | 921 (p50 33 ms) |

### Disk growth (run B, sizes after merges, checkpoints and deletions settled)

Item sizes are payload sizes. "Per item" is the settled growth of each directory over the group, divided by the
group's items.

| Group (items, payload) | Event log B/item | Canonical db B/item | Local cache B/item | Total / payload |
|---|---:|---:|---:|---:|
| 200 B (1,143,293 items in 768,293 events, 218 MiB) | 243 | 249 | 239 | 3.65 |
| 2 KiB (152,368, 298 MiB) | 2,105 | 2,099 | 2,088 | 3.07 |
| 16 KiB (38,413, 600 MiB) | 16,445 | 16,438 | 16,426 | 3.01 |
| **Final** (1,334,074 items, 1,116 MiB payload) | 1,173 MiB | 1,178 MiB | 1,166 MiB | 3.15 (3.5 GiB) |

- Fixed overhead is about 40–50 bytes per item per copy. It covers kind, keys, `When`/`EventId`, the publication
  GUID and framing, and is smaller per item when items are batched.
- The event log temporarily holds merged objects next to the splits they cover, until `DeletionDelay` passes. Peaks
  were 482 MiB against 278 MiB settled for the 200 B group, and 1,655 MiB against 1,173 MiB at the end. With the
  production `DeletionDelay` of 1 day, this extra copy lasts a day.
- The local cache held a single `00000001.trl` (1.2 GB) plus a 90-byte KVI. Nothing was ever deleted, so
  compaction had nothing to reclaim. The canonical store (`shared/db`) is a TRL copy plus small KVIs.
- Memory: after run B the GC heap was 470 MB and the working set 1.16 GB. On restart the working set was 1.18 GB,
  and during the rebuild from the event log 2.5 GB. `OnDiskReplicationFileStorage` memory-maps the database files,
  so the resident size follows the database size.

### Cold restart and queries (on run B's data: 959,074 events, 3.5 GiB)

| Restart | Process start → publisher ready | Restore | Publisher `StartAsync` |
|---|---:|---:|---:|
| Warm (same directories), 1st | 873 ms | 721 ms | 70 ms |
| Warm, 2nd | 895 ms | 742 ms | 67 ms |
| Local cache deleted (restore from canonical) | 865 ms | 662 ms | 68 ms |
| **Rebuild from the event log** (cache, canonical db and leader deleted; replays all 959,074 events) | **5,768 ms** | 22 ms | 5,455 ms |
| Warm restart right after the rebuild | 2,075 ms | 1,913 ms | 71 ms |

- Process start to `Main` took 33 ms. All restarts had all 1,334,074 items (`Count` < 1 ms).
- The rebuild replayed about 176 k events/s (245 k items/s, ~200 MiB/s). The files were in the OS page cache, so
  this is the best case for this machine.
- The restart right after the rebuild was slower. The process stopped about 0.4 s after the rebuild, before a new
  checkpoint was published.
- Restoring after deleting the cache copies a local directory, which is cheap on APFS. On Blob it would be a
  download.

Query latency is median over 20 sessions per phase, each with a fresh `publisher.StartQuery()` +
`Ambient.Relation<ISessionItemTable>()`. Read-all is `FindById(sessionId)` over 1,000 items. The page is
`ListById(sessionId, Descending).Take(50)`.

| Items | Read all 1,000, first query of process | Read all, median | Last 50, median |
|---|---:|---:|---:|
| 200 B | 4.0 ms | 1.2–1.6 ms | 0.07–0.11 ms |
| 2 KiB | 2.9 ms | 2.9–3.4 ms | 0.15 ms |
| 16 KiB | 18–25 ms | 4.7–22 ms | 0.2–0.9 ms |

After the rebuild (freshly written, resident pages) the same queries took 0.5 / 0.9–1.5 / 4–5 ms.

### Retention on a separate topic (`retention-offload.json`)

Topic `test-retention`, `Retention = new EventLogRetention(MaxBytes: 16 MiB)`, `MergeInterval` 2 s,
`DeletionDelay` 5 s. Input was 60,000 × 2 KiB records (117 MiB) from 32 publishers, at 2,237 records/s.

- The directory peaked at 93 MiB while publishing, because whole sealed objects (merged level-1 objects of
  ~4 MiB) are condemned one pass at a time. It settled at **14.5 MiB** within 10 s after publishing ended.
- `First` advanced to 52,609 (of 60,000) while publishing. The data and metadata topics were not involved.
- Retention works as documented. The bound is approximate while writes go faster than the merge pass.

## Bottlenecks and anomalies

1. **No group commit with `FileSystemEventLogStorage` (BTDB issue, worth reporting upstream).**
   - All writes of `FileSystemEventLogStorage` complete synchronously: full split rewrite + `Flush(true)`, metadata
     write + `Flush(true)`, rename, all under a lock.
   - `EventLogService.SubmitLocalAsync` hands a round's records to the owner lane one at a time. The first
     `lane.SubmitAsync` kicks `EventLogOwnerLane.RunAsync`, which then runs the whole commit inline on the
     submitting thread. Each record is therefore committed in its own write before the next one is enqueued.
   - Result: 83–94 records/s whatever the concurrency. Latency grows linearly with the number of publishers
     (3 s at 256).
   - Moving the storage writes to the thread pool (`spikes/btdb-volume/OffloadingEventLogStorage.cs`, 30 lines) gives
     2.8 k/s with 32 publishers and 20 k/s with 256, with p50 back at ~11–12 ms.
   - Azure storage really is asynchronous, so cluster mode should not hit this. The BoSoFa dev server does.
2. **Per-commit floor of ~11 ms.** One event-log commit means two `F_FULLFSYNC`s (~4.2 ms each) plus the rewrite.
   A publisher that awaits each event can never exceed ~85 events/s, in either mode.
3. **Large records amplify writes.** Every commit rewrites the whole current split (up to `SplitCap` 256 KiB), in
   the file-system store as in a Put Blob. With 16 KiB items, only about 15 records fit in a split. Throughput
   drops to 401 events/s (6.3 MiB/s) and p99 to 387 ms, compared with 1.8 k/s for 2 KiB.
4. **Three copies on one disk** in single-node mode, plus a temporary extra event-log copy during merges, kept for
   `DeletionDelay`.
5. **Resident memory follows database size** (memory-mapped files). Plan pod memory for the whole `SessionItem`
   relation, or keep that relation small.
6. No crashes, exceptions or publication errors in any run. Every run stopped cleanly within 30 ms.

## Recommendation for §4.2

- **Keep session items as domain events → `SessionItem (SessionId, Seq)`.** Volume, replay and query cost are fine:
  - 1.3 M items replay in 5.5 s.
  - Reading a 1,000-item session takes a few ms.
  - Paging the last 50 items takes well under 1 ms.
- **Batch items per event.** Name the event `SessionItemsAppended` and carry all items of one daemon outbox flush
  (the 50 ms batch in §5.3) for one session, up to about **64 items or 128 KiB serialized**, whichever comes first.
  - This keeps each event well below one 256 KiB split.
  - 16 items/event gave 10x the items/s of 1 item/event at the same concurrency (25.8 k vs 2.4 k).
  - Do not await one event per item in the ingestion path: that path is capped at ~85/s per stream by the fsync floor.
  - `Consume` stays idempotent by the `(SessionId, Seq)` upsert. Dedup on `(machine, stream, seq)` as planned.
- **Lower the inline-payload threshold from 64 KiB to 16 KiB per item**, and move anything larger to the
  content-addressed blob store. Store the hash, size, MIME type and a short text preview (≤ 2 KiB) inline.
  - Items above 16 KiB cost write amplification in the event log (anomaly 3).
  - They are also stored three times, and kept resident through the memory-mapped database.
  - Typical items (messages, tool-call headers, plans, diff summaries) are well under 16 KiB.
  - Large tool outputs, file contents and full diffs belong in blobs anyway.
- **Never persist streaming deltas**, as planned. With ~3x disk per payload byte, token-level persistence is out of
  the question.
- **Sizing rule of thumb:**
  - Single node: about 3.1x the persisted payload on disk, plus up to 1.4x of the event log for one `DeletionDelay`.
  - Cluster: event log and canonical database on Blob (2x), plus one local cache copy per pod.
  - Example: 10,000 sessions × 2,000 items × 2 KiB is about 40 GiB of payload, so about 120 GiB on one node and a
    memory-mapped database of ~40 GiB whose touched pages stay resident. Revisit retention of old sessions (archive transcripts to blobs, delete rows)
    before reaching that.
- **BTDB options:**
  - Single node: register an async wrapper around `FileSystemEventLogStorage`, like the spike's
    `OffloadingEventLogStorage`, in `ServerApp`, until BTDB fixes the synchronous-storage path. Report the issue to
    BTDB with the explanation above.
  - Keep `EventLogOptions` defaults otherwise. `MaxRecordSize`/`MaximumEventSize` 51 MiB is far above what the
    batching rule produces.
  - Consider lowering `EventLogOptions.DeletionDelay` for the dev server (minutes). Keep 1 day in the cluster.
  - Keep `data`/`metadata` at `Retention` forever (AGENTS.md). `EventLogRetention(MaxBytes/MaxAge)` behaves well for
    auxiliary topics (presence, notifications).
  - `EventPublisherOptions.MaximumBatchSize` 32 was not the bottleneck and was not varied.
- **Not covered here:** Azure Blob latency and cluster mode (M2), a sustained run longer than 10 minutes, and
  `MaximumBatchSize` or `SplitCap` variations.
