namespace RimGovernor.Brain.Policy;

/// <summary>
/// The native census of indoor growing capacity inside the planning region: sun lamps with the exact cells the
/// game grows under them, plant growers (hydroponics) with their fertility and sow tag, roofed rooms with
/// temperature and light, and every power network's headroom split by source. Site-type selection reads it to
/// price greenhouse reuse, new lamps, basins and dark rooms against outdoor soil; nothing here is planned or
/// written natively.
/// </summary>
public sealed record ControlledEnvironment
{
    public EquatableArray<GrowLight> Lights { get; init; }

    public EquatableArray<PlantGrower> Growers { get; init; }

    public EquatableArray<GrowRoom> Rooms { get; init; }

    public EquatableArray<PowerHeadroom> Networks { get; init; }

    public Fact<double> OutdoorTemperatureC { get; init; }

    public Fact<bool> Daylight { get; init; }

    /// <summary>The current WeatherDef's accuracyMultiplier; unknown when the weather is unread.</summary>
    public Fact<double> WeatherAccuracy { get; init; }

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
            if (!Positive(l.LitNow))
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
    public string Id { get; init; } = "";

    public string Definition { get; init; } = "";

    public Cell Cell { get; init; }

    public Fact<string> Room { get; init; }

    public Fact<string> Network { get; init; }

    public Fact<bool> Powered { get; init; }

    public Fact<double> PowerW { get; init; }

    /// <summary>Powered and inside the lamp's native schedule.</summary>
    public Fact<bool> LitNow { get; init; }

    /// <summary>The native growth radius, not the wider glow radius.</summary>
    public EquatableArray<Cell> GrowthCells { get; init; }
}

public sealed record PlantGrower
{
    public string Id { get; init; } = "";

    public string Definition { get; init; } = "";

    public Cell Cell { get; init; }

    public Fact<string> Room { get; init; }

    public Fact<string> Network { get; init; }

    public Fact<bool> Powered { get; init; }

    public Fact<double> PowerW { get; init; }

    public Fact<double> Fertility { get; init; }

    public Fact<string> SowTag { get; init; }

    public Fact<string> Crop { get; init; }

    public Fact<bool> CanSow { get; init; }

    public EquatableArray<Cell> Cells { get; init; }
}

public sealed record GrowRoom
{
    public string Id { get; init; } = "";

    public Fact<double> TemperatureC { get; init; }

    public Fact<int> Cells { get; init; }

    public Fact<int> OpenRoof { get; init; }

    public Fact<int> Lit { get; init; }

    public Fact<bool> Proper { get; init; }

    public Fact<bool> Outdoors { get; init; }
}

public sealed record PowerHeadroom
{
    public string Id { get; init; } = "";

    public Fact<double> GenerationW { get; init; }

    public Fact<double> SolarW { get; init; }

    public Fact<double> WindW { get; init; }

    public Fact<double> ConsumptionW { get; init; }

    public Fact<double> StoredWattDays { get; init; }

    public Fact<double> CapacityWattDays { get; init; }

    public Fact<bool> ActiveSource { get; init; }

    /// <summary>The network's surplus once solar output stops. Unknown when any term is unknown.</summary>
    public Fact<double> NightHeadroomW()
    {
        var gk = GenerationW.TryGetValue(out var generation);
        var sk = SolarW.TryGetValue(out var solar);
        var ck = ConsumptionW.TryGetValue(out var consumption);
        if (!gk || !sk || !ck || !FoodNumber(generation) || !FoodNumber(solar) || !FoodNumber(consumption))
        {
            return Unknown<double>();
        }
        return Known(generation - solar - consumption);
    }

    /// <summary>
    /// The surplus with neither sun nor wind: the margin a lamp or basin must fit in to keep growing through
    /// a still night.
    /// </summary>
    public Fact<double> CalmNightHeadroomW()
    {
        var nk = NightHeadroomW().TryGetValue(out var night);
        var wk = WindW.TryGetValue(out var wind);
        if (!nk || !wk || !FoodNumber(wind))
        {
            return Unknown<double>();
        }
        return Known(night - wind);
    }
}

public static partial class PolicyFunctions
{
    /// <summary>Reports whether a crop's native sow tags include the grower's sow tag; unknown tags never match.</summary>
    public static bool GrowerAccepts(PlantGrower grower, CropChoice crop)
    {
        var tk = grower.SowTag.TryGetValue(out var tag);
        var ck = crop.SowTags.TryGetValue(out var tags);
        return tk && ck && !string.IsNullOrEmpty(tag) && tags.Contains(tag);
    }

    /// <summary>Reports a crop whose native minimum glow is zero, such as cave fungus, so an unlit roofed room is eligible for it.</summary>
    public static bool GrowsInDark(CropChoice crop) =>
        crop.MinGlow.TryGetValue(out var glow) && FoodNumber(glow) && glow <= 0;
}
