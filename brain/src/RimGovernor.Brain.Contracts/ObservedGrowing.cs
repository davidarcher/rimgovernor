using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Contracts.Wire.Observations;

public sealed record ControlledEnvironment
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<GrowLight>))]
    public required EquatableArray<GrowLight> Lights { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PlantGrower>))]
    public required EquatableArray<PlantGrower> Growers { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<GrowRoom>))]
    public required EquatableArray<GrowRoom> Rooms { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PowerHeadroom>))]
    public required EquatableArray<PowerHeadroom> Networks { get; init; }

    public required double OutdoorTemperatureC { get; init; }
}

public sealed record GrowLight
{
    public required string Id { get; init; }

    /// <summary>The native room id the lamp stands in; null when it stands in none.</summary>
    public required string? Room { get; init; }

    /// <summary>The power network the lamp is connected to; null when it is connected to none.</summary>
    public required string? Network { get; init; }

    /// <summary>Powered and inside the lamp's native schedule.</summary>
    public required bool LitNow { get; init; }

    /// <summary>The native growth radius, not the wider glow radius.</summary>
    public required EquatableArray<Cell> GrowthCells { get; init; }
}

public sealed record PlantGrower
{
    public required string Id { get; init; }

    public required GrowerFertility Fertility { get; init; }

    /// <summary>The sow tag of the grower def's building properties; null when the def names none, so it takes no crop.</summary>
    public required string? SowTag { get; init; }

    /// <summary>The plant the grower sows; null when it is set to sow none.</summary>
    public required string? Crop { get; init; }

    public required bool CanSow { get; init; }
}

/// <summary>
/// The fertility a plant grower gives its plants: a fixed value its def sets (hydroponics), or, for a def that
/// sets none, the fertility of the ground beneath it, which the census does not price.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(GrowerFertility.Fixed), "fixed")]
[JsonDerivedType(typeof(GrowerFertility.Ground), "ground")]
public abstract record GrowerFertility
{
    private GrowerFertility()
    {
    }

    public sealed record Fixed(double Value) : GrowerFertility;

    public sealed record Ground : GrowerFertility;
}

public sealed record GrowRoom
{
    /// <summary>The native room id.</summary>
    public required string Id { get; init; }

    /// <summary>The room's key in the cell grid (SiteCell.Room); Id is the native room id, a different key space.</summary>
    public required int GridRoom { get; init; }

    public required double TemperatureC { get; init; }
}

public sealed record PowerHeadroom
{
    public required string Id { get; init; }

    public required double GenerationW { get; init; }

    public required double SolarW { get; init; }

    public required double WindW { get; init; }

    public required double ConsumptionW { get; init; }

    public required double CapacityWattDays { get; init; }

    public required bool ActiveSource { get; init; }
}
