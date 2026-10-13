namespace RimGovernor.Brain.Policy;

/// <summary>
/// Separates immediately usable stock from prospective ore. DaysLeft includes only known safe surface ore;
/// unknown ore counts as none. A zero observed rate has no finite runway, rather than an invented infinity.
/// </summary>
public sealed record ResourceRunway
{
    public Resource Resource { get; init; }

    public Tick Tick { get; init; }

    public double WindowDays { get; init; }

    public long Reserve { get; init; }

    public Fact<long> Stock { get; init; }

    public Fact<long> SurfaceOre { get; init; }

    public Fact<double> ConsumptionPerDay { get; init; }

    public Fact<double> StockDays { get; init; }

    public Fact<double> DaysLeft { get; init; }

    public Fact<bool> Deficit { get; init; }

    public long Target { get; init; }

    /// <summary>
    /// The stock the runway guards: the reserve plus the observed rate over the projection horizon. It is the
    /// target while the runway is in deficit and the trade's retained stock always. False while the rate is unread.
    /// </summary>
    public (long Line, bool Ok) ProtectedLine()
    {
        if (!ConsumptionPerDay.TryGetValue(out var rate) || Reserve < 0)
        {
            return (0, false);
        }
        return (Reserve + (long)Math.Ceiling(rate * PolicyFunctions.ProjectionHorizonDays), true);
    }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Reads the resource's recurring spend over the consumption window (resource_consumption.go). A window
    /// shorter than a day is read as a day, so a burst in the first hours is not a huge daily rate. Unknown
    /// consumption leaves the rate, and so the runway, unknown; unknown ore is no prospective supply.
    /// </summary>
    public static ResourceRunway ForecastResourceRunway(
        Resource resource, Fact<long> stock, Fact<long> ore, long reserve, Tick tick, Fact<ResourceConsumption> consumption)
    {
        var output = new ResourceRunway { Resource = resource, Tick = tick, Reserve = reserve, Stock = stock, SurfaceOre = ore };
        if (!consumption.TryGetValue(out var c) || reserve < 0)
        {
            return output;
        }
        output = output with { WindowDays = c.WindowDays };
        var rate = (double)c.Recurring.GetValueOrDefault(resource) / Math.Max(c.WindowDays, 1);
        output = output with { ConsumptionPerDay = Known(rate) };
        if (!stock.TryGetValue(out var n) || n < 0)
        {
            return output;
        }
        if (rate == 0)
        {
            return output with { Deficit = Known(n < reserve), Target = reserve };
        }
        var usable = Math.Max(0, n - reserve);
        output = output with { StockDays = Known(usable / rate) };
        ore.TryGetValue(out var remaining);
        var days = (usable + (double)Math.Max(0, remaining)) / rate;
        output = output with { DaysLeft = Known(days), Deficit = Known(days < ProjectionHorizonDays) };
        if (days < ProjectionHorizonDays)
        {
            output = output with { Target = output.ProtectedLine().Line };
        }
        return output;
    }

    public static Dictionary<Resource, long> ResourceRunwayTargets(IReadOnlyList<ResourceRunway> rows)
    {
        var output = new Dictionary<Resource, long>();
        foreach (var row in rows)
        {
            if (row.Target > 0)
            {
                output[row.Resource] = row.Target;
            }
        }
        return output;
    }

    /// <summary>
    /// Every resource a runway is forecast for and its reserve: every catalog medicine (the colony tends with
    /// whichever it holds, so a better medicine in use is counted too), the lowest-potency one (herbal)
    /// reserved at the medical reserve's TargetPerColonist doses per colonist so a colony never tended still
    /// stocks it; once doses are used the observed tend rate takes over. Without the catalog's medicines it
    /// adds nothing; an unread colonist count leaves the reserve zero. Every other resource the consumption
    /// ring shows recurring spend on (MaterialRunwayKeys) is a row with no reserve: its protected line is the
    /// observed rate over the horizon.
    /// </summary>
    public static Dictionary<Resource, long> RunwayReserves(
        this RoundsPolicy p, ItemFacts items, Fact<long> colonists, Fact<ResourceConsumption> consumption)
    {
        var materials = MaterialRunwayKeys(items, consumption);
        var output = new Dictionary<Resource, long>();
        var rank = 0;
        foreach (var medicine in items.MedicineTiers())
        {
            output[medicine] = 0;
            if (rank == 0)
            {
                colonists.TryGetValue(out var n);
                output[medicine] = Math.Max(0, n) * p.MedicalReserve.TargetPerColonist;
            }
            rank++;
        }
        foreach (var resource in materials)
        {
            output.TryAdd(resource, 0);
        }
        return output;
    }

    /// <summary>
    /// The resources the consumption ring shows recurring spend on, sorted: the colony's own use names them,
    /// no list does. Food (a catalog nutrition value) is left to the food ledger, which plans it.
    /// </summary>
    public static IReadOnlyList<Resource> MaterialRunwayKeys(ItemFacts items, Fact<ResourceConsumption> consumption)
    {
        if (!consumption.TryGetValue(out var c))
        {
            return [];
        }
        var output = new List<Resource>();
        foreach (var (resource, spent) in c.Recurring)
        {
            if (spent > 0 && items.Nutrition.GetValueOrDefault(resource) <= 0)
            {
                output.Add(resource);
            }
        }
        output.Sort();
        return output;
    }

    public static Fact<long> SurfaceOre(IReadOnlyList<ResourceSource> sources)
    {
        long total = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (source.Method != ResourceSourceMethod.Mine || !MineSafe(source.Safety) || source.Buried)
            {
                continue;
            }
            if (seen.Contains(source.ThingID) || source.Yield < 0 || source.Yield > long.MaxValue - total)
            {
                return Unknown<long>();
            }
            seen.Add(source.ThingID);
            total += source.Yield;
        }
        return Known(total);
    }
}
