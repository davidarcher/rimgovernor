using System.Text.Json;
using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Contracts.Wire;

public sealed class ObservedConditionalThoughtsJsonConverter : JsonConverter<Fact<EquatableArray<Observations.NeedConditionalThought>>>
{
    private static readonly EquatableArrayJsonConverter<Observations.NeedConditionalThought> Items = new();
    public override Fact<EquatableArray<Observations.NeedConditionalThought>> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? default : Fact.Known(Items.Read(ref reader, typeof(EquatableArray<Observations.NeedConditionalThought>), options));
    public override void Write(Utf8JsonWriter writer, Fact<EquatableArray<Observations.NeedConditionalThought>> value, JsonSerializerOptions options)
    { if (value.TryGetValue(out var items)) Items.Write(writer, items, options); else writer.WriteNullValue(); }
}
