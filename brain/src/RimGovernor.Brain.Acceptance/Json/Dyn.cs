using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// Dynamic-JSON navigation shared by the harness and the cases (Go <c>assert.go</c>). A native reply is a decoded
/// JSON document; these helpers navigate it loosely, with explicit results in place of bare casts. The Go
/// <c>map[string]any</c> is a <see cref="JsonObject"/>, <c>[]any</c> a <see cref="JsonArray"/>.
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
    /// The number as a double. A numeric string is read as a number (ProtoJSON carries 64-bit counters as
    /// strings); anything else is -1, as in Go.
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

        if (json.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return -1;
    }

    /// <summary>A non-negative integer as JSON or ProtoJSON (a string) carries it.</summary>
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

        if (json.TryGetValue<string>(out var s) && ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    /// <summary>A JSON Ref's <c>id</c>, empty without one.</summary>
    public static string RefId(this JsonNode? value) => AsString(value.AsMap()?["id"]);

    /// <summary>A member of an object value, null when the value is not an object or lacks the key.</summary>
    public static JsonNode? Member(this JsonNode? value, string key) => value is JsonObject map && map.TryGetPropertyValue(key, out var found) ? found : null;

    /// <summary>A shallow union of <paramref name="baseValues"/> and <paramref name="extra"/>, extra's keys taking priority. Neither input is changed.</summary>
    public static JsonObject Merge(JsonObject? baseValues, JsonObject? extra)
    {
        var merged = new JsonObject();
        foreach (var (key, value) in baseValues ?? [])
        {
            merged[key] = value?.DeepClone();
        }

        foreach (var (key, value) in extra ?? [])
        {
            merged[key] = value?.DeepClone();
        }

        return merged;
    }

    /// <summary>Whether <paramref name="values"/> holds <paramref name="want"/> (ordinal).</summary>
    public static bool Contains(IEnumerable<string> values, string want) => values.Contains(want, StringComparer.Ordinal);

    /// <summary>Structural equality of two decoded-JSON values; object member order does not matter.</summary>
    public static bool DeepEqual(JsonNode? a, JsonNode? b) => Canonical(a) == Canonical(b);

    /// <summary>The compact JSON text of <paramref name="value"/> with object members in ordinal key order.</summary>
    public static string Canonical(JsonNode? value)
    {
        var builder = new StringBuilder();
        WriteCanonical(builder, value);
        return builder.ToString();
    }

    /// <summary>The compact JSON text of a value; "null" for none.</summary>
    public static string ToJson(this JsonNode? value) => value is null ? "null" : value.ToJsonString();

    /// <summary>Parses JSON text into a node; the text must be valid JSON.</summary>
    public static JsonNode? Parse(string json) => JsonNode.Parse(json);

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
