using System.Collections;

namespace RimGovernor.Brain.Telemetry.Otlp;

/// <summary>
/// A record's attributes by key, natively typed: string, bool, long, double, <c>List&lt;object?&gt;</c> (array values) and
/// <c>Dictionary&lt;string, object?&gt;</c> (kvlist values). Bytes values stay base64 text. An attribute with no value is absent.
/// </summary>
public sealed class OtlpAttrDictionary : IReadOnlyDictionary<string, object?>
{
    public static readonly OtlpAttrDictionary Empty = new([]);

    private readonly Dictionary<string, object?> _values;

    internal OtlpAttrDictionary(Dictionary<string, object?> values) => _values = values;

    public object? this[string key] => _values[key];

    public IEnumerable<string> Keys => _values.Keys;

    public IEnumerable<object?> Values => _values.Values;

    public int Count => _values.Count;

    public bool ContainsKey(string key) => _values.ContainsKey(key);

    public bool TryGetValue(string key, out object? value) => _values.TryGetValue(key, out value);

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>The string attribute <paramref name="key"/>, empty when absent or not a string.</summary>
    public string Str(string key) => _values.TryGetValue(key, out var v) && v is string s ? s : "";

    /// <summary>The integer attribute <paramref name="key"/>; a double with an integral value counts.</summary>
    public bool TryInt(string key, out long value)
    {
        switch (_values.GetValueOrDefault(key))
        {
            case long l:
                value = l;
                return true;
            case double d when d == Math.Floor(d) && d >= long.MinValue && d <= long.MaxValue:
                value = (long)d;
                return true;
            default:
                value = 0;
                return false;
        }
    }

    /// <summary>The numeric attribute <paramref name="key"/> as a double.</summary>
    public bool TryFloat(string key, out double value)
    {
        switch (_values.GetValueOrDefault(key))
        {
            case long l:
                value = l;
                return true;
            case double d:
                value = d;
                return true;
            default:
                value = 0;
                return false;
        }
    }

    /// <summary>The boolean attribute <paramref name="key"/>, false when absent.</summary>
    public bool Bool(string key) => _values.TryGetValue(key, out var v) && v is true;
}

/// <summary>The emitting process: service.name, service.instance.id (one per launch) and service.version.</summary>
public readonly record struct OtlpResource(string Service, string Instance, string Version);

/// <summary>One log record (a decision or event row, see otel-signals.md "OTEL mapping").</summary>
public sealed record OtlpLog
{
    public OtlpPosition Pos { get; init; }

    public OtlpResource Resource { get; init; }

    /// <summary>The record's own time in Unix nanoseconds (the row's "at"); the observed time when absent.</summary>
    public long TimeUnixNano { get; init; }

    public long ObservedUnixNano { get; init; }

    public string EventName { get; init; } = "";

    /// <summary>OTLP severity number: INFO 9, WARN 13, ERROR 17.</summary>
    public int Severity { get; init; }

    public string SeverityText { get; init; } = "";

    /// <summary>A string body; empty for none.</summary>
    public string Body { get; init; } = "";

    public string TraceId { get; init; } = "";

    public string SpanId { get; init; } = "";

    public OtlpAttrDictionary Attrs { get; init; } = OtlpAttrDictionary.Empty;

    public DateTimeOffset Time => OtlpTime.FromUnixNano(TimeUnixNano);
}

/// <summary>One span event.</summary>
public sealed record OtlpSpanEvent(long TimeUnixNano, string Name, OtlpAttrDictionary Attrs);

/// <summary>One finished span.</summary>
public sealed record OtlpSpan
{
    public const int StatusUnset = 0;
    public const int StatusOk = 1;
    public const int StatusError = 2;

    public OtlpPosition Pos { get; init; }

    public OtlpResource Resource { get; init; }

    public string TraceId { get; init; } = "";

    public string SpanId { get; init; } = "";

    /// <summary>Empty for a root.</summary>
    public string ParentSpanId { get; init; } = "";

    public string Name { get; init; } = "";

    /// <summary>OTLP span kind: 1 internal, 2 server, 3 client.</summary>
    public int Kind { get; init; }

    public long StartUnixNano { get; init; }

    public long EndUnixNano { get; init; }

    public int StatusCode { get; init; }

    public string StatusMessage { get; init; } = "";

    public OtlpAttrDictionary Attrs { get; init; } = OtlpAttrDictionary.Empty;

    public IReadOnlyList<OtlpSpanEvent> Events { get; init; } = [];

    public TimeSpan Duration => TimeSpan.FromTicks((EndUnixNano - StartUnixNano) / 100);
}

internal static class OtlpTime
{
    internal static DateTimeOffset FromUnixNano(long nanos) =>
        nanos == 0 ? default : DateTimeOffset.UnixEpoch.AddTicks(nanos / 100);
}
