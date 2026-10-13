using System.Buffers;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace RimGovernor.Brain.Telemetry.Otlp;

/// <summary>
/// OTLP/JSON encoding for the local file sink. Each Encode method appends one complete, self-contained OTLP/JSON
/// request (ExportTraceServiceRequest, ExportMetricsServiceRequest or ExportLogsServiceRequest) carrying exactly one
/// span, one metric or one log record, and a newline, so a file is one request per line. The field names, int64 as
/// string and hex id rules follow the OTLP/JSON mapping
/// (https://opentelemetry.io/docs/specs/otlp/#json-protobuf-encoding); field order and omissions match what the Go
/// writer produced. One instance is used from one task.
/// </summary>
internal sealed class OtlpEncoder : IDisposable
{
    private static readonly JsonWriterOptions s_options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        SkipValidation = true,
    };

    private readonly ArrayBufferWriter<byte> _item = new(1024);
    private readonly Utf8JsonWriter _writer;
    private readonly byte[] _resource;
    private readonly int _maxAttributeBytes;
    private readonly Dictionary<(string Name, string Version), byte[]> _scopes = [];

    internal OtlpEncoder(OtlpFileConfig config)
    {
        _maxAttributeBytes = config.MaxAttributeBytes;
        _writer = new Utf8JsonWriter(_item, s_options);
        var resource = new List<KeyValuePair<string, object?>>();
        if (config.RunId.Length > 0)
        {
            resource.Add(new("service.instance.id", config.RunId));
        }
        resource.Add(new("service.name", config.Service));
        if (config.Version.Length > 0)
        {
            resource.Add(new("service.version", config.Version));
        }
        _writer.WriteStartObject();
        WriteAttributes("attributes", resource);
        _writer.WriteEndObject();
        _resource = TakeItem();
    }

    private byte[] TakeItem()
    {
        _writer.Flush();
        var bytes = _item.WrittenSpan.ToArray();
        _item.Clear();
        _writer.Reset(_item);
        return bytes;
    }

    private byte[] Scope(string name, string version)
    {
        if (_scopes.TryGetValue((name, version), out var cached))
        {
            return cached;
        }
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var w = new Utf8JsonWriter(buffer, s_options))
        {
            w.WriteStartObject();
            w.WriteString("name", Clean(name));
            if (version.Length > 0)
            {
                w.WriteString("version", Clean(version));
            }
            w.WriteEndObject();
        }
        var bytes = buffer.WrittenSpan.ToArray();
        _scopes[(name, version)] = bytes;
        return bytes;
    }

    // Appends {"<top>":[{"resource":R,"<scopeKey>":[{"scope":S,"<itemKey>":[ITEM]}]}]}\n around the item written
    // so far, then clears the item buffer for the next record.
    private void Complete(IBufferWriter<byte> dst, string top, string scopeKey, string itemKey, string scopeName, string scopeVersion)
    {
        _writer.Flush();
        Append(dst, "{\"" + top + "\":[{\"resource\":");
        dst.Write(_resource);
        Append(dst, ",\"" + scopeKey + "\":[{\"scope\":");
        dst.Write(Scope(scopeName, scopeVersion));
        Append(dst, ",\"" + itemKey + "\":[");
        dst.Write(_item.WrittenSpan);
        Append(dst, "]}]}]}\n");
        Reset();
    }

    private static void Append(IBufferWriter<byte> dst, string ascii)
    {
        var span = dst.GetSpan(ascii.Length);
        Encoding.UTF8.GetBytes(ascii, span);
        dst.Advance(ascii.Length);
    }

    private static string Nanos(DateTimeOffset t) => (((t.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks)) * 100).ToString(CultureInfo.InvariantCulture);

    private static string Nanos(DateTime utc) => ((utc.Ticks - DateTime.UnixEpoch.Ticks) * 100).ToString(CultureInfo.InvariantCulture);

    // --- values ---

    private void WriteAttributes(string property, IEnumerable<KeyValuePair<string, object?>> kvs)
    {
        var started = false;
        foreach (var (key, value) in kvs)
        {
            if (!started)
            {
                _writer.WriteStartArray(property);
                started = true;
            }
            WriteKeyValue(key, value);
        }
        if (started)
        {
            _writer.WriteEndArray();
        }
    }

    private void WriteKeyValue(string key, object? value)
    {
        _writer.WriteStartObject();
        _writer.WriteString("key", Clean(key));
        _writer.WritePropertyName("value");
        WriteValue(value);
        _writer.WriteEndObject();
    }

    private void WriteValue(object? v)
    {
        switch (v)
        {
            case null:
                _writer.WriteStartObject();
                _writer.WriteEndObject();
                break;
            case string s:
                WriteStringValue(s);
                break;
            case bool b:
                _writer.WriteStartObject();
                _writer.WriteBoolean("boolValue", b);
                _writer.WriteEndObject();
                break;
            case sbyte or byte or short or ushort or int or uint or long:
                WriteInt(Convert.ToInt64(v, CultureInfo.InvariantCulture));
                break;
            case ulong u:
                if (u > long.MaxValue)
                {
                    WriteStringValue(u.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    WriteInt((long)u);
                }
                break;
            case float f:
                WriteDouble(f);
                break;
            case double d:
                WriteDouble(d);
                break;
            case byte[] bytes:
                _writer.WriteStartObject();
                _writer.WriteString("bytesValue", Convert.ToBase64String(bytes));
                _writer.WriteEndObject();
                break;
            case IReadOnlyDictionary<string, object?> map:
                WriteKvlist(map);
                break;
            case IDictionary<string, object?> map:
                WriteKvlist(map.Select(p => p));
                break;
            case IEnumerable items:
                _writer.WriteStartObject();
                _writer.WriteStartObject("arrayValue");
                _writer.WriteStartArray("values");
                foreach (var item in items)
                {
                    WriteValue(item);
                }
                _writer.WriteEndArray();
                _writer.WriteEndObject();
                _writer.WriteEndObject();
                break;
            default:
                WriteStringValue(Convert.ToString(v, CultureInfo.InvariantCulture) ?? "");
                break;
        }
    }

    private void WriteKvlist(IEnumerable<KeyValuePair<string, object?>> map)
    {
        _writer.WriteStartObject();
        _writer.WriteStartObject("kvlistValue");
        _writer.WriteStartArray("values");
        foreach (var (key, value) in map)
        {
            WriteKeyValue(key, value);
        }
        _writer.WriteEndArray();
        _writer.WriteEndObject();
        _writer.WriteEndObject();
    }

    private void WriteStringValue(string s)
    {
        _writer.WriteStartObject();
        _writer.WriteString("stringValue", Clean(Truncate(s, _maxAttributeBytes)));
        _writer.WriteEndObject();
    }

    private void WriteInt(long n)
    {
        _writer.WriteStartObject();
        _writer.WriteString("intValue", n.ToString(CultureInfo.InvariantCulture));
        _writer.WriteEndObject();
    }

    // NaN and the infinities stay representable: JSON numbers cannot carry them, so they travel as their string form.
    private void WriteDouble(double d)
    {
        if (double.IsNaN(d))
        {
            WriteStringValue("NaN");
        }
        else if (double.IsInfinity(d))
        {
            WriteStringValue(d > 0 ? "+Inf" : "-Inf");
        }
        else
        {
            _writer.WriteStartObject();
            _writer.WriteNumber("doubleValue", d);
            _writer.WriteEndObject();
        }
    }

    // Caps a string at maxBytes of UTF-8 without splitting a character.
    internal static string Truncate(string s, int maxBytes)
    {
        if (s.Length * 3 <= maxBytes || Encoding.UTF8.GetByteCount(s) <= maxBytes)
        {
            return s;
        }
        var bytes = 0;
        var i = 0;
        while (i < s.Length)
        {
            var width = char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
            var cost = Encoding.UTF8.GetByteCount(s.AsSpan(i, width));
            if (bytes + cost > maxBytes)
            {
                break;
            }
            bytes += cost;
            i += width;
        }
        return s[..i];
    }

    // The writer rejects invalid UTF-16; a lone surrogate becomes U+FFFD, as encoding/json wrote it.
    private static string Clean(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsSurrogate(s[i]))
            {
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    i++;
                    continue;
                }
                return Repair(s);
            }
        }
        return s;
    }

    private static string Repair(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                sb.Append(s[i]).Append(s[i + 1]);
                i++;
            }
            else
            {
                sb.Append(char.IsSurrogate(s[i]) ? '�' : s[i]);
            }
        }
        return sb.ToString();
    }

    // --- logs ---

    private const int SeverityInfo = 9;
    private const int SeverityWarn = 13;
    private const int SeverityError = 17;

    /// <summary>Appends one ExportLogsServiceRequest line for <paramref name="r"/>.</summary>
    internal void EncodeLog(IBufferWriter<byte> dst, TelemetryRecord r)
    {
        var (number, text) = r.Severity switch
        {
            TelemetryLevel.Warn => (SeverityWarn, "WARN"),
            TelemetryLevel.Error => (SeverityError, "ERROR"),
            _ => (SeverityInfo, "INFO"),
        };
        _writer.WriteStartObject();
        _writer.WriteString("timeUnixNano", Nanos(r.Timestamp));
        _writer.WriteString("observedTimeUnixNano", Nanos(r.ObservedTimestamp));
        _writer.WriteNumber("severityNumber", number);
        _writer.WriteString("severityText", text);
        if (r.Body.Length > 0)
        {
            _writer.WritePropertyName("body");
            WriteStringValue(r.Body);
        }
        var started = false;
        foreach (var (key, value) in r.Attributes)
        {
            if (!started)
            {
                _writer.WriteStartArray("attributes");
                started = true;
            }
            WriteKeyValue(key, value);
        }
        if (r.Durable)
        {
            if (!started)
            {
                _writer.WriteStartArray("attributes");
                started = true;
            }
            WriteKeyValue(RowKeys.LogDurable, true);
        }
        if (started)
        {
            _writer.WriteEndArray();
        }
        if (r.TraceId.Length > 0)
        {
            _writer.WriteNumber("flags", 1);
            _writer.WriteString("traceId", r.TraceId);
            if (r.SpanId.Length > 0)
            {
                _writer.WriteString("spanId", r.SpanId);
            }
        }
        _writer.WriteString("eventName", Clean(r.Kind));
        _writer.WriteEndObject();
        Complete(dst, "resourceLogs", "scopeLogs", "logRecords", Spans.ScopePrefix + "telemetry", "");
    }


    // --- traces ---

    /// <summary>Appends one ExportTraceServiceRequest line for the finished <paramref name="a"/>.</summary>
    internal void EncodeSpan(IBufferWriter<byte> dst, Activity a)
    {
        var tracing = a.TraceId.ToHexString();
        var tags = a.TagObjects;
        _writer.WriteStartObject();
        _writer.WriteString("traceId", tracing);
        _writer.WriteString("spanId", a.SpanId.ToHexString());
        if (!string.IsNullOrEmpty(a.TraceStateString))
        {
            _writer.WriteString("traceState", Clean(a.TraceStateString));
        }
        if (a.ParentSpanId != default)
        {
            _writer.WriteString("parentSpanId", a.ParentSpanId.ToHexString());
        }
        if (a.ActivityTraceFlags != ActivityTraceFlags.None)
        {
            _writer.WriteNumber("flags", (int)a.ActivityTraceFlags);
        }
        _writer.WriteString("name", Clean(a.DisplayName));
        _writer.WriteNumber("kind", (int)a.Kind + 1);
        _writer.WriteString("startTimeUnixNano", Nanos(a.StartTimeUtc));
        _writer.WriteString("endTimeUnixNano", Nanos(a.StartTimeUtc + a.Duration));
        WriteAttributes("attributes", tags);
        var events = false;
        foreach (var e in a.Events)
        {
            if (!events)
            {
                _writer.WriteStartArray("events");
                events = true;
            }
            _writer.WriteStartObject();
            _writer.WriteString("timeUnixNano", Nanos(e.Timestamp));
            _writer.WriteString("name", Clean(e.Name));
            WriteAttributes("attributes", e.Tags);
            _writer.WriteEndObject();
        }
        if (events)
        {
            _writer.WriteEndArray();
        }
        var links = false;
        foreach (var l in a.Links)
        {
            if (!links)
            {
                _writer.WriteStartArray("links");
                links = true;
            }
            _writer.WriteStartObject();
            _writer.WriteString("traceId", l.Context.TraceId.ToHexString());
            _writer.WriteString("spanId", l.Context.SpanId.ToHexString());
            if (!string.IsNullOrEmpty(l.Context.TraceState))
            {
                _writer.WriteString("traceState", Clean(l.Context.TraceState));
            }
            if (l.Tags is not null)
            {
                WriteAttributes("attributes", l.Tags);
            }
            _writer.WriteEndObject();
        }
        if (links)
        {
            _writer.WriteEndArray();
        }
        switch (a.Status)
        {
            case ActivityStatusCode.Ok:
                _writer.WriteStartObject("status");
                _writer.WriteNumber("code", 1);
                _writer.WriteEndObject();
                break;
            case ActivityStatusCode.Error:
                _writer.WriteStartObject("status");
                if (!string.IsNullOrEmpty(a.StatusDescription))
                {
                    _writer.WriteString("message", Clean(a.StatusDescription));
                }
                _writer.WriteNumber("code", 2);
                _writer.WriteEndObject();
                break;
            default:
                break;
        }
        _writer.WriteEndObject();
        Complete(dst, "resourceSpans", "scopeSpans", "spans", a.Source.Name, a.Source.Version ?? "");
    }

    // --- metrics ---

    /// <summary>Appends one ExportMetricsServiceRequest line for <paramref name="m"/>.</summary>
    internal void EncodeMetric(IBufferWriter<byte> dst, MetricSnapshot m)
    {
        _writer.WriteStartObject();
        _writer.WriteString("name", Clean(m.Name));
        if (m.Description.Length > 0)
        {
            _writer.WriteString("description", Clean(m.Description));
        }
        if (m.Unit.Length > 0)
        {
            _writer.WriteString("unit", Clean(m.Unit));
        }
        _writer.WriteStartObject(m.Kind switch
        {
            MetricKind.Gauge => "gauge",
            MetricKind.Sum => "sum",
            _ => "histogram",
        });
        _writer.WriteStartArray("dataPoints");
        foreach (var p in m.Points)
        {
            _writer.WriteStartObject();
            WriteAttributes("attributes", p.Attributes);
            if (p.Start is { } start)
            {
                _writer.WriteString("startTimeUnixNano", Nanos(start));
            }
            _writer.WriteString("timeUnixNano", Nanos(p.Time));
            if (m.Kind == MetricKind.Histogram)
            {
                _writer.WriteString("count", p.Count.ToString(CultureInfo.InvariantCulture));
                _writer.WriteNumber("sum", p.Sum);
                _writer.WriteStartArray("bucketCounts");
                foreach (var c in p.Buckets)
                {
                    _writer.WriteStringValue(c.ToString(CultureInfo.InvariantCulture));
                }
                _writer.WriteEndArray();
                _writer.WriteStartArray("explicitBounds");
                foreach (var b in m.Bounds)
                {
                    _writer.WriteNumberValue(b);
                }
                _writer.WriteEndArray();
                if (p.Count > 0)
                {
                    _writer.WriteNumber("min", p.Min);
                    _writer.WriteNumber("max", p.Max);
                }
            }
            else if (m.IsInt)
            {
                _writer.WriteString("asInt", p.IntValue.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                _writer.WriteNumber("asDouble", double.IsFinite(p.Value) ? p.Value : 0);
            }
            _writer.WriteEndObject();
        }
        _writer.WriteEndArray();
        if (m.Kind != MetricKind.Gauge)
        {
            _writer.WriteNumber("aggregationTemporality", 2);
        }
        if (m.Kind == MetricKind.Sum)
        {
            _writer.WriteBoolean("isMonotonic", m.Monotonic);
        }
        _writer.WriteEndObject();
        _writer.WriteEndObject();
        Complete(dst, "resourceMetrics", "scopeMetrics", "metrics", m.Scope, m.ScopeVersion);
    }

    public void Dispose() => _writer.Dispose();

    /// <summary>Drops the item buffer a failed encode left half-written, so the next record starts clean.</summary>
    internal void Reset()
    {
        _item.Clear();
        _writer.Reset(_item);
    }
}
