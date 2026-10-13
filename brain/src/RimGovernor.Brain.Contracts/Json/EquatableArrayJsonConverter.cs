using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Contracts.Wire;

/// <summary>A JSON array as an <see cref="EquatableArray{T}"/>. The element type must be registered in the serializer context.</summary>
public sealed class EquatableArrayJsonConverter<T> : JsonConverter<EquatableArray<T>>
{
    public override EquatableArray<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("A list must be a JSON array.");
        }
        var element = (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
        var items = ImmutableArray.CreateBuilder<T>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            items.Add(JsonSerializer.Deserialize(ref reader, element)!);
        }
        return new EquatableArray<T>(items.ToImmutable());
    }

    public override void Write(Utf8JsonWriter writer, EquatableArray<T> value, JsonSerializerOptions options)
    {
        var element = (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
        writer.WriteStartArray();
        foreach (var item in value)
        {
            JsonSerializer.Serialize(writer, item, element);
        }
        writer.WriteEndArray();
    }
}
