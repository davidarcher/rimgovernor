using System.Globalization;
using RimGovernor.Brain.Telemetry;
using RimGovernor.Brain.Telemetry.Otlp;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// One row of a service's own journal of what it did (planner_step, dispatch, native_* rows; see the Telemetry
/// project), read from its OTLP log ring (<c>otlp/logs.jsonl</c>, rebuilt by <see cref="OtlpTimeline.FromLog"/>).
/// <see cref="Tick"/> is the game tick the row was stamped with, when the service knew one. <see cref="Sequence"/>
/// numbers rows in the order the reader met them. Numbers in <see cref="Context"/> and <see cref="Payload"/> are
/// <see cref="double"/>, as a JSON-decoded row held them.
/// </summary>
public sealed record LogRow
{
    public ulong Sequence { get; init; }

    public string Kind { get; init; } = "";

    public long Tick { get; init; }

    public bool HasTick { get; init; }

    public IReadOnlyDictionary<string, object?> Context { get; init; } = new Dictionary<string, object?>();

    public IReadOnlyDictionary<string, object?> Payload { get; init; } = new Dictionary<string, object?>();

    /// <summary>Builds the row of a timeline record.</summary>
    public static LogRow From(TimelineRecord record, ulong sequence)
    {
        ArgumentNullException.ThrowIfNull(record);
        var row = new LogRow { Sequence = sequence, Kind = record.Kind, Context = record.Context, Payload = record.Payload };
        if (record.Context.TryGetValue("tick", out var tick) && tick is double number)
        {
            row = row with { Tick = (long)number, HasTick = true };
        }

        return row;
    }

    /// <summary>The row as the timeline reader holds it, for the kind helpers (<see cref="RowKinds"/>).</summary>
    public TimelineRecord Record() => new() { Kind = Kind, Sequence = (long)Sequence, Context = Context, Payload = Payload };

    /// <summary>The row's data as one flat map (Go <c>bridge.RowFields</c>): a decision row's attrs with its verdict, reason, target and dur_ms.</summary>
    public IReadOnlyDictionary<string, object?> Fields()
    {
        if (!Payload.ContainsKey(Decision.VerdictKey))
        {
            return Payload;
        }

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in new[] { Decision.VerdictKey, Decision.ReasonKey, Decision.TargetKey, Decision.DurMsKey })
        {
            if (Payload.TryGetValue(key, out var value))
            {
                fields[key] = value;
            }
        }

        if (Payload.TryGetValue(Decision.AttrsKey, out var attrs) && attrs is Dictionary<string, object?> map)
        {
            foreach (var (key, value) in map)
            {
                fields[key] = value;
            }
        }

        return fields;
    }

    /// <summary>The string context entry <paramref name="key"/>, empty when absent.</summary>
    public string ContextString(string key) => RowValue.Text(Context.GetValueOrDefault(key));
}

/// <summary>Loose reads of the decoded values a <see cref="LogRow"/> holds (Go <c>AsString</c>/<c>AsNumber</c> over <c>any</c>).</summary>
internal static class RowValue
{
    /// <summary>The string a value holds; empty when it is not a string.</summary>
    internal static string Text(object? value) => value as string ?? "";

    /// <summary>The number a value holds; -1 when it is not numeric.</summary>
    internal static double Number(object? value) => value switch
    {
        double d => d,
        long l => l,
        int i => i,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => -1,
    };
}

/// <summary>The row-kind predicates of Go <c>bridge/rows.go</c> that the evidence readers use.</summary>
public static class RowKinds
{
    /// <summary>The target of the clock worker's per-step planner_step row.</summary>
    public const string WorkerStepTarget = "worker_step";

    /// <summary>Whether the row is the clock worker's per-step planner_step row.</summary>
    public static bool IsWorkerStep(LogRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Kind == "planner_step" && RowValue.Text(row.Payload.GetValueOrDefault(Decision.TargetKey)) == WorkerStepTarget;
    }

    /// <summary>
    /// Whether the row is a dispatch row that recorded an outcome change (attr changed): the service reconciled an
    /// action to a new outcome (a stage change, a completion, a failure), the moment a watch's sample is most likely
    /// to have changed.
    /// </summary>
    public static bool WorkerOutcome(LogRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Kind == "dispatch" && row.Fields().GetValueOrDefault("changed") is true;
    }
}

/// <summary>
/// Follows a service's OTLP log ring from where it left off, so a harness can wake on what the service just did
/// instead of polling its state on a timer. Each <see cref="Next"/> opens the active segment (logs.jsonl), reads
/// the complete lines appended since the previous call and closes it again: the file is never held open, so the
/// sink's rotation (a close-and-rename) is not blocked on Windows. A rotation between two reads is detected as the
/// file shrinking; the rows that landed in the rotated segment are skipped, which a caller polling anyway
/// tolerates. The sink batches for up to 250 ms, so a row is visible that much after the service logged it.
/// </summary>
public sealed class LogTail
{
    private readonly string _path;
    private long _offset;
    private ulong _seq;

    private LogTail(string path, long offset)
    {
        _path = path;
        _offset = offset;
    }

    /// <summary>Starts a tail at the end of <paramref name="path"/> (what the service wrote before now is history, not a wake), or at the start when the file does not exist yet.</summary>
    public static LogTail Create(string path) => new(path, File.Exists(path) ? new FileInfo(path).Length : 0);

    /// <summary>Starts a tail at the first row of <paramref name="path"/>: it returns every row the active file holds on its first <see cref="Next"/>.</summary>
    public static LogTail FromStart(string path) => new(path, 0);

    /// <summary>The rows appended since the previous call. A missing file is not an error (the service has not opened its sink yet); a corrupt line is skipped.</summary>
    public IReadOnlyList<LogRow> Next()
    {
        FileStream file;
        try
        {
            file = OpenShared(_path);
        }
        catch (FileNotFoundException)
        {
            return [];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }

        using (file)
        {
            var size = file.Length;
            if (size < _offset)
            {
                _offset = 0;
            }

            if (size == _offset)
            {
                return [];
            }

            file.Seek(_offset, SeekOrigin.Begin);
            var data = new byte[size - _offset];
            file.ReadExactly(data);

            // A partial trailing line is a write in progress; it is read whole next time.
            var complete = Array.LastIndexOf(data, (byte)'\n') + 1;
            var rows = new List<LogRow>();
            var start = 0;
            while (start < complete)
            {
                var end = Array.IndexOf(data, (byte)'\n', start);
                if (ParseLogRow(data.AsSpan(start, end - start)) is { } row)
                {
                    _seq++;
                    rows.Add(row with { Sequence = _seq });
                }

                start = end + 1;
            }

            _offset += complete;
            return rows;
        }
    }

    /// <summary>Rebuilds the row of one logs.jsonl line (null for a line that is no row).</summary>
    public static LogRow? ParseLogRow(ReadOnlySpan<byte> line)
    {
        var trimmed = System.Text.Encoding.UTF8.GetString(line).Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (OtlpReader.DecodeLog(System.Text.Encoding.UTF8.GetBytes(trimmed)) is not { } log)
        {
            return null;
        }

        return OtlpTimeline.FromLog(log) is { } record ? LogRow.From(record, 0) : null;
    }

    /// <summary>
    /// The last row of the log ring file at <paramref name="path"/> that <paramref name="match"/> accepts, with its
    /// JSON line. <paramref name="tail"/> &gt; 0 reads only that many trailing bytes (the first, possibly partial,
    /// line is dropped); 0 reads the whole segment. Null when no row matches or the file cannot be read.
    /// </summary>
    public static (LogRow Row, string Line)? LastLogRow(string path, long tail, Func<LogRow, bool> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        byte[] data;
        var skipFirst = false;
        try
        {
            using var file = OpenShared(path);
            var start = 0L;
            if (tail > 0 && file.Length > tail)
            {
                start = file.Length - tail;
                skipFirst = true;
            }

            file.Seek(start, SeekOrigin.Begin);
            data = new byte[file.Length - start];
            var read = file.Read(data, 0, data.Length);
            data = data[..read];
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        (LogRow, string)? last = null;
        var offset = 0;
        while (offset < data.Length)
        {
            var end = Array.IndexOf(data, (byte)'\n', offset);
            if (end < 0)
            {
                end = data.Length;
            }

            if (skipFirst)
            {
                skipFirst = false;
            }
            else if (ParseLogRow(data.AsSpan(offset, end - offset)) is { } row && match(row))
            {
                last = (row, System.Text.Encoding.UTF8.GetString(data, offset, end - offset).Trim());
            }

            offset = end + 1;
        }

        return last;
    }

    private static FileStream OpenShared(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        Share = FileShare.ReadWrite | FileShare.Delete,
    });
}
