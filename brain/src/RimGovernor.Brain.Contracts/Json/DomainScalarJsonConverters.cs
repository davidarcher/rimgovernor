using System.Text.Json;
using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Contracts.Wire;

/// <summary>A facts record's <see cref="Tick"/> is its bare number on the wire.</summary>
public sealed class TickJsonConverter : JsonConverter<Tick>
{
    public override Tick Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetInt64());

    public override void Write(Utf8JsonWriter writer, Tick value, JsonSerializerOptions options) => writer.WriteNumberValue(value.Value);
}

/// <summary>A facts record's <see cref="PawnId"/> is its bare id text on the wire.</summary>
public sealed class PawnIdJsonConverter : JsonConverter<PawnId>
{
    public override PawnId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? throw new JsonException("A pawn id must be a string."));

    public override void Write(Utf8JsonWriter writer, PawnId value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
}

/// <summary>A facts record's <see cref="QuestId"/> is its bare id text on the wire.</summary>
public sealed class QuestIdJsonConverter : JsonConverter<QuestId>
{
    public override QuestId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? throw new JsonException("A quest id must be a string."));

    public override void Write(Utf8JsonWriter writer, QuestId value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
}

/// <summary>A facts record's <see cref="MapId"/> is its bare number on the wire.</summary>
public sealed class MapIdJsonConverter : JsonConverter<MapId>
{
    public override MapId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetInt32());

    public override void Write(Utf8JsonWriter writer, MapId value, JsonSerializerOptions options) => writer.WriteNumberValue(value.Value);
}
