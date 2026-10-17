using RimGovernor.Brain.Telemetry;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// Reads the fields of native's clock and colony event rows (otel-signals.md#clock-and-colony-events) the scenario cases
/// assert on. Numbers in a decoded row are doubles; absent fields read as null or empty.
/// </summary>
public static class ClockRows
{
    /// <summary>The harness's wall clock in Unix ms: the same clock native stamps <c>observed_at_unix_ms</c> with.</summary>
    public static long NowUnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Whether native observed the row's event at or after <paramref name="sinceUnixMs"/>.</summary>
    public static bool ObservedSince(LogRow row, long sinceUnixMs) =>
        Whole(row, ClockEventTelemetry.ObservedAtKey) is { } at && at >= sinceUnixMs;

    /// <summary>A hold row's reason in the scenario's lowercase form ("LetterPause" is "letter_pause").</summary>
    public static string Reason(LogRow row) => StopSummaries.ReasonName(Text(row, ClockEventTelemetry.ReasonKey));

    /// <summary>The row's event tick.</summary>
    public static long Tick(LogRow row) => Whole(row, ClockEventTelemetry.TickKey) ?? 0;

    /// <summary>The string field <paramref name="key"/>; empty when absent.</summary>
    public static string Text(LogRow row, string key)
    {
        ArgumentNullException.ThrowIfNull(row);
        return RowValue.Text(row.Payload.GetValueOrDefault(key));
    }

    /// <summary>The integer field <paramref name="key"/>; null when absent.</summary>
    public static long? Whole(LogRow row, string key)
    {
        ArgumentNullException.ThrowIfNull(row);
        return Whole(row.Payload.GetValueOrDefault(key));
    }

    /// <summary>The integer a decoded value holds; null when it is not a number.</summary>
    public static long? Whole(object? value) => value switch
    {
        double d => (long)d,
        long l => l,
        _ => null,
    };

    /// <summary>The string list field <paramref name="key"/> (fact families, entity ids); empty when absent.</summary>
    public static IReadOnlyList<string> Strings(LogRow row, string key)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Payload.GetValueOrDefault(key) is List<object?> list ? [.. list.Select(RowValue.Text)] : [];
    }

    /// <summary>The map field <paramref name="key"/> (an invalidation's cells); empty when absent.</summary>
    public static IReadOnlyDictionary<string, object?> Map(LogRow row, string key)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Payload.GetValueOrDefault(key) as Dictionary<string, object?> ?? new Dictionary<string, object?>();
    }
}
