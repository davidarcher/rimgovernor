using System.Text.Json;
using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Contracts.Wire;

/// <summary>Known values encode as their value; explicit JSON null denotes an unknown estimate.</summary>
public sealed class FactJsonConverter<T> : JsonConverter<Fact<T>>
{
    public override Fact<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? default
            : Fact.Known((T)JsonSerializer.Deserialize(ref reader, options.GetTypeInfo(typeof(T)))!);
    public override void Write(Utf8JsonWriter writer, Fact<T> value, JsonSerializerOptions options)
    {
        if (value.TryGetValue(out var known)) JsonSerializer.Serialize(writer, known, options.GetTypeInfo(typeof(T)));
        else writer.WriteNullValue();
    }
}
