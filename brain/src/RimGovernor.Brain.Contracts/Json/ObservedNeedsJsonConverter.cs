using System.Text.Json;
using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Contracts.Wire;

/// <summary>Wire unknown is JSON null; a known number remains a number. Does not serialize Fact's guarded getter.</summary>
public sealed class ObservedNumberJsonConverter : JsonConverter<Fact<double>>
{
    public override Fact<double> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? default : Fact.Known(reader.GetDouble());
    public override void Write(Utf8JsonWriter writer, Fact<double> value, JsonSerializerOptions options)
    { if (value.TryGetValue(out var number)) writer.WriteNumberValue(number); else writer.WriteNullValue(); }
}
public sealed class ObservedMemoriesJsonConverter : JsonConverter<Fact<EquatableArray<Observations.NeedMemory>>>
{
    private static readonly EquatableArrayJsonConverter<Observations.NeedMemory> Items = new();
    public override Fact<EquatableArray<Observations.NeedMemory>> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? default : Fact.Known(Items.Read(ref reader, typeof(EquatableArray<Observations.NeedMemory>), options));
    public override void Write(Utf8JsonWriter writer, Fact<EquatableArray<Observations.NeedMemory>> value, JsonSerializerOptions options)
    { if (value.TryGetValue(out var items)) Items.Write(writer, items, options); else writer.WriteNullValue(); }
}
