using System.Collections.Concurrent;
using System.Text.Json;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Store.Codec;

/// <summary>A persisted record that does not decode: the journal is damaged or was written by other code.</summary>
public sealed class StoreCorruptException(string message) : Exception(message);

/// <summary>
/// One record type's JSON shape, written and read by hand with fixed Go-style field names, so renaming a C#
/// property never changes the saved shape. The brain is reloadable, so nothing here goes through the
/// reflection-based <c>JsonSerializer</c> (its static caches pin a collectible load context): only the raw
/// <see cref="Utf8JsonWriter"/> and <see cref="JsonElement"/> are used.
/// </summary>
public interface ICodec<T>
{
    void Encode(Utf8JsonWriter w, T value, Codecs codecs);

    T Decode(JsonElement e, Codecs codecs);
}

/// <summary>Writes a value known only by its runtime type (an action payload).</summary>
public interface IBoxedCodec
{
    void EncodeBoxed(Utf8JsonWriter w, object value, Codecs codecs);
}

/// <summary>
/// The registry of every codec. Each domain record registers explicitly in <see cref="CodecRegistry"/>; the
/// closed generic shapes (enums, <see cref="Fact{T}"/>, <see cref="EquatableArray{T}"/>) are derived from the
/// codec of their element.
/// </summary>
public sealed class Codecs
{
    private readonly ConcurrentDictionary<Type, object> _codecs = new();

    /// <summary>The registry the journal reads and writes with.</summary>
    public static Codecs Default { get; } = CodecRegistry.Build();

    public void Register<T>(ICodec<T> codec)
    {
        if (!_codecs.TryAdd(typeof(T), codec))
        {
            throw new InvalidOperationException("duplicate codec for " + typeof(T).Name);
        }
    }

    public ICodec<T> For<T>() => (ICodec<T>)_codecs.GetOrAdd(typeof(T), static type => Derive(type));

    public void Write<T>(Utf8JsonWriter w, T value) => For<T>().Encode(w, value, this);

    public T Read<T>(JsonElement e) => For<T>().Decode(e, this);

    /// <summary>Writes <paramref name="value"/> with the record codec of its runtime type.</summary>
    public void WriteBoxed(Utf8JsonWriter w, object value)
    {
        if (!_codecs.TryGetValue(value.GetType(), out var codec) || codec is not IBoxedCodec boxed)
        {
            throw new InvalidOperationException("no record codec for " + value.GetType());
        }
        boxed.EncodeBoxed(w, value, this);
    }

    public string Serialize<T>(T value)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            Write(w, value);
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public T Deserialize<T>(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return Read<T>(document.RootElement);
        }
        catch (JsonException e)
        {
            throw new StoreCorruptException(typeof(T).Name + " record is not JSON: " + e.Message);
        }
    }

    private static object Derive(Type type)
    {
        if (type.IsEnum)
        {
            return Activator.CreateInstance(typeof(WireEnumCodec<>).MakeGenericType(type))!;
        }
        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            var codec = definition switch
            {
                _ when definition == typeof(Fact<>) => typeof(FactCodec<>),
                _ when definition == typeof(EquatableArray<>) => typeof(ArrayCodec<>),
                _ when definition == typeof(List<>) => typeof(ListCodec<>),
                _ when definition == typeof(IReadOnlyList<>) => typeof(ReadOnlyListCodec<>),
                _ when definition == typeof(HashSet<>) => typeof(HashSetCodec<>),
                _ when definition == typeof(IReadOnlySet<>) => typeof(ReadOnlySetCodec<>),
                _ when definition == typeof(Nullable<>) => typeof(NullableCodec<>),
                _ when definition == typeof(Dictionary<,>) => typeof(DictionaryCodec<,>),
                _ when definition == typeof(IReadOnlyDictionary<,>) => typeof(ReadOnlyDictionaryCodec<,>),
                _ => null,
            };
            if (codec is not null)
            {
                return Activator.CreateInstance(codec.MakeGenericType(type.GetGenericArguments()))!;
            }
        }
        throw new InvalidOperationException("no codec registered for " + type);
    }
}

/// <summary>The journal's JSON entry points (Go's <c>json.Marshal</c> and <c>decode</c> of a store record).</summary>
public static class StoreJson
{
    public static string Serialize<T>(T value) => Codecs.Default.Serialize(value);

    public static T Deserialize<T>(string json) => Codecs.Default.Deserialize<T>(json);
}

/// <summary>A record written as a JSON object by hand, one field at a time.</summary>
public abstract class ObjectCodec<T> : ICodec<T>, IBoxedCodec
{
    void IBoxedCodec.EncodeBoxed(Utf8JsonWriter w, object value, Codecs codecs) => Encode(w, (T)value, codecs);

    public T Decode(JsonElement e, Codecs codecs)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            throw new StoreCorruptException(typeof(T).Name + " record is not an object");
        }
        try
        {
            return Read(e, codecs);
        }
        catch (DomainException error)
        {
            throw new StoreCorruptException(typeof(T).Name + " record: " + error.Message);
        }
    }

    public void Encode(Utf8JsonWriter w, T value, Codecs codecs)
    {
        w.WriteStartObject();
        WriteFields(w, value, codecs);
        w.WriteEndObject();
    }

    protected abstract T Read(JsonElement e, Codecs o);

    protected abstract void WriteFields(Utf8JsonWriter w, T value, Codecs o);
}

/// <summary>A single scalar written as a JSON string.</summary>
public abstract class StringCodec<T>(Func<string, T> make, Func<T, string> text) : ICodec<T>
{
    public T Decode(JsonElement e, Codecs codecs)
    {
        if (e.ValueKind != JsonValueKind.String)
        {
            throw new StoreCorruptException(typeof(T).Name + " is not a string");
        }
        try
        {
            return make(e.GetString()!);
        }
        catch (DomainException error)
        {
            throw new StoreCorruptException(typeof(T).Name + ": " + error.Message);
        }
    }

    public void Encode(Utf8JsonWriter w, T value, Codecs codecs) => w.WriteStringValue(text(value));
}

/// <summary>A single scalar written as a JSON number.</summary>
public abstract class NumberCodec<T>(Func<JsonElement, T> read, Action<Utf8JsonWriter, T> write) : ICodec<T>
{
    public T Decode(JsonElement e, Codecs codecs)
    {
        if (e.ValueKind != JsonValueKind.Number)
        {
            throw new StoreCorruptException(typeof(T).Name + " is not a number");
        }
        try
        {
            return read(e);
        }
        catch (FormatException)
        {
            throw new StoreCorruptException(typeof(T).Name + " number out of range");
        }
    }

    public void Encode(Utf8JsonWriter w, T value, Codecs codecs) => write(w, value);
}

/// <summary>Reading and writing helpers shared by the hand-written codecs.</summary>
public static class JsonFields
{
    public static void Put<T>(this Utf8JsonWriter w, string name, T value, Codecs o)
    {
        w.WritePropertyName(name);
        o.Write(w, value);
    }

    public static T Get<T>(this JsonElement e, string name, Codecs o) =>
        e.TryGetProperty(name, out var field) ? o.Read<T>(field) : throw new StoreCorruptException($"missing field {name}");

    /// <summary>The field, or <paramref name="fallback"/> when it is absent (a field Go left out when empty).</summary>
    public static T GetOr<T>(this JsonElement e, string name, T fallback, Codecs o) =>
        e.TryGetProperty(name, out var field) && field.ValueKind != JsonValueKind.Null ? o.Read<T>(field) : fallback;

    public static string Str(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString()! : throw new StoreCorruptException($"missing string {name}");

    public static string StrOr(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString()! : "";

    public static int I32(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var field) && field.TryGetInt32(out var value) ? value : throw new StoreCorruptException($"missing int {name}");

    public static long I64(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var field) && field.TryGetInt64(out var value) ? value : throw new StoreCorruptException($"missing long {name}");

    public static ulong U64(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var field) && field.TryGetUInt64(out var value) ? value : throw new StoreCorruptException($"missing ulong {name}");

    public static double Dbl(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var field) && field.TryGetDouble(out var value) ? value : throw new StoreCorruptException($"missing number {name}");

    public static bool Bool(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var field) && (field.ValueKind is JsonValueKind.True or JsonValueKind.False)
            ? field.GetBoolean()
            : throw new StoreCorruptException($"missing bool {name}");

    public static bool BoolOr(this JsonElement e, string name) =>
        e.TryGetProperty(name, out var field) && (field.ValueKind is JsonValueKind.True or JsonValueKind.False) && field.GetBoolean();

    public static bool Has(this JsonElement e, string name) => e.TryGetProperty(name, out var field) && field.ValueKind != JsonValueKind.Null;
}

/// <summary>A <see cref="Fact{T}"/> persists as JSON null when unknown and as the value when known.</summary>
public sealed class FactCodec<T> : ICodec<Fact<T>>
{
    public Fact<T> Decode(JsonElement e, Codecs codecs) => e.ValueKind == JsonValueKind.Null ? default : Fact.Known(codecs.Read<T>(e));

    public void Encode(Utf8JsonWriter w, Fact<T> value, Codecs codecs)
    {
        if (value.TryGetValue(out var known))
        {
            codecs.Write(w, known);
        }
        else
        {
            w.WriteNullValue();
        }
    }
}

/// <summary>An <see cref="EquatableArray{T}"/> persists as a JSON array.</summary>
public sealed class ArrayCodec<T> : ICodec<EquatableArray<T>>
{
    public EquatableArray<T> Decode(JsonElement e, Codecs codecs)
    {
        if (e.ValueKind == JsonValueKind.Null)
        {
            return default;
        }
        if (e.ValueKind != JsonValueKind.Array)
        {
            throw new StoreCorruptException("array record is not an array");
        }
        var items = new List<T>(e.GetArrayLength());
        foreach (var item in e.EnumerateArray())
        {
            items.Add(codecs.Read<T>(item));
        }
        return EquatableArray.From(items);
    }

    public void Encode(Utf8JsonWriter w, EquatableArray<T> value, Codecs codecs)
    {
        w.WriteStartArray();
        foreach (var item in value)
        {
            codecs.Write(w, item);
        }
        w.WriteEndArray();
    }
}

/// <summary>A closed-string enum persists as its Go wire name (<see cref="Wire"/>).</summary>
public sealed class WireEnumCodec<TEnum> : ICodec<TEnum>
    where TEnum : struct, Enum
{
    public TEnum Decode(JsonElement e, Codecs codecs)
    {
        var name = e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        return Wire.TryParse<TEnum>(name, out var value) ? value : throw new StoreCorruptException($"unknown {typeof(TEnum).Name} \"{name}\"");
    }

    public void Encode(Utf8JsonWriter w, TEnum value, Codecs codecs)
    {
        if (!Wire.IsDefined(value))
        {
            throw new StoreCorruptException($"undefined {typeof(TEnum).Name} {value}");
        }
        w.WriteStringValue(Wire.Name(value));
    }
}

public sealed class StringValueCodec() : StringCodec<string>(s => s, s => s);

public sealed class Int32Codec() : NumberCodec<int>(e => e.GetInt32(), (w, v) => w.WriteNumberValue(v));

public sealed class Int64Codec() : NumberCodec<long>(e => e.GetInt64(), (w, v) => w.WriteNumberValue(v));

public sealed class UInt32Codec() : NumberCodec<uint>(e => e.GetUInt32(), (w, v) => w.WriteNumberValue(v));

public sealed class UInt64Codec() : NumberCodec<ulong>(e => e.GetUInt64(), (w, v) => w.WriteNumberValue(v));

public sealed class SingleCodec() : NumberCodec<float>(e => e.GetSingle(), (w, v) => w.WriteNumberValue(v));

public sealed class DoubleCodec() : NumberCodec<double>(e => e.GetDouble(), (w, v) => w.WriteNumberValue(v));

public sealed class BooleanCodec : ICodec<bool>
{
    public bool Decode(JsonElement e, Codecs codecs) =>
        e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : throw new StoreCorruptException("boolean record is not a boolean");

    public void Encode(Utf8JsonWriter w, bool value, Codecs codecs) => w.WriteBooleanValue(value);
}
