using System.Text.Json;
using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;
using DomainWire = RimGovernor.Brain.Domain.Wire;

namespace RimGovernor.Contracts.Wire;

/// <summary>Writes an enum as its <see cref="WireNameAttribute"/> string and reads it back; an unknown name is a <see cref="JsonException"/>.</summary>
public sealed class WireEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"{typeof(TEnum).Name} must be a string.");
        }
        var text = reader.GetString();
        if (!DomainWire.TryParse<TEnum>(text, out var value))
        {
            throw new JsonException($"{typeof(TEnum).Name} has no value named '{text}'.");
        }
        return value;
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        if (!DomainWire.IsDefined(value))
        {
            throw new JsonException($"{typeof(TEnum).Name} has no member {value}.");
        }
        writer.WriteStringValue(DomainWire.Name(value));
    }
}
