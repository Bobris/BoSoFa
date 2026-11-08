#!/bin/sh
# Runs the whole spike 01 benchmark from the repository root (about 27 minutes). Logs and JSON results go to
# artifacts/spikes/btdb-volume-results; data to artifacts/spikes/btdb-volume-data (deleted by each write run).
set -u
cd "$(dirname "$0")/../.."
dotnet build spikes/btdb-volume/BtdbVolume.csproj -c Release -v q || exit 1
B=./artifacts/spikes/bin/BtdbVolume/release/btdb-volume
L=artifacts/spikes/btdb-volume-results/logs
mkdir -p $L
# 0. EventLog alone (no EventPublisher), as configured and with storage writes offloaded.
$B rawlog > $L/rawlog.log 2>&1
$B rawlog --offload-writes > $L/rawlog-offload.log 2>&1
# 1. As configured in the server today (FileSystemEventLogStorage used directly), shortened: it is fsync-bound.
$B write --scale 0.25 > $L/write.log 2>&1
# 2. Same with event-log storage writes offloaded to the thread pool (see OffloadingEventLogStorage.cs).
$B write --offload-writes > $L/write-offload.log 2>&1
# 3. Restarts on the data of run 2, with the server's configuration (no offload).
$B restart --label warm1 > $L/restart-warm1.log 2>&1
$B restart --label warm2 > $L/restart-warm2.log 2>&1
$B restart --drop cache > $L/restart-cache.log 2>&1
$B restart --drop rebuild > $L/restart-rebuild.log 2>&1
$B restart --label after-rebuild > $L/restart-after-rebuild.log 2>&1
# 4. Retention on a separate topic.
$B retention --offload-writes > $L/retention-offload.log 2>&1
echo ALL DONE
