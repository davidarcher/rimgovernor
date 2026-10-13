using System.Text.Json;

namespace RimGovernor.Brain.Store.Codec;

/// <summary>Shared array reading for the collection codecs: a JSON array of elements, or null for none.</summary>
internal static class ArrayReader
{
    public static List<T> Items<T>(JsonElement e, Codecs codecs)
    {
        if (e.ValueKind == JsonValueKind.Null)
        {
            return [];
        }
        if (e.ValueKind != JsonValueKind.Array)
        {
            throw new StoreCorruptException("collection record is not an array");
        }
        var items = new List<T>(e.GetArrayLength());
        foreach (var item in e.EnumerateArray())
        {
            items.Add(codecs.Read<T>(item));
        }
        return items;
    }

    public static void Write<T>(Utf8JsonWriter w, IEnumerable<T> items, Codecs codecs)
    {
        w.WriteStartArray();
        foreach (var item in items)
        {
            codecs.Write(w, item);
        }
        w.WriteEndArray();
    }
}

/// <summary>A <see cref="List{T}"/> persists as a JSON array.</summary>
public sealed class ListCodec<T> : ICodec<List<T>>
{
    public List<T> Decode(JsonElement e, Codecs codecs) => ArrayReader.Items<T>(e, codecs);

    public void Encode(Utf8JsonWriter w, List<T> value, Codecs codecs) => ArrayReader.Write(w, value, codecs);
}

/// <summary>An <see cref="IReadOnlyList{T}"/> persists as a JSON array.</summary>
public sealed class ReadOnlyListCodec<T> : ICodec<IReadOnlyList<T>>
{
    public IReadOnlyList<T> Decode(JsonElement e, Codecs codecs) => ArrayReader.Items<T>(e, codecs);

    public void Encode(Utf8JsonWriter w, IReadOnlyList<T> value, Codecs codecs) => ArrayReader.Write(w, value, codecs);
}

/// <summary>A <see cref="HashSet{T}"/> persists as a JSON array in the elements' serialized order, so equal sets write equal bytes.</summary>
public sealed class HashSetCodec<T> : ICodec<HashSet<T>>
{
    public HashSet<T> Decode(JsonElement e, Codecs codecs) => [.. ArrayReader.Items<T>(e, codecs)];

    public void Encode(Utf8JsonWriter w, HashSet<T> value, Codecs codecs) => ArrayReader.Write(w, SetOrder.Sorted(value, codecs), codecs);
}

/// <summary>An <see cref="IReadOnlySet{T}"/> persists as a sorted JSON array.</summary>
public sealed class ReadOnlySetCodec<T> : ICodec<IReadOnlySet<T>>
{
    public IReadOnlySet<T> Decode(JsonElement e, Codecs codecs) => new HashSet<T>(ArrayReader.Items<T>(e, codecs));

    public void Encode(Utf8JsonWriter w, IReadOnlySet<T> value, Codecs codecs) => ArrayReader.Write(w, SetOrder.Sorted(value, codecs), codecs);
}

internal static class SetOrder
{
    public static IEnumerable<T> Sorted<T>(IEnumerable<T> items, Codecs codecs) => items.OrderBy(i => codecs.Serialize(i), StringComparer.Ordinal);
}

/// <summary>A nullable struct persists as JSON null or the value.</summary>
public sealed class NullableCodec<T> : ICodec<T?>
    where T : struct
{
    public T? Decode(JsonElement e, Codecs codecs) => e.ValueKind == JsonValueKind.Null ? null : codecs.Read<T>(e);

    public void Encode(Utf8JsonWriter w, T? value, Codecs codecs)
    {
        if (value is { } known)
        {
            codecs.Write(w, known);
        }
        else
        {
            w.WriteNullValue();
        }
    }
}

/// <summary>Shared entry reading for the dictionary codecs: an array of <c>{Key,Value}</c> objects in key order.</summary>
internal static class EntryReader
{
    public static List<KeyValuePair<TKey, TValue>> Entries<TKey, TValue>(JsonElement e, Codecs codecs)
    {
        if (e.ValueKind == JsonValueKind.Null)
        {
            return [];
        }
        if (e.ValueKind != JsonValueKind.Array)
        {
            throw new StoreCorruptException("map record is not an array");
        }
        var entries = new List<KeyValuePair<TKey, TValue>>(e.GetArrayLength());
        foreach (var item in e.EnumerateArray())
        {
            entries.Add(new(item.Get<TKey>("Key", codecs), item.Get<TValue>("Value", codecs)));
        }
        return entries;
    }

    public static void Write<TKey, TValue>(Utf8JsonWriter w, IEnumerable<KeyValuePair<TKey, TValue>> entries, Codecs codecs)
    {
        w.WriteStartArray();
        foreach (var entry in entries.OrderBy(p => codecs.Serialize(p.Key), StringComparer.Ordinal))
        {
            w.WriteStartObject();
            w.Put("Key", entry.Key, codecs);
            w.Put("Value", entry.Value, codecs);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }
}

/// <summary>A <see cref="Dictionary{TKey,TValue}"/> persists as an array of key/value objects sorted by serialized key (a map's iteration order is not stable).</summary>
public sealed class DictionaryCodec<TKey, TValue> : ICodec<Dictionary<TKey, TValue>>
    where TKey : notnull
{
    public Dictionary<TKey, TValue> Decode(JsonElement e, Codecs codecs) => EntryReader.Entries<TKey, TValue>(e, codecs).ToDictionary(p => p.Key, p => p.Value);

    public void Encode(Utf8JsonWriter w, Dictionary<TKey, TValue> value, Codecs codecs) => EntryReader.Write(w, value, codecs);
}

/// <summary>An <see cref="IReadOnlyDictionary{TKey,TValue}"/> persists like <see cref="DictionaryCodec{TKey,TValue}"/>.</summary>
public sealed class ReadOnlyDictionaryCodec<TKey, TValue> : ICodec<IReadOnlyDictionary<TKey, TValue>>
    where TKey : notnull
{
    public IReadOnlyDictionary<TKey, TValue> Decode(JsonElement e, Codecs codecs) => EntryReader.Entries<TKey, TValue>(e, codecs).ToDictionary(p => p.Key, p => p.Value);

    public void Encode(Utf8JsonWriter w, IReadOnlyDictionary<TKey, TValue> value, Codecs codecs) => EntryReader.Write(w, value, codecs);
}
