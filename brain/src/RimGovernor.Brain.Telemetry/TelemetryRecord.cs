namespace RimGovernor.Brain.Telemetry;

/// <summary>
/// Log record attribute keys. All are prefixed with <see cref="AttrPrefix"/>; a row's own payload fields are
/// under <see cref="FieldPrefix"/>. The schema is what the log readers parse
/// (docs/developers/contracts/otel-signals.md, "Mapping").
/// </summary>
public static class RowKeys
{
    public const string AttrPrefix = "rimgovernor.";
    public const string FieldPrefix = AttrPrefix + "attr.";

    public const string LogKind = AttrPrefix + "kind";
    public const string LogComponent = AttrPrefix + "component";
    public const string LogTick = AttrPrefix + "tick";
    public const string LogRow = AttrPrefix + "row";
    public const string LogVerdict = AttrPrefix + "verdict";
    public const string LogReason = AttrPrefix + "reason";
    public const string LogTarget = AttrPrefix + "target";
    public const string LogDurMs = AttrPrefix + "dur_ms";

    public const string LogTruncated = AttrPrefix + "truncated";
    public const string LogOriginalBytes = AttrPrefix + "original_bytes";
    public const string LogSha256 = AttrPrefix + "sha256";
    public const string LogPreview = AttrPrefix + "preview";

    /// <summary>Marks a row the file sink exports and fsyncs at once.</summary>
    public const string LogDurable = AttrPrefix + "durable";

    /// <summary>The values of <see cref="LogRow"/>.</summary>
    public const string RowDecision = "decision";

    public const string RowEvent = "event";

    /// <summary>The row context keys a record's trace ids are read back under and that mod_log rows are written with.</summary>
    public const string TraceIdKey = "trace_id";

    public const string SpanIdKey = "span_id";
}

/// <summary>
/// One row as the sink receives it: an OTEL log record in the process's own types. Attribute values are
/// normalized to null, string, bool, long, double, <c>IReadOnlyList&lt;object?&gt;</c> (an OTLP array) or
/// <c>IReadOnlyDictionary&lt;string, object?&gt;</c> (a kvlist, keys in ordinal order).
/// </summary>
public sealed record TelemetryRecord
{
    public string Kind { get; init; } = "";

    public string Component { get; init; } = "";

    /// <summary><see cref="RowKeys.RowDecision"/> or <see cref="RowKeys.RowEvent"/>.</summary>
    public string Row { get; init; } = "";

    public TelemetryLevel Severity { get; init; }

    /// <summary>The record body: a decision's summary or an event's message; empty for none.</summary>
    public string Body { get; init; } = "";

    public DateTimeOffset Timestamp { get; init; }

    public DateTimeOffset ObservedTimestamp { get; init; }

    /// <summary>The trace id of the span the row was emitted under, empty for none.</summary>
    public string TraceId { get; init; } = "";

    /// <summary>The span id of the span the row was emitted under, empty for none.</summary>
    public string SpanId { get; init; } = "";

    /// <summary>The row must survive a machine crash: the sink exports and fsyncs its batch at once.</summary>
    public bool Durable { get; init; }

    /// <summary>The record attributes in emit order: the fixed <c>rimgovernor.*</c> keys, then the payload fields under <see cref="RowKeys.FieldPrefix"/> in ordinal key order.</summary>
    public IReadOnlyList<KeyValuePair<string, object?>> Attributes { get; init; } = [];
}

/// <summary>
/// Receives the rows. The production implementation is the OTLP file writer (#3083); tests record. With no sink
/// installed (<see cref="NullTelemetrySink"/>) emitting a row costs one <see cref="Enabled"/> call.
/// </summary>
public interface ITelemetrySink
{
    /// <summary>Whether a row of this severity and kind would be recorded.</summary>
    bool Enabled(TelemetryLevel severity, string kind);

    void Emit(TelemetryRecord record);
}

/// <summary>The sink of a process that never installs one: it records nothing.</summary>
public sealed class NullTelemetrySink : ITelemetrySink
{
    public static readonly NullTelemetrySink Instance = new();

    private NullTelemetrySink()
    {
    }

    public bool Enabled(TelemetryLevel severity, string kind) => false;

    public void Emit(TelemetryRecord record)
    {
    }
}

/// <summary>The context of one row.</summary>
public sealed record RowContext
{
    public TelemetryLevel Level { get; init; } = TelemetryLevel.Info;

    /// <summary>When the row happened; unset means now.</summary>
    public DateTimeOffset? At { get; init; }

    /// <summary>The game tick the row is stamped with; unset means the last observed tick, if any.</summary>
    public long? Tick { get; init; }

    public string Component { get; init; } = "";
}

/// <summary>
/// The scheduler's last-known game tick. Every row emitted afterwards carries it, so a row can be lined up with
/// the game's own clock. The scheduler and the clock poll call <see cref="Observe"/> on each status or page they
/// take. Safe for concurrent use.
/// </summary>
public sealed class TickClock
{
    private long _last = -1;

    /// <summary>Records the game tick the service last read.</summary>
    public void Observe(long tick) => Interlocked.Exchange(ref _last, tick);

    /// <summary>The last observed tick, and whether one was observed.</summary>
    public bool TryGet(out long tick)
    {
        tick = Interlocked.Read(ref _last);
        return tick >= 0;
    }
}
