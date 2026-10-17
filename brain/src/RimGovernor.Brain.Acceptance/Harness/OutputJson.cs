using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// Builds the output documents (result.json blocks, summaries, digests) without JsonNode string indexers (#3410): a
/// typed output record serialized through its source-generated type info, or an object whose fields are added in
/// order. The shape is the document's contract, so a port keeps every key, order and value kind as it was.
/// </summary>
public static class OutputJson
{
    /// <summary>The strings as a JSON array, in order.</summary>
    public static JsonArray Array(IEnumerable<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var array = new JsonArray();
        foreach (var item in items)
        {
            array.Add(item);
        }

        return array;
    }

    /// <summary>Each item mapped to its node, as a JSON array in order.</summary>
    public static JsonArray Array<T>(IEnumerable<T> items, Func<T, JsonNode?> map)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(map);
        var array = new JsonArray();
        foreach (var item in items)
        {
            array.Add(map(item));
        }

        return array;
    }

    /// <summary>An object of <paramref name="fields"/> in order; a null value is written as JSON null.</summary>
    public static JsonObject Fields(params ReadOnlySpan<(string Key, JsonNode? Value)> fields)
    {
        var o = new JsonObject();
        foreach (var (key, value) in fields)
        {
            o.Add(key, value);
        }

        return o;
    }

    /// <summary>
    /// Adds <paramref name="key"/> only when <paramref name="value"/> is not null: the optional field of a block whose
    /// writer leaves an absent fact out rather than writing null.
    /// </summary>
    public static JsonObject AddIfPresent(this JsonObject o, string key, JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(o);
        if (value is not null)
        {
            o.Add(key, value);
        }

        return o;
    }

    /// <summary>
    /// Sets <paramref name="key"/> to <paramref name="value"/>: replaced in place when the object has the field, else
    /// appended, so a field updated while a run goes on (a launch entry's state) keeps its position.
    /// </summary>
    public static JsonObject Set(this JsonObject o, string key, JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(o);
        var index = o.IndexOf(key);
        if (index >= 0)
        {
            o.SetAt(index, value);
        }
        else
        {
            o.Add(key, value);
        }

        return o;
    }

    /// <summary>A typed output record as its JSON node, through the record's source-generated <paramref name="info"/>.</summary>
    public static JsonNode Node<T>(T value, JsonTypeInfo<T> info) =>
        JsonSerializer.SerializeToNode(value, info) ?? throw new JsonException($"{typeof(T).Name} serialized to JSON null");
}
