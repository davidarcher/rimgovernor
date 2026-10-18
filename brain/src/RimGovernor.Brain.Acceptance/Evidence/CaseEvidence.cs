using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RimGovernor.Brain.Telemetry.Otlp;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// Puts the in-mod brain's evidence of one case into the case output as bounded text, so the remote exporter (which
/// carries only text diagnostics) publishes it (#3437): the lines of the game's OTLP log and trace rings written since
/// the case started, as <c>&lt;output&gt;/otlp/logs.jsonl</c> and <c>traces.jsonl</c>, and the store's
/// <c>need_methods</c> and <c>need_transitions</c> tables as <c>&lt;output&gt;/store/&lt;table&gt;.jsonl</c>, one row
/// per line. A file over its cap keeps its newest lines and begins with a <see cref="TruncatedKey"/> line saying what
/// was dropped. The metrics ring is not sliced: its instruments are cumulative per process, so a slice of a kept
/// process would charge the case with earlier cases' totals.
/// </summary>
public static class CaseEvidence
{
    /// <summary>The store dumps' directory under the case output.</summary>
    public const string StoreDir = "store";

    /// <summary>The member of the marker line a truncated file starts with.</summary>
    public const string TruncatedKey = "export_truncated";

    /// <summary>
    /// The log slice's cap: a served case writes about 1 MB of log records a minute, so 16 MiB holds a quarter-hour
    /// case whole and the end of a longer one.
    /// </summary>
    public const long LogCapBytes = 16L << 20;

    /// <summary>The trace slice's cap: spans (the brain's step and hop timing) run up to several times the log volume.</summary>
    public const long TraceCapBytes = 32L << 20;

    /// <summary>Each store dump's cap: the method and transition journals of a case are kilobytes; the cap only bounds a runaway.</summary>
    public const long StoreCapBytes = 4L << 20;

    // The store tables a failed case is diagnosed from; the store declares them in SQL only (StateMirror, Timelines).
    private static readonly string[] StoreTables = ["need_methods", "need_transitions"];

    // The wall-clock column (ms since the epoch) a store table's rows are windowed to the case by.
    private const string WallMsColumn = "wall_ms";

    private static readonly byte[] NanoField = "UnixNano\":\""u8.ToArray();

    /// <summary>
    /// Writes the case's brain evidence into <paramref name="output"/> from the game's channel directory
    /// <paramref name="gameDir"/> (its OTLP rings) and the store file the brain writes, <paramref name="store"/>: a
    /// kept game keeps the state path it was launched with, an earlier case's <c>service.sqlite</c>, so the caller
    /// passes the live path (<see cref="ServiceJournal.PathOf"/>), not the case's own. A missing store writes nothing.
    /// Returns what failed, empty when everything was written; when the brain ran (its store exists), a log ring with
    /// no line since the case started is a failure. A failure never fails the case.
    /// </summary>
    public static string Capture(string gameDir, string store, string output, DateTime since)
    {
        var errors = new List<string>();
        var served = File.Exists(store);
        var sinceNanos = (since.ToUniversalTime() - DateTime.UnixEpoch).Ticks * 100;
        foreach (var (signal, cap) in new[] { (OtlpSignal.Logs, LogCapBytes), (OtlpSignal.Traces, TraceCapBytes) })
        {
            try
            {
                var ring = OtlpFiles.RingPath(gameDir, signal);
                if (SliceRing(ring, OtlpFiles.RingPath(ServiceLogs.OtlpDir(output), signal), sinceNanos, since.ToUniversalTime(), cap) == 0 && served && signal == OtlpSignal.Logs)
                {
                    errors.Add($"{signal}: no line since the case started in {ring}");
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{signal}: {e.Message}");
            }
        }

        try
        {
            DumpStore(store, output, since);
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            errors.Add("store: " + e.Message);
        }

        return string.Join("; ", errors);
    }

    /// <summary>
    /// Copies the lines of the ring at <paramref name="ring"/> (with its rotated segments) whose latest timestamp is at
    /// or after <paramref name="sinceNanos"/> to <paramref name="target"/>, newest kept under <paramref name="cap"/>.
    /// A segment last written before <paramref name="since"/> holds nothing of the case and is not read. Returns how
    /// many lines were the case's, the dropped ones included.
    /// </summary>
    internal static long SliceRing(string ring, string target, long sinceNanos, DateTime since, long cap)
    {
        var kept = new Kept(cap);
        long lines = 0;
        OtlpRing.Scan(ring, since, (_, line) =>
        {
            if (LatestNanos(line.Span) >= sinceNanos)
            {
                kept.Add(line.ToArray());
                lines++;
            }
        });
        kept.Write(target);
        return lines;
    }

    /// <summary>The largest <c>*UnixNano</c> value on an OTLP/JSON line (a log record's time, a span's end), 0 when none.</summary>
    internal static long LatestNanos(ReadOnlySpan<byte> line)
    {
        long latest = 0;
        while (true)
        {
            var at = line.IndexOf(NanoField);
            if (at < 0)
            {
                return latest;
            }

            line = line[(at + NanoField.Length)..];
            if (Utf8Parser.TryParse(line, out long value, out _) && value > latest)
            {
                latest = value;
            }
        }
    }

    /// <summary>
    /// Dumps each of <see cref="StoreTables"/> from the store file <paramref name="store"/> to
    /// <c>&lt;output&gt;/store/&lt;table&gt;.jsonl</c>, oldest row first. A table with a <c>wall_ms</c> column
    /// (need_transitions) keeps only the rows written since <paramref name="since"/>; one without (need_methods) is
    /// dumped whole, so from a kept game's store it carries the earlier cases' rows too.
    /// </summary>
    internal static void DumpStore(string store, string output, DateTime since)
    {
        if (!File.Exists(store))
        {
            return;
        }

        var sinceMs = new DateTimeOffset(since.ToUniversalTime()).ToUnixTimeMilliseconds();
        var (db, note) = PostmortemStore.OpenRaw(store);
        if (db is null)
        {
            throw new IOException(note);
        }

        using (db)
        {
            foreach (var table in StoreTables)
            {
                if (!HasTable(db, table))
                {
                    continue;
                }

                // Newest first, so the cap keeps the end of the journal; Kept writes them back oldest first.
                var kept = new Kept(StoreCapBytes, newestFirst: true);
                using var cmd = db.CreateCommand();
                var window = HasColumn(db, table, WallMsColumn) ? $" WHERE {WallMsColumn} >= $since" : "";
                cmd.CommandText = $"SELECT * FROM {table}{window} ORDER BY rowid DESC";
                cmd.Parameters.AddWithValue("$since", sinceMs);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    kept.Add(RowJson(reader));
                }

                kept.Write(Path.Combine(output, StoreDir, table + ".jsonl"));
            }
        }
    }

    private static bool HasTable(SqliteConnection db, string table)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name";
        cmd.Parameters.AddWithValue("$name", table);
        return cmd.ExecuteScalar() is not null;
    }

    private static bool HasColumn(SqliteConnection db, string table, string column)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM pragma_table_info($table) WHERE name=$column";
        cmd.Parameters.AddWithValue("$table", table);
        cmd.Parameters.AddWithValue("$column", column);
        return cmd.ExecuteScalar() is not null;
    }

    private static byte[] RowJson(SqliteDataReader reader)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                switch (reader.IsDBNull(i) ? null : reader.GetValue(i))
                {
                    case null:
                        w.WriteNull(name);
                        break;
                    case long n:
                        w.WriteNumber(name, n);
                        break;
                    case double d:
                        w.WriteNumber(name, d);
                        break;
                    case byte[] bytes:
                        w.WriteString(name, Encoding.UTF8.GetString(bytes));
                        break;
                    case var other:
                        w.WriteString(name, Convert.ToString(other, CultureInfo.InvariantCulture));
                        break;
                }
            }

            w.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>The newest lines that fit a byte cap (newline included), and how many older ones were dropped.</summary>
    private sealed class Kept(long cap, bool newestFirst = false)
    {
        private readonly LinkedList<byte[]> _lines = new();
        private long _bytes;
        private long _droppedLines;
        private long _droppedBytes;

        public void Add(byte[] line)
        {
            var size = line.Length + 1L;
            if (newestFirst)
            {
                // Rows arrive newest first: once one does not fit, every older one is dropped.
                if (_droppedLines > 0 || _bytes + size > cap)
                {
                    _droppedLines++;
                    _droppedBytes += size;
                    return;
                }

                _lines.AddFirst(line);
                _bytes += size;
                return;
            }

            _lines.AddLast(line);
            _bytes += size;
            while (_bytes > cap && _lines.First is { } oldest)
            {
                _lines.RemoveFirst();
                _bytes -= oldest.Value.Length + 1L;
                _droppedLines++;
                _droppedBytes += oldest.Value.Length + 1L;
            }
        }

        public void Write(string target)
        {
            if (_lines.Count == 0 && _droppedLines == 0)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.Read);
            if (_droppedLines > 0)
            {
                file.Write(Encoding.UTF8.GetBytes(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{{\"{TruncatedKey}\":{{\"cap_bytes\":{cap},\"dropped_lines\":{_droppedLines},\"dropped_bytes\":{_droppedBytes},\"kept\":\"newest\"}}}}\n")));
            }

            foreach (var line in _lines)
            {
                file.Write(line);
                file.WriteByte((byte)'\n');
            }
        }
    }
}
