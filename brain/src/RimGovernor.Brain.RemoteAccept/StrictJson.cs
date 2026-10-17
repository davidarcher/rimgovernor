using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.RemoteAccept;

/// <summary>
/// The JSON boundary: duplicate keys (also case aliases), missing required fields, null
/// non-nullable fields, wrong types and trailing values fail closed. Hand-written over <see cref="JsonNode"/>; no
/// reflection-based serialization.
/// </summary>
internal static class StrictJson
{
    private const int MaxDepth = 1000;

    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = MaxDepth };

    /// <summary>Parses exactly one JSON value; duplicate keys (case-folded) and trailing data throw. Returns null for a JSON null.</summary>
    public static JsonNode? Parse(ReadOnlySpan<byte> data)
    {
        CheckUnique(data, allowMultiple: false);
        try
        {
            return JsonNode.Parse(data, null, DocumentOptions);
        }
        catch (JsonException e)
        {
            throw new RemoteAcceptException(e.Message, e);
        }
    }

    /// <summary>Parses exactly one JSON object (a null or any other value throws).</summary>
    public static JsonObject ParseObject(ReadOnlySpan<byte> data, string what)
    {
        var node = Parse(data);
        return node as JsonObject ?? throw new RemoteAcceptException($"{what}: expected a JSON object");
    }

    /// <summary>Parses exactly one JSON array.</summary>
    public static JsonArray ParseArray(ReadOnlySpan<byte> data, string what)
    {
        var node = Parse(data);
        return node as JsonArray ?? throw new RemoteAcceptException($"{what}: expected a JSON array");
    }

    /// <summary>Throws on a duplicate key (compared case-insensitively), malformed JSON or, unless <paramref name="allowMultiple"/>, trailing values.</summary>
    public static void CheckUnique(ReadOnlySpan<byte> data, bool allowMultiple)
    {
        var options = new JsonReaderOptions { MaxDepth = MaxDepth, AllowMultipleValues = allowMultiple };
        var reader = new Utf8JsonReader(data, isFinalBlock: true, new JsonReaderState(options));
        var scopes = new Stack<HashSet<string>>();
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        scopes.Push(new HashSet<string>(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.EndObject:
                        scopes.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                        var raw = reader.GetString()!;
                        if (!scopes.Peek().Add(raw.ToLowerInvariant()))
                        {
                            throw new RemoteAcceptException($"duplicate JSON key \"{raw}\"");
                        }

                        break;
                }
            }
        }
        catch (JsonException e)
        {
            throw new RemoteAcceptException(e.Message, e);
        }
        catch (InvalidOperationException e)
        {
            throw new RemoteAcceptException(e.Message, e);
        }
    }

    /// <summary>Required string field: present, not null, a JSON string.</summary>
    public static string Str(JsonObject o, string key) => ReadString(Required(o, key), key);

    /// <summary>Required integer field.</summary>
    public static long Int64(JsonObject o, string key) => ReadInt64(Required(o, key), key);

    /// <summary>Required 32-bit integer field.</summary>
    public static int Int(JsonObject o, string key) => ToInt(Int64(o, key), key);

    /// <summary>Required boolean field.</summary>
    public static bool Bool(JsonObject o, string key) => ReadBool(Required(o, key), key);

    /// <summary>Required object field.</summary>
    public static JsonObject Obj(JsonObject o, string key) => Required(o, key) as JsonObject ?? throw new RemoteAcceptException($"{key}: expected an object");

    /// <summary>Required array field.</summary>
    public static JsonArray Arr(JsonObject o, string key) => Required(o, key) as JsonArray ?? throw new RemoteAcceptException($"{key}: expected an array");

    /// <summary>Required but nullable integer: the key must exist, null is allowed.</summary>
    public static int? NullableInt(JsonObject o, string key)
    {
        var n = Present(o, key);
        return n is null ? null : ToInt(ReadInt64(n, key), key);
    }

    /// <summary>Required but nullable string.</summary>
    public static string? NullableStr(JsonObject o, string key)
    {
        var n = Present(o, key);
        return n is null ? null : ReadString(n, key);
    }

    /// <summary>Required but nullable object.</summary>
    public static JsonObject? NullableObj(JsonObject o, string key)
    {
        var n = Present(o, key);
        return n is null ? null : n as JsonObject ?? throw new RemoteAcceptException($"{key}: expected an object");
    }

    /// <summary>Optional non-pointer string (absent gives null; JSON null is an error).</summary>
    public static string? OptStr(JsonObject o, string key) =>
        o.TryGetPropertyValue(key, out var n) ? ReadString(n ?? throw new RemoteAcceptException($"{key}: null string"), key) : null;

    /// <summary>Optional non-pointer boolean (JSON null is an error).</summary>
    public static bool OptBool(JsonObject o, string key) =>
        o.TryGetPropertyValue(key, out var n) && ReadBool(n ?? throw new RemoteAcceptException($"{key}: null bool"), key);

    /// <summary>Optional non-pointer 64-bit integer (JSON null is an error).</summary>
    public static long OptInt64(JsonObject o, string key) =>
        o.TryGetPropertyValue(key, out var n) ? ReadInt64(n ?? throw new RemoteAcceptException($"{key}: null number"), key) : 0;

    /// <summary>Optional non-pointer 32-bit integer.</summary>
    public static int OptInt(JsonObject o, string key) => ToInt(OptInt64(o, key), key);

    /// <summary>Optional pointer integer (null and absent both give null).</summary>
    public static int? OptNullableInt(JsonObject o, string key) => o.ContainsKey(key) ? NullableInt(o, key) : null;

    /// <summary>Optional pointer bool (null and absent both give null).</summary>
    public static bool? OptNullableBool(JsonObject o, string key) =>
        o.TryGetPropertyValue(key, out var n) && n is not null ? ReadBool(n, key) : null;

    /// <summary>Every element of the required array parsed by <paramref name="read"/>.</summary>
    public static List<T> List<T>(JsonObject o, string key, Func<JsonNode?, T> read) => Elements(Arr(o, key), read);

    /// <summary>Every element of <paramref name="array"/> parsed by <paramref name="read"/>.</summary>
    public static List<T> Elements<T>(JsonArray array, Func<JsonNode?, T> read)
    {
        var list = new List<T>(array.Count);
        foreach (var item in array)
        {
            list.Add(read(item));
        }

        return list;
    }

    /// <summary>An array element that must be an object.</summary>
    public static JsonObject ElementObj(JsonNode? n) => n as JsonObject ?? throw new RemoteAcceptException("expected an object");

    /// <summary>An array element that must be a string.</summary>
    public static string ElementStr(JsonNode? n) => ReadString(n ?? throw new RemoteAcceptException("null string"), "element");

    /// <summary>Lenient string read: absent or null gives "", a non-string throws.</summary>
    public static string LooseStr(JsonObject o, string key) =>
        o.TryGetPropertyValue(key, out var n) && n is not null ? ReadString(n, key) : "";

    /// <summary>Lenient 64-bit integer read: absent or null gives 0.</summary>
    public static long LooseInt64(JsonObject o, string key) =>
        o.TryGetPropertyValue(key, out var n) && n is not null ? ReadInt64(n, key) : 0;

    /// <summary>Lenient boolean read: absent or null gives false.</summary>
    public static bool LooseBool(JsonObject o, string key) =>
        o.TryGetPropertyValue(key, out var n) && n is not null && ReadBool(n, key);

    /// <summary>Lenient object read: absent or null gives null.</summary>
    public static JsonObject? LooseObj(JsonObject o, string key) =>
        o.TryGetPropertyValue(key, out var n) && n is not null ? n as JsonObject ?? throw new RemoteAcceptException($"{key}: expected an object") : null;

    /// <summary>Lenient array read: absent or null gives an empty list.</summary>
    public static List<JsonNode?> LooseArr(JsonObject o, string key) =>
        o.TryGetPropertyValue(key, out var n) && n is not null ? [.. (n as JsonArray ?? throw new RemoteAcceptException($"{key}: expected an array"))] : [];

    /// <summary>Lenient parse: well-formed JSON object, duplicates tolerated.</summary>
    public static JsonObject ParseLoose(ReadOnlySpan<byte> data)
    {
        try
        {
            return JsonNode.Parse(data, null, DocumentOptions) as JsonObject ?? throw new RemoteAcceptException("expected a JSON object");
        }
        catch (JsonException e)
        {
            throw new RemoteAcceptException(e.Message, e);
        }
    }

    /// <summary>Writes compact JSON.</summary>
    public static byte[] Compact(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, WriterOptions(indented: false)))
        {
            write(w);
        }

        return stream.ToArray();
    }

    /// <summary>Writes two-space indented JSON with a trailing newline.</summary>
    public static byte[] Indented(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, WriterOptions(indented: true)))
        {
            write(w);
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    /// <summary>A copy of <paramref name="source"/> with its top-level keys in ordinal order.</summary>
    public static JsonObject SortedCopy(JsonObject source)
    {
        var copy = new JsonObject();
        foreach (var key in source.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal))
        {
            copy[key] = source[key]?.DeepClone();
        }

        return copy;
    }

    /// <summary>Writes <paramref name="node"/> with every object's keys sorted.</summary>
    public static void WriteSorted(Utf8JsonWriter w, JsonNode? node)
    {
        switch (node)
        {
            case null:
                w.WriteNullValue();
                break;
            case JsonObject o:
                w.WriteStartObject();
                foreach (var key in o.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal))
                {
                    w.WritePropertyName(key);
                    WriteSorted(w, o[key]);
                }

                w.WriteEndObject();
                break;
            case JsonArray a:
                w.WriteStartArray();
                foreach (var item in a)
                {
                    WriteSorted(w, item);
                }

                w.WriteEndArray();
                break;
            default:
                node.WriteTo(w);
                break;
        }
    }

    /// <summary>Writes <paramref name="n"/> (a raw JSON text, "null" included) or nothing when null; used for raw JSON fields.</summary>
    public static void WriteRaw(Utf8JsonWriter w, string raw)
    {
        var node = JsonNode.Parse(raw, null, DocumentOptions);
        if (node is null)
        {
            w.WriteNullValue();
        }
        else
        {
            node.WriteTo(w);
        }
    }

    private static JsonWriterOptions WriterOptions(bool indented) => new()
    {
        Indented = indented,
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static int ToInt(long v, string key) =>
        v is >= int.MinValue and <= int.MaxValue ? (int)v : throw new RemoteAcceptException($"{key}: number out of range");

    private static JsonNode Required(JsonObject o, string key) =>
        Present(o, key) ?? throw new RemoteAcceptException($"{key}: null value");

    /// <summary>The value of a key that must exist; JSON null comes back as null.</summary>
    private static JsonNode? Present(JsonObject o, string key) =>
        o.TryGetPropertyValue(key, out var n) ? n : throw new RemoteAcceptException($"missing {key}");

    private static string ReadString(JsonNode n, string key) =>
        n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : throw new RemoteAcceptException($"{key}: expected a string");

    private static bool ReadBool(JsonNode n, string key) =>
        n is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? v.GetValue<bool>() : throw new RemoteAcceptException($"{key}: expected a boolean");

    private static long ReadInt64(JsonNode n, string key)
    {
        if (n is JsonValue v && v.GetValueKind() == JsonValueKind.Number
            && long.TryParse(v.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        throw new RemoteAcceptException($"{key}: expected an integer");
    }
}
