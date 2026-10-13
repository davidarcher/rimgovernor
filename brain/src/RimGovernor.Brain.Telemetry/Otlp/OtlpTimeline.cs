using System.Globalization;

namespace RimGovernor.Brain.Telemetry.Otlp;

/// <summary>
/// One row read back from the log ring. Every decision and event row, and the bridge's native_call, native_request,
/// native_frame and native_poll rows, is an OTEL log record in <c>logs.jsonl</c> (docs/developers/contracts/otel-signals.md,
/// "Mapping"). <see cref="OtlpTimeline.FromLog"/> is the inverse of <see cref="TelemetryEmitter.EmitRow"/>.
/// </summary>
public sealed record TimelineRecord
{
    public string Kind { get; init; } = "";

    /// <summary>Numbers the rows in the order the reader met them (1-based).</summary>
    public long Sequence { get; init; }

    /// <summary>
    /// The emitting process's service.instance.id (a launch). A ring kept under a profile outlives the process that
    /// wrote it, so the rows of successive launches share a file and Run tells them apart.
    /// </summary>
    public string Run { get; init; } = "";

    /// <summary>The row's Unix time in seconds.</summary>
    public double WallTime { get; init; }

    /// <summary>level, at, component, tick (when the record had one) and the record's trace_id and span_id.</summary>
    public IReadOnlyDictionary<string, object?> Context { get; init; } = new Dictionary<string, object?>();

    /// <summary>
    /// A decision's payload is verdict, reason, target, dur_ms and attrs; an event's is msg (the record body) and its
    /// fields. All numbers are double, as a JSON-decoded row held them; arrays are <c>List&lt;object?&gt;</c> and kvlists
    /// <c>Dictionary&lt;string, object?&gt;</c>. A record whose payload was capped carries truncated, original_bytes,
    /// sha256 and preview beside the correlation fields that survived.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Payload { get; init; } = new Dictionary<string, object?>();
}

/// <summary>Reads the log ring as rows.</summary>
public static class OtlpTimeline
{
    /// <summary>The context key a row's component is under.</summary>
    public const string ComponentKey = "component";

    /// <summary>
    /// Reads the logs ring at <paramref name="path"/> (a <c>logs.jsonl</c>, with its rotated segments) as rows, oldest
    /// first. Sequence numbers are the records' order among the rows (1-based). A missing ring is an empty timeline.
    /// </summary>
    public static List<TimelineRecord> Read(string path)
    {
        var rows = new List<TimelineRecord>();
        OtlpReader.ReadLogs(path, log =>
        {
            if (FromLog(log) is { } row)
            {
                rows.Add(row with { Sequence = rows.Count + 1 });
            }
        });
        return rows;
    }

    /// <summary>Rebuilds a row from its log record; null for a record that is no row (no event name or kind).</summary>
    public static TimelineRecord? FromLog(OtlpLog l)
    {
        ArgumentNullException.ThrowIfNull(l);
        var kind = l.EventName.Length > 0 ? l.EventName : l.Attrs.Str(RowKeys.LogKind);
        if (kind.Length == 0)
        {
            return null;
        }
        var level = l.SeverityText.ToUpperInvariant();
        if (level.Length == 0)
        {
            level = l.Severity switch
            {
                >= 17 => "ERROR",
                >= 13 => "WARN",
                _ => "INFO",
            };
        }
        var context = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["level"] = level,
            ["at"] = Rfc3339Nano(l.TimeUnixNano),
        };
        if (l.Attrs.Str(RowKeys.LogComponent) is { Length: > 0 } component)
        {
            context[ComponentKey] = component;
        }
        if (l.Attrs.TryFloat(RowKeys.LogTick, out var tick))
        {
            context["tick"] = tick;
        }
        if (l.TraceId.Length > 0)
        {
            context[RowKeys.TraceIdKey] = l.TraceId;
        }
        if (l.SpanId.Length > 0)
        {
            context[RowKeys.SpanIdKey] = l.SpanId;
        }

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in l.Attrs)
        {
            if (key.StartsWith(RowKeys.FieldPrefix, StringComparison.Ordinal))
            {
                fields[key[RowKeys.FieldPrefix.Length..]] = Plain(value);
            }
        }
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (l.Attrs.Str(RowKeys.LogRow) == RowKeys.RowDecision)
        {
            payload[Decision.VerdictKey] = l.Attrs.Str(RowKeys.LogVerdict);
            payload[Decision.ReasonKey] = l.Attrs.Str(RowKeys.LogReason);
            payload[Decision.TargetKey] = l.Attrs.Str(RowKeys.LogTarget);
            l.Attrs.TryFloat(RowKeys.LogDurMs, out var dur);
            payload[Decision.DurMsKey] = dur;
            payload[Decision.AttrsKey] = fields;
        }
        else
        {
            foreach (var (key, value) in fields)
            {
                payload[key] = value;
            }
            if (l.Body.Length > 0)
            {
                payload["msg"] = l.Body;
            }
        }
        if (l.Attrs.Bool(RowKeys.LogTruncated))
        {
            payload["truncated"] = true;
            if (l.Attrs.TryFloat(RowKeys.LogOriginalBytes, out var bytes))
            {
                payload["original_bytes"] = bytes;
            }
            payload["sha256"] = l.Attrs.Str(RowKeys.LogSha256);
            payload["preview"] = l.Attrs.Str(RowKeys.LogPreview);
        }
        return new TimelineRecord
        {
            Kind = kind,
            Run = l.Resource.Instance,
            WallTime = l.TimeUnixNano / 1e9,
            Context = context,
            Payload = payload,
        };
    }

    // The JSON-decoded form rows hold: integers become double, arrays and kvlists are rebuilt.
    private static object? Plain(object? v) => v switch
    {
        long n => (double)n,
        List<object?> list => list.Select(Plain).ToList(),
        Dictionary<string, object?> map => map.ToDictionary(p => p.Key, p => Plain(p.Value), StringComparer.Ordinal),
        _ => v,
    };

    // Go's time.RFC3339Nano: UTC, a fraction without trailing zeros (none when zero), then Z.
    private static string Rfc3339Nano(long unixNanos)
    {
        var seconds = Math.DivRem(unixNanos, 1_000_000_000L, out var nanos);
        if (nanos < 0)
        {
            seconds--;
            nanos += 1_000_000_000L;
        }
        var at = DateTimeOffset.UnixEpoch.AddSeconds(seconds);
        var text = at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        if (nanos != 0)
        {
            text += "." + nanos.ToString("D9", CultureInfo.InvariantCulture).TrimEnd('0');
        }
        return text + "Z";
    }
}
