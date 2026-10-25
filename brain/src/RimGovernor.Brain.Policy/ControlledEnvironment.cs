namespace RimGovernor.Brain.Policy;

/// <summary>
/// The native census of indoor growing capacity inside the planning region: sun lamps with the exact cells the
/// game grows under them, plant growers (hydroponics) with their fertility and sow tag, roofed rooms with
/// temperature and light, and every power network's headroom split by source. Site-type selection reads it to
/// price greenhouse reuse, new lamps, basins and dark rooms against outdoor soil; nothing here is planned or
/// written natively. Every row is whole.
/// </summary>
public readonly record struct ControlledEnvironment
{
    public required EquatableArray<GrowLight> Lights { get; init; }

    public required EquatableArray<PlantGrower> Growers { get; init; }

    public required EquatableArray<GrowRoom> Rooms { get; init; }

    public required EquatableArray<PowerHeadroom> Networks { get; init; }

    public required double OutdoorTemperatureC { get; init; }

    /// <summary>The headroom row for a native network id.</summary>
    public PowerHeadroom? Network(string id)
    {
        foreach (var n in Networks)
        {
            if (n.Id == id)
            {
                return n;
            }
        }
        return null;
    }

    /// <summary>
    /// Maps every cell a currently lit lamp grows to that lamp's id; a cell under two lamps keeps the
    /// lexically smaller id so output is stable.
    /// </summary>
    public Dictionary<Cell, string> LitCells()
    {
        var result = new Dictionary<Cell, string>();
        foreach (var l in Lights)
        {
            if (!l.LitNow)
            {
                continue;
            }
            foreach (var c in l.GrowthCells)
            {
                if (!result.TryGetValue(c, out var prev) || string.CompareOrdinal(l.Id, prev) < 0)
                {
                    result[c] = l.Id;
                }
            }
        }
        return result;
    }
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

    /// <summary>The network's surplus by day: generation less consumption.</summary>
    public double DayHeadroomW() => GenerationW - ConsumptionW;

    /// <summary>The network's surplus once solar output stops.</summary>
    public double NightHeadroomW() => GenerationW - SolarW - ConsumptionW;

    /// <summary>
    /// The surplus with neither sun nor wind: the margin a lamp or basin must fit in to keep growing through
    /// a still night.
    /// </summary>
    public double CalmNightHeadroomW() => NightHeadroomW() - WindW;
}

public static class ControlledEnvironmentRules
{
    /// <summary>Reports a crop whose native minimum glow is zero, such as cave fungus, so an unlit roofed room is eligible for it.</summary>
    public static bool GrowsInDark(CropChoice crop) =>
        crop.MinGlow.TryGetValue(out var glow) && FoodForecastRules.FoodNumber(glow) && glow <= 0;
}
