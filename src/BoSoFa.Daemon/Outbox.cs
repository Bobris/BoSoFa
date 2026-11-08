using System.Text.Json;
using System.Text.Json.Serialization;
using BoSoFa.Protocol;

namespace BoSoFa.Daemon;

/// <summary>
/// Durable reports waiting for the server's acknowledgement (plan §5.3). Records are appended as JSON lines to segment
/// files and flushed to disk before <see cref="Append"/> returns. Each session has its own gapless seq. Acknowledged
/// seqs are kept in acked.json, written before any segment is deleted, so seqs never repeat after a restart. A torn
/// last line from a crash is skipped; appending always starts a new segment after opening.
/// </summary>
public sealed class Outbox : IDisposable
{
    const long SegmentLimit = 1024 * 1024;

    readonly string _directory;
    readonly object _lock = new();
    readonly List<Entry> _entries = [];
    readonly Dictionary<ulong, long> _acked;
    readonly Dictionary<ulong, long> _last = new();
    readonly Dictionary<int, int> _liveBySegment = new();
    FileStream? _segment;
    int _segmentNumber;
    long _position;

    public sealed record Entry(long Position, int Segment, OutboxRecord Record);

    /// <summary>Raised after each append, outside the lock.</summary>
    public event Action? Appended;

    public Outbox(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        _acked = File.Exists(AckedPath)
            ? JsonSerializer.Deserialize(File.ReadAllText(AckedPath), OutboxJsonContext.Default.DictionaryUInt64Int64) ?? []
            : [];
        foreach (var (sessionId, seq) in _acked) _last[sessionId] = seq;
        foreach (var (number, path) in Segments())
        {
            _segmentNumber = Math.Max(_segmentNumber, number);
            foreach (var line in File.ReadLines(path))
            {
                OutboxRecord? record;
                try { record = JsonSerializer.Deserialize(line, ProtocolJsonContext.Default.OutboxRecord); }
                catch (JsonException) { continue; } // torn write at a crash
                if (record == null) continue;
                _last[record.SessionId] = Math.Max(_last.GetValueOrDefault(record.SessionId), record.Seq);
                if (record.Seq <= _acked.GetValueOrDefault(record.SessionId)) continue;
                _entries.Add(new(++_position, number, record));
                _liveBySegment[number] = _liveBySegment.GetValueOrDefault(number) + 1;
            }
            if (!_liveBySegment.ContainsKey(number)) File.Delete(path);
        }
    }

    string AckedPath => Path.Combine(_directory, "acked.json");
    string SegmentPath(int number) => Path.Combine(_directory, $"{number:D8}.jsonl");

    IEnumerable<(int Number, string Path)> Segments() => Directory.EnumerateFiles(_directory, "*.jsonl")
        .Select(path => (Number: int.TryParse(Path.GetFileNameWithoutExtension(path), out var n) ? n : -1, Path: path))
        .Where(segment => segment.Number >= 0).OrderBy(segment => segment.Number);

    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    /// <summary>Stores a report durably and returns its seq.</summary>
    public long Append(ulong sessionId, SessionReport report)
    {
        long seq;
        lock (_lock)
        {
            seq = _last.GetValueOrDefault(sessionId) + 1;
            var record = new OutboxRecord(sessionId, seq, report);
            if (_segment == null || _segment.Length > SegmentLimit)
            {
                if (_segment != null)
                {
                    _segment.Dispose();
                    if (_liveBySegment.GetValueOrDefault(_segmentNumber) == 0)
                    {
                        _liveBySegment.Remove(_segmentNumber);
                        File.Delete(SegmentPath(_segmentNumber));
                    }
                }
                _segment = new FileStream(SegmentPath(++_segmentNumber), FileMode.CreateNew,
                    FileAccess.Write, FileShare.Read);
            }
            var line = JsonSerializer.SerializeToUtf8Bytes(record, ProtocolJsonContext.Default.OutboxRecord);
            _segment.Write(line);
            _segment.WriteByte((byte)'\n');
            _segment.Flush(true);
            _last[sessionId] = seq;
            _entries.Add(new(++_position, _segmentNumber, record));
            _liveBySegment[_segmentNumber] = _liveBySegment.GetValueOrDefault(_segmentNumber) + 1;
        }
        Appended?.Invoke();
        return seq;
    }

    /// <summary>Unacknowledged records after <paramref name="position"/>, oldest first, at most about
    /// <paramref name="maximumBytes"/> of JSON (always at least one).</summary>
    public IReadOnlyList<Entry> After(long position, int maximumCount, int maximumBytes)
    {
        lock (_lock)
        {
            var result = new List<Entry>();
            var bytes = 0;
            foreach (var entry in _entries)
            {
                if (entry.Position <= position) continue;
                bytes += EstimatedSize(entry.Record);
                if (result.Count > 0 && (result.Count >= maximumCount || bytes > maximumBytes)) break;
                result.Add(entry with { Record = entry.Record with { Acked = _acked.GetValueOrDefault(entry.Record.SessionId) } });
            }
            return result;
        }
    }

    static int EstimatedSize(OutboxRecord record) => record.Report switch
    {
        ItemReport item => 256 + item.Text.Length + (item.Data?.Length ?? 0),
        ApprovalRequestedReport approval => 256 + approval.Input.Length,
        _ => 256
    };

    public void Ack(IEnumerable<OutboxAck> acks)
    {
        lock (_lock)
        {
            var changed = false;
            foreach (var ack in acks)
                if (ack.Seq > _acked.GetValueOrDefault(ack.SessionId))
                {
                    _acked[ack.SessionId] = ack.Seq;
                    changed = true;
                }
            if (!changed) return;
            var temporary = AckedPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_acked, OutboxJsonContext.Default.DictionaryUInt64Int64));
            File.Move(temporary, AckedPath, true);
            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                var entry = _entries[i];
                if (entry.Record.Seq > _acked.GetValueOrDefault(entry.Record.SessionId)) continue;
                _entries.RemoveAt(i);
                if (--_liveBySegment[entry.Segment] > 0 || entry.Segment == _segmentNumber && _segment != null) continue;
                _liveBySegment.Remove(entry.Segment);
                File.Delete(SegmentPath(entry.Segment));
            }
        }
    }

    /// <summary>Drops what is still waiting for a session the server no longer has. Seqs keep counting, so they never
    /// repeat.</summary>
    public void Retire(ulong sessionId)
    {
        lock (_lock) Ack([new(sessionId, _last.GetValueOrDefault(sessionId))]);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _segment?.Dispose();
            _segment = null;
        }
    }
}

[JsonSerializable(typeof(Dictionary<ulong, long>))]
[JsonSerializable(typeof(Dictionary<string, DateTime>))]
internal sealed partial class OutboxJsonContext : JsonSerializerContext;
