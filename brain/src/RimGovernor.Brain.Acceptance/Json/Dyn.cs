using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// Dynamic-JSON navigation for the documents the harness handles as JSON: fixture-op replies, recorded evidence, the
/// report and the service API (a wire reply is read as its typed Contracts record instead). These helpers navigate
/// loosely, with explicit results in place of bare casts.
/// </summary>
public static class Dyn
{
    /// <summary>The value as an object; null when it is anything else.</summary>
    public static JsonObject? AsMap(this JsonNode? value) => value as JsonObject;

    /// <summary>The value as an array; empty when it is anything else.</summary>
    public static IReadOnlyList<JsonNode?> AsSlice(this JsonNode? value) => value is JsonArray array ? [.. array] : [];

    /// <summary>The string a JSON string holds; empty when the value is not a string.</summary>
    public static string AsString(this JsonNode? value)
    {
        if (value is JsonValue json && json.TryGetValue<string>(out var text))
        {
            return text;
        }

        return "";
    }

    /// <summary>The boolean a JSON boolean holds; null when the value is not one.</summary>
    public static bool? AsBool(this JsonNode? value)
    {
        if (value is JsonValue json && json.TryGetValue<bool>(out var flag))
        {
            return flag;
        }

        return null;
    }

    /// <summary>
    /// The number as a double; anything that is not a JSON number (a numeric string included) is -1, as in Go.
    /// </summary>
    public static double AsNumber(this JsonNode? value)
    {
        if (value is not JsonValue json)
        {
            return -1;
        }

        if (json.TryGetValue<double>(out var d))
        {
            return d;
        }

        if (json.TryGetValue<long>(out var l))
        {
            return l;
        }

        return -1;
    }

    /// <summary>A non-negative JSON integer; null for anything else (a numeric string included).</summary>
    public static ulong? AsUInt64(this JsonNode? value)
    {
        if (value is not JsonValue json)
        {
            return null;
        }

        if (json.TryGetValue<ulong>(out var u))
        {
            return u;
        }

        if (json.TryGetValue<long>(out var l))
        {
            return l >= 0 ? (ulong)l : null;
        }

        if (json.TryGetValue<double>(out var d))
        {
            return d >= 0 && d == Math.Floor(d) && d < 1.8446744073709552E19 ? (ulong)d : null;
        }

        return null;
    }

    /// <summary>A member of an object value, null when the value is not an object or lacks the key.</summary>
    public static JsonNode? Member(this JsonNode? value, string key) => value is JsonObject map && map.TryGetPropertyValue(key, out var found) ? found : null;

    /// <summary>Whether <paramref name="values"/> holds <paramref name="want"/> (ordinal).</summary>
    public static bool Contains(IEnumerable<string> values, string want) => values.Contains(want, StringComparer.Ordinal);

    /// <summary>The compact JSON text of <paramref name="value"/> with object members in ordinal key order.</summary>
    public static string Canonical(JsonNode? value)
    {
        var builder = new StringBuilder();
        WriteCanonical(builder, value);
        return builder.ToString();
    }

    /// <summary>The compact JSON text of a value; "null" for none.</summary>
    public static string ToJson(this JsonNode? value) => value is null ? "null" : value.ToJsonString();

    /// <summary>Parses JSON text that must be an object.</summary>
    public static JsonObject ParseObject(string json) =>
        JsonNode.Parse(json) as JsonObject ?? throw new JsonException("expected a JSON object");

    private static void WriteCanonical(StringBuilder builder, JsonNode? value)
    {
        switch (value)
        {
            case null:
                builder.Append("null");
                break;
            case JsonObject map:
                builder.Append('{');
                var first = true;
                foreach (var key in map.Select(pair => pair.Key).Order(StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    builder.Append(JsonValue.Create(key).ToJsonString()).Append(':');
                    WriteCanonical(builder, map[key]);
                }

                builder.Append('}');
                break;
            case JsonArray array:
                builder.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    WriteCanonical(builder, array[i]);
                }

                builder.Append(']');
                break;
            default:
                builder.Append(value.ToJsonString());
                break;
        }
    }
}
