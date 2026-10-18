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
    public static void ReadSpans(string path, Action<OtlpSpan> onSpan) => ReadSpans(path, null, onSpan);

    /// <summary>
    /// <see cref="ReadSpans(string, Action{OtlpSpan})"/> over only the lines that carry the instrumentation scope of
    /// <paramref name="component"/> (<see cref="Spans.Source"/>), skipping the rest unparsed (each line holds one span
    /// under its scope). Null reads every line.
    /// </summary>
    public static void ReadSpans(string path, string? component, Action<OtlpSpan> onSpan)
    {
        ArgumentNullException.ThrowIfNull(onSpan);
        var needle = component is null ? null : System.Text.Encoding.UTF8.GetBytes("\"" + Spans.ScopePrefix + component + "\"");
        OtlpRing.Scan(path, (pos, line) =>
        {
            if (needle is not null && line.Span.IndexOf(needle) < 0)
            {
                return;
            }

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
