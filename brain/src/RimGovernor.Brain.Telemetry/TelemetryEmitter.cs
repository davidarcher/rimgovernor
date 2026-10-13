using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace RimGovernor.Brain.Telemetry;

/// <summary>
/// The service's structured event substrate: turns every event or decision into an OTEL-shaped row stamped with
/// the scheduler's last-known game tick (<see cref="EmitRow"/>, mapping in otel-signals.md) and hands it to the
/// sink. A row emitted while an <see cref="Activity"/> is current carries its trace and span ids natively and is
/// also an event on it. Call sites emit through <see cref="Event"/> and <see cref="Decide"/>; a row with no kind
/// is dropped. Safe for concurrent use when the sink is.
/// </summary>
public sealed class TelemetryEmitter(ITelemetrySink sink, TickClock ticks, TimeProvider time)
{
    /// <summary>Caps one row's encoded payload: a larger payload is replaced by a preview, its size and its hash.</summary>
    public const int PayloadCapBytes = 64 << 10;

    private const int PreviewBytes = 4 << 10;

    // The correlation keys that survive a truncation.
    private static readonly string[] s_correlation = ["request", "tool", "native_tool", "category", "timing"];

    private readonly ITelemetrySink _sink = sink;
    private readonly TickClock _ticks = ticks;
    private readonly TimeProvider _time = time;

    /// <summary>An emitter on the system clock; a fresh <see cref="TickClock"/> is the caller's to feed through <see cref="Ticks"/>.</summary>
    public TelemetryEmitter(ITelemetrySink sink)
        : this(sink, new TickClock(), TimeProvider.System)
    {
    }

    /// <summary>The clock the scheduler and the clock poll feed with each status or page they take.</summary>
    public TickClock Ticks => _ticks;

    /// <summary>Emits one telemetry row. The kind is a required parameter, so no call site can write a row that would be dropped.</summary>
    public void Event(TelemetryLevel level, string component, string kind, string message, IEnumerable<KeyValuePair<string, object?>>? attrs = null)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal) { ["msg"] = message };
        if (attrs is not null)
        {
            foreach (var (key, value) in attrs)
            {
                payload[key] = value;
            }
        }
        EmitRow(kind, new RowContext { Level = level, Component = component }, payload);
    }

    /// <summary>Emits <paramref name="d"/> as a decision row, stamped with the tick and the current trace.</summary>
    public void Decide(Decision d)
    {
        ArgumentNullException.ThrowIfNull(d);
        EmitRow(d.Kind, new RowContext { Level = d.Level, Component = d.Component }, d.Payload());
    }

    /// <summary>
    /// Writes one row as an OTEL log record and, when an activity is current and recording, as an event on it.
    /// A payload carrying the verdict key is a decision (verdict, reason, target, dur_ms, attrs), anything else
    /// an event whose msg is the record body and whose other fields are attributes.
    /// </summary>
    public void EmitRow(string kind, RowContext context, IReadOnlyDictionary<string, object?> payload) =>
        Emit(kind, context, payload, durable: false);

    /// <summary>
    /// <see cref="EmitRow"/> for a row that must survive a machine crash: its record is marked durable, so the
    /// file sink exports and fsyncs its batch at once (the slow-call marker, written before a call that may hang).
    /// </summary>
    public void EmitDurableRow(string kind, RowContext context, IReadOnlyDictionary<string, object?> payload) =>
        Emit(kind, context, payload, durable: true);

    private void Emit(string kind, RowContext context, IReadOnlyDictionary<string, object?> payload, bool durable)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(payload);
        if (kind.Length == 0 || !_sink.Enabled(context.Level, kind))
        {
            return;
        }
        var activity = Activity.Current;
        var at = context.At ?? _time.GetUtcNow();
        var decision = payload.ContainsKey(Decision.VerdictKey);
        var attrs = new List<KeyValuePair<string, object?>>(12 + payload.Count)
        {
            new(RowKeys.LogKind, kind),
            new(RowKeys.LogComponent, context.Component),
            new(RowKeys.LogRow, decision ? RowKeys.RowDecision : RowKeys.RowEvent),
        };
        var tick = context.Tick.GetValueOrDefault();
        if (context.Tick is not null || _ticks.TryGet(out tick))
        {
            attrs.Add(new(RowKeys.LogTick, tick));
        }

        string body;
        IReadOnlyDictionary<string, object?> fields;
        if (decision)
        {
            var verdict = payload[Decision.VerdictKey] as string ?? "";
            var reason = payload.GetValueOrDefault(Decision.ReasonKey) as string ?? "";
            var target = payload.GetValueOrDefault(Decision.TargetKey) as string ?? "";
            var dur = payload.GetValueOrDefault(Decision.DurMsKey) is double d ? d : 0;
            attrs.Add(new(RowKeys.LogVerdict, verdict));
            attrs.Add(new(RowKeys.LogReason, reason));
            attrs.Add(new(RowKeys.LogTarget, target));
            attrs.Add(new(RowKeys.LogDurMs, dur));
            body = new Decision { Kind = kind, Verdict = verdict, Reason = reason, Target = target }.Summary();
            fields = RowValues.Normalize(payload.GetValueOrDefault(Decision.AttrsKey)) as IReadOnlyDictionary<string, object?>
                ?? new Dictionary<string, object?>();
        }
        else
        {
            body = payload.GetValueOrDefault("msg") as string ?? "";
            var rest = new Dictionary<string, object?>(payload.Count, StringComparer.Ordinal);
            foreach (var (key, value) in payload)
            {
                if (key != "msg")
                {
                    rest[key] = RowValues.Normalize(value);
                }
            }
            fields = rest;
        }

        var encoded = RowJson.Encode(fields);
        if (encoded is not null && encoded.Length > PayloadCapBytes)
        {
            attrs.Add(new(RowKeys.LogTruncated, true));
            attrs.Add(new(RowKeys.LogOriginalBytes, (long)encoded.Length));
            attrs.Add(new(RowKeys.LogSha256, Convert.ToHexStringLower(SHA256.HashData(encoded))));
            attrs.Add(new(RowKeys.LogPreview, Encoding.UTF8.GetString(encoded, 0, PreviewBytes)));

            // The correlation keys survive the truncation.
            var kept = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var key in s_correlation)
            {
                if (fields.TryGetValue(key, out var value) && Correlatable(value))
                {
                    kept[key] = value;
                }
            }
            fields = kept;
        }
        foreach (var key in fields.Keys.Order(StringComparer.Ordinal))
        {
            attrs.Add(new(RowKeys.FieldPrefix + key, fields[key]));
        }

        var traceId = "";
        var spanId = "";
        if (activity is not null && activity.TraceId != default)
        {
            traceId = activity.TraceId.ToHexString();
            spanId = activity.SpanId.ToHexString();
        }
        _sink.Emit(new TelemetryRecord
        {
            Kind = kind,
            Component = context.Component,
            Row = decision ? RowKeys.RowDecision : RowKeys.RowEvent,
            Severity = context.Level,
            Body = body,
            Timestamp = at,
            ObservedTimestamp = _time.GetUtcNow(),
            TraceId = traceId,
            SpanId = spanId,
            Durable = durable,
            Attributes = attrs,
        });

        if (activity is { IsAllDataRequested: true })
        {
            activity.AddEvent(new ActivityEvent(kind, at, SpanTags(attrs)));
        }
    }

    // The small scalar fields (and one flat map of them) that survive truncation.
    private static bool Correlatable(object? value)
    {
        switch (value)
        {
            case string s:
                return Encoding.UTF8.GetByteCount(s) <= 256;
            case long or double:
                return true;
            case IReadOnlyDictionary<string, object?> map:
                if (map.Count > 16)
                {
                    return false;
                }
                foreach (var inner in map.Values)
                {
                    if (inner is IReadOnlyDictionary<string, object?> || !Correlatable(inner))
                    {
                        return false;
                    }
                }
                return true;
            default:
                return false;
        }
    }

    // A span event carries scalars as they are; a list or map field is carried as its JSON text, since span tags are flat.
    private static ActivityTagsCollection SpanTags(List<KeyValuePair<string, object?>> attrs)
    {
        var tags = new ActivityTagsCollection();
        foreach (var (key, value) in attrs)
        {
            tags[key] = value switch
            {
                IReadOnlyDictionary<string, object?> or IReadOnlyList<object?> =>
                    Encoding.UTF8.GetString(RowJson.Encode(value) ?? []),
                _ => value,
            };
        }
        return tags;
    }
}
