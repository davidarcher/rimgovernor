using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimGovernor.Brain.Acceptance;

// The shot tap's row kinds (#3410), one record each with its fields in the order the rows were encoded
// (the dataset under calibration/shots keeps that order): seed and k first. A field a row does not carry reads as
// its zero, a null string as "", and a field a kind does not declare is an error, so a schema change fails the dataset
// build rather than reading as zeros. The properties are settable, not init: the source generator assigns every init
// property, so an absent field would read as null rather than its zero.

/// <summary>Any tap row, read for its kind.</summary>
internal sealed record ShotTapKind
{
    public string K { get; set; } = "";
}

/// <summary>A projectile launched.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ShotTapShot
{
    public int Seed { get; set; }

    public string K { get; set; } = "";

    public int Tick { get; set; }

    public int Id { get; set; }

    public string Shooter { get; set; } = "";

    public string Side { get; set; } = "";

    public int X { get; set; }

    public int Z { get; set; }

    public string Target { get; set; } = "";

    public int Tx { get; set; }

    public int Tz { get; set; }

    public int Dx { get; set; }

    public int Dz { get; set; }

    public int Burst { get; set; }

    public int Of { get; set; }

    public string Weapon { get; set; } = "";

    public string Proj { get; set; } = "";

    public int Tti { get; set; }
}

/// <summary>A projectile's impact and, once stamped, the damage it dealt.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ShotTapImpact
{
    public int Seed { get; set; }

    public string K { get; set; } = "";

    public int Tick { get; set; }

    public int Id { get; set; }

    public string Hit { get; set; } = "";

    public int X { get; set; }

    public int Z { get; set; }

    public double Dealt { get; set; }
}

/// <summary>A pawn's job change.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ShotTapJob
{
    public int Seed { get; set; }

    public string K { get; set; } = "";

    public int Tick { get; set; }

    public string Pawn { get; set; } = "";

    public string Side { get; set; } = "";

    public string Job { get; set; } = "";

    public bool Forced { get; set; }

    public string Thing { get; set; } = "";

    public int X { get; set; }

    public int Z { get; set; }
}

/// <summary>A pawn's stance change.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ShotTapStance
{
    public int Seed { get; set; }

    public string K { get; set; } = "";

    public int Tick { get; set; }

    public string Pawn { get; set; } = "";

    public string Side { get; set; } = "";

    public string Stance { get; set; } = "";

    public int Left { get; set; }

    public string Thing { get; set; } = "";

    public int X { get; set; }

    public int Z { get; set; }
}

/// <summary>A pawn drafted or undrafted.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ShotTapDraft
{
    public int Seed { get; set; }

    public string K { get; set; } = "";

    public int Tick { get; set; }

    public string Pawn { get; set; } = "";

    public bool Drafted { get; set; }
}

/// <summary>A pawn's think-tree passes.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ShotTapThink
{
    public int Seed { get; set; }

    public string K { get; set; } = "";

    public string Pawn { get; set; } = "";

    public IReadOnlyList<int> Ticks { get; set; } = [];

    public int Count { get; set; }
}

/// <summary>Reads a JSON null string as "", so a stamped row carries "" where the tap wrote null.</summary>
internal sealed class EmptyForNullString : JsonConverter<string>
{
    public override bool HandleNull => true;

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? "" : reader.GetString() ?? "";

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

/// <summary>The shot tap rows' JSON: compact, camelCase, null strings read as "".</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, Converters = [typeof(EmptyForNullString)])]
[JsonSerializable(typeof(ShotTapKind))]
[JsonSerializable(typeof(ShotTapShot))]
[JsonSerializable(typeof(ShotTapImpact))]
[JsonSerializable(typeof(ShotTapJob))]
[JsonSerializable(typeof(ShotTapStance))]
[JsonSerializable(typeof(ShotTapDraft))]
[JsonSerializable(typeof(ShotTapThink))]
internal sealed partial class ShotTapJsonContext : JsonSerializerContext;
