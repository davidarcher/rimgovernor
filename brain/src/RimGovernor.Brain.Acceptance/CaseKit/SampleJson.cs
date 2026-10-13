using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// Loose reads of the timeline samples the serve-driven watch builds (Go <c>map[string]any</c> with typed values).
/// A sample is a <see cref="JsonObject"/>: counters are JSON numbers, plans an array of objects.
/// </summary>
internal static class SampleJson
{
    /// <summary>Reads a JSON number of any width (Go <c>asFloat</c>); false for anything that is not one.</summary>
    internal static bool TryFloat(JsonNode? value, out double number)
    {
        number = 0;
        if (value is not JsonValue json)
        {
            return false;
        }

        if (json.TryGetValue<double>(out var d))
        {
            number = d;
            return true;
        }

        if (json.TryGetValue<long>(out var l))
        {
            number = l;
            return true;
        }

        if (json.TryGetValue<ulong>(out var u))
        {
            number = u;
            return true;
        }

        if (json.TryGetValue<int>(out var i))
        {
            number = i;
            return true;
        }

        if (json.TryGetValue<decimal>(out var m))
        {
            number = (double)m;
            return true;
        }

        if (json.TryGetValue<float>(out var f))
        {
            number = f;
            return true;
        }

        return false;
    }

    /// <summary>A non-negative integer member (a tick, a revision); null when absent or not a number.</summary>
    internal static ulong? Tick(JsonNode? value) => TryFloat(value, out var n) && n >= 0 ? (ulong)n : null;

    /// <summary>An optional number as JSON (null stays null).</summary>
    internal static JsonNode? Num(double? value) => value is null ? null : JsonValue.Create(value.Value);

    /// <summary>An optional integer as JSON (null stays null).</summary>
    internal static JsonNode? Num(int? value) => value is null ? null : JsonValue.Create(value.Value);

    /// <summary>An optional unsigned integer as JSON (null stays null).</summary>
    internal static JsonNode? Num(ulong? value) => value is null ? null : JsonValue.Create(value.Value);

    /// <summary>The sample's int member (0 when absent).</summary>
    internal static int Count(JsonObject sample, string key) => TryFloat(sample[key], out var n) ? (int)n : 0;

    /// <summary>The sample's string members of an array member.</summary>
    internal static IReadOnlyList<string> Strings(JsonNode? value) => [.. value.AsSlice().Select(item => item.AsString())];

    /// <summary>The wall-clock stamp a sample's <c>at</c> member carries (RFC 3339 UTC).</summary>
    internal static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Parses a sample's <c>at</c>; the zero time when it is absent or malformed (Go discards the parse error too).</summary>
    internal static DateTimeOffset ParseStamp(JsonNode? value) =>
        DateTimeOffset.TryParse(value.AsString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? at : default;
}
