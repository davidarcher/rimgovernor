using System.Globalization;
using System.Text.Json;

namespace RimGovernor.Brain.Telemetry.Otlp;

/// <summary>
/// Typed readers of the OTLP/JSON file rings (the line format is in docs/developers/contracts/otel-core.md).
/// Acceptance oracles, the postmortem and the CLI/launcher readers parse the files through these, never through
/// the raw JSON. Every reader skips torn or unparsable lines.
/// </summary>
public static class OtlpReader
{
    /// <summary>Calls <paramref name="onSpan"/> with every span of the traces ring at <paramref name="path"/>, oldest first.</summary>
    public static void ReadSpans(string path, Action<OtlpSpan> onSpan)
    {
        ArgumentNullException.ThrowIfNull(onSpan);
        OtlpRing.Scan(path, (pos, line) =>
        {
            using var doc = Parse(line);
            if (doc is null || !Items(doc.RootElement, "resourceSpans", "scopeSpans", "spans", out var groups))
            {
                return;
            }
            foreach (var (resource, element) in groups)
            {
                var status = element.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
                onSpan(new OtlpSpan
                {
                    Pos = pos,
                    Resource = resource,
                    TraceId = Str(element, "traceId"),
                    SpanId = Str(element, "spanId"),
                    ParentSpanId = Str(element, "parentSpanId"),
                    Name = Str(element, "name"),
                    Kind = Int(element, "kind"),
                    StartUnixNano = Nanos(element, "startTimeUnixNano"),
                    EndUnixNano = Nanos(element, "endTimeUnixNano"),
                    StatusCode = status.ValueKind == JsonValueKind.Object ? Int(status, "code") : 0,
                    StatusMessage = status.ValueKind == JsonValueKind.Object ? Str(status, "message") : "",
                    Attrs = Attrs(element, "attributes"),
                    Events = Array(element, "events").Select(e => new OtlpSpanEvent(Nanos(e, "timeUnixNano"), Str(e, "name"), Attrs(e, "attributes"))).ToList(),
                });
            }
        });
    }

    /// <summary>Calls <paramref name="onLog"/> with every log record of the logs ring at <paramref name="path"/>, oldest first.</summary>
    public static void ReadLogs(string path, Action<OtlpLog> onLog)
    {
        ArgumentNullException.ThrowIfNull(onLog);
        OtlpRing.Scan(path, (pos, line) =>
        {
            if (DecodeLog(pos, line.Span) is { } log)
            {
                onLog(log);
            }
        });
    }

    /// <summary>Decodes one <c>logs.jsonl</c> line, for a reader following a growing file; null when it holds no record.</summary>
    public static OtlpLog? DecodeLog(ReadOnlySpan<byte> line) => DecodeLog(default, line);

    private static OtlpLog? DecodeLog(OtlpPosition pos, ReadOnlySpan<byte> line)
    {
        using var doc = Parse(line.ToArray());
        // The sink writes one record per request, so a line holds at most one.
        if (doc is null || !Items(doc.RootElement, "resourceLogs", "scopeLogs", "logRecords", out var groups) || groups.Count == 0)
        {
            return null;
        }
        var (resource, element) = groups[0];
        var time = Nanos(element, "timeUnixNano");
        var observed = Nanos(element, "observedTimeUnixNano");
        return new OtlpLog
        {
            Pos = pos,
            Resource = resource,
            TimeUnixNano = time != 0 ? time : observed,
            ObservedUnixNano = observed,
            EventName = Str(element, "eventName"),
            Severity = Int(element, "severityNumber"),
            SeverityText = Str(element, "severityText"),
            Body = element.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object && body.TryGetProperty("stringValue", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString() ?? ""
                : "",
            TraceId = Str(element, "traceId"),
            SpanId = Str(element, "spanId"),
            Attrs = Attrs(element, "attributes"),
        };
    }

    /// <summary>
    /// Calls <paramref name="onMetric"/> with every metric export of the metrics ring at <paramref name="path"/>, oldest
    /// first. Exports are cumulative every 10 s and once at shutdown; the last export of a metric is its total.
    /// </summary>
    public static void ReadMetrics(string path, Action<OtlpMetricRecord> onMetric)
    {
        ArgumentNullException.ThrowIfNull(onMetric);
        OtlpRing.Scan(path, (pos, line) =>
        {
            using var doc = Parse(line);
            if (doc is null || !Items(doc.RootElement, "resourceMetrics", "scopeMetrics", "metrics", out var groups))
            {
                return;
            }
            foreach (var (resource, element) in groups)
            {
                OtlpMetricKind kind;
                JsonElement data;
                var monotonic = false;
                if (element.TryGetProperty("gauge", out data) && data.ValueKind == JsonValueKind.Object)
                {
                    kind = OtlpMetricKind.Gauge;
                }
                else if (element.TryGetProperty("sum", out data) && data.ValueKind == JsonValueKind.Object)
                {
                    kind = OtlpMetricKind.Sum;
                    monotonic = data.TryGetProperty("isMonotonic", out var m) && m.ValueKind == JsonValueKind.True;
                }
                else if (element.TryGetProperty("histogram", out data) && data.ValueKind == JsonValueKind.Object)
                {
                    kind = OtlpMetricKind.Histogram;
                }
                else
                {
                    continue;
                }
                onMetric(new OtlpMetricRecord
                {
                    Pos = pos,
                    Resource = resource,
                    Name = Str(element, "name"),
                    Unit = Str(element, "unit"),
                    Kind = kind,
                    Monotonic = monotonic,
                    Points = Array(data, "dataPoints").Select(DecodePoint).ToList(),
                });
            }
        });
    }

    private static OtlpPoint DecodePoint(JsonElement w)
    {
        var value = 0.0;
        if (w.TryGetProperty("asInt", out var asInt) && asInt.ValueKind == JsonValueKind.String
            && long.TryParse(asInt.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
        {
            value = n;
        }
        else if (w.TryGetProperty("asDouble", out var asDouble) && asDouble.ValueKind == JsonValueKind.Number)
        {
            value = asDouble.GetDouble();
        }
        return new OtlpPoint
        {
            Attrs = Attrs(w, "attributes"),
            StartUnixNano = Nanos(w, "startTimeUnixNano"),
            TimeUnixNano = Nanos(w, "timeUnixNano"),
            Value = value,
            Count = w.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.String
                && ulong.TryParse(c.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : 0,
            Sum = w.TryGetProperty("sum", out var sum) && sum.ValueKind == JsonValueKind.Number ? sum.GetDouble() : 0,
            Bounds = Array(w, "explicitBounds").Where(b => b.ValueKind == JsonValueKind.Number).Select(b => b.GetDouble()).ToList(),
            Buckets = Array(w, "bucketCounts")
                .Select(b => b.ValueKind == JsonValueKind.String && ulong.TryParse(b.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var k) ? k : 0)
                .ToList(),
        };
    }

    // --- JSON helpers ---

    private static JsonDocument? Parse(ReadOnlyMemory<byte> line)
    {
        try
        {
            return JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The (resource, item) pairs of one request: resourceX[].scopeX[].items[].
    private static bool Items(JsonElement root, string top, string scopeKey, string itemKey, out List<(OtlpResource Resource, JsonElement Item)> groups)
    {
        groups = [];
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        foreach (var rs in Array(root, top))
        {
            var resource = DecodeResource(rs.TryGetProperty("resource", out var r) ? r : default);
            foreach (var ss in Array(rs, scopeKey))
            {
                foreach (var item in Array(ss, itemKey))
                {
                    groups.Add((resource, item));
                }
            }
        }
        return true;
    }

    private static OtlpResource DecodeResource(JsonElement resource)
    {
        if (resource.ValueKind != JsonValueKind.Object)
        {
            return default;
        }
        var a = Attrs(resource, "attributes");
        return new OtlpResource(a.Str("service.name"), a.Str("service.instance.id"), a.Str("service.version"));
    }

    private static List<JsonElement> Array(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray()]
            : [];

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    private static long Nanos(JsonElement e, string name) =>
        long.TryParse(Str(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static OtlpAttrDictionary Attrs(JsonElement parent, string name) => new(DecodeAttrs(Array(parent, name)));

    private static Dictionary<string, object?> DecodeAttrs(IEnumerable<JsonElement> attrs)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var a in attrs)
        {
            if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("value", out var value) && TryDecode(value, out var decoded))
            {
                map[Str(a, "key")] = decoded;
            }
        }
        return map;
    }

    // An AnyValue as its natural type; false for one with no value.
    private static bool TryDecode(JsonElement v, out object? decoded)
    {
        decoded = null;
        if (v.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        if (v.TryGetProperty("stringValue", out var s) && s.ValueKind == JsonValueKind.String)
        {
            decoded = s.GetString();
            return true;
        }
        if (v.TryGetProperty("boolValue", out var b) && b.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            decoded = b.GetBoolean();
            return true;
        }
        if (v.TryGetProperty("intValue", out var i) && i.ValueKind == JsonValueKind.String)
        {
            if (long.TryParse(i.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                decoded = n;
                return true;
            }
            return false;
        }
        if (v.TryGetProperty("doubleValue", out var d) && d.ValueKind == JsonValueKind.Number)
        {
            decoded = d.GetDouble();
            return true;
        }
        if (v.TryGetProperty("arrayValue", out var arr) && arr.ValueKind == JsonValueKind.Object)
        {
            var list = new List<object?>();
            foreach (var e in Array(arr, "values"))
            {
                TryDecode(e, out var item);
                list.Add(item);
            }
            decoded = list;
            return true;
        }
        if (v.TryGetProperty("kvlistValue", out var kv) && kv.ValueKind == JsonValueKind.Object)
        {
            decoded = DecodeAttrs(Array(kv, "values"));
            return true;
        }
        if (v.TryGetProperty("bytesValue", out var bytes) && bytes.ValueKind == JsonValueKind.String)
        {
            decoded = bytes.GetString();
            return true;
        }
        return false;
    }
}
