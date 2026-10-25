namespace RimGovernor.Brain.Policy;

/// <summary>
/// What the consumption ring says of a resource's runway: Unrated while the ring is unread, Idle when nothing
/// was spent in the window (no finite runway, rather than an invented infinity) with its Deficit against the
/// reserve, or Burning at PerDay with the days the usable stock lasts (StockDays), the days it and the safe
/// surface ore last (DaysLeft), and whether DaysLeft is under the maintenance horizon (Deficit).
/// </summary>
public abstract record RunwayRating
{
    private protected RunwayRating() { }

    public sealed record Unrated : RunwayRating;

    public sealed record Idle(bool Deficit) : RunwayRating;

    public sealed record Burning(double PerDay, double StockDays, double DaysLeft, bool Deficit) : RunwayRating;

    /// <summary>The observed use per day; false while the ring is unread. Idle uses nothing.</summary>
    public bool TryRate(out double perDay)
    {
        perDay = this is Burning burning ? burning.PerDay : 0;
        return this is Burning || this is Idle;
    }
}

/// <summary>
/// Separates immediately usable stock from prospective ore. DaysLeft includes only safe surface ore; a resource
/// with no readable ore row has none.
/// </summary>
public sealed record ResourceRunway
{
    public Resource Resource { get; init; }

    public Tick Tick { get; init; }

    public double WindowDays { get; init; }

    public long Reserve { get; init; }

    public long Stock { get; init; }

    public long SurfaceOre { get; init; }

    public RunwayRating Rating { get; init; } = new RunwayRating.Unrated();

    public long Target { get; init; }

    /// <summary>Whether the rate is read and the usable stock falls short of the horizon (an Idle runway short of its reserve counts).</summary>
    public bool InDeficit => Rating is RunwayRating.Idle { Deficit: true } or RunwayRating.Burning { Deficit: true };

    /// <summary>
    /// The stock the runway guards: the reserve plus the observed rate over the projection horizon. It is the
    /// target while the runway is in deficit and the trade's retained stock always. False while the rate is unread.
    /// </summary>
    public bool TryProtectedLine(out long line)
    {
        if (!Rating.TryRate(out var rate))
        {
            line = 0;
            return false;
        }
        line = Reserve + (long)Math.Ceiling(rate * ColonyDiagnosticsRules.MaintenanceHorizonDays);
        return true;
    }
}

public static partial class ResourceRules
{
    /// <summary>
    /// Reads the resource's recurring spend over the consumption window. A window
    /// shorter than a day is read as a day, so a burst in the first hours is not a huge daily rate. An unread
    /// ring leaves the runway Unrated; a negative reserve or stock throws <see cref="DomainException"/>.
    /// </summary>
    public static ResourceRunway ForecastResourceRunway(
        Resource resource, long stock, long ore, long reserve, Tick tick, ResourceConsumption? consumption)
    {
        if (reserve < 0 || stock < 0 || ore < 0)
        {
            throw new DomainException($"resource runway {resource.Value} has a negative reserve, stock or ore");
        }
        var output = new ResourceRunway { Resource = resource, Tick = tick, Reserve = reserve, Stock = stock, SurfaceOre = ore };
        if (consumption is not { } c)
        {
            return output;
        }
        output = output with { WindowDays = c.WindowDays };
        var rate = (double)c.Recurring.GetValueOrDefault(resource) / Math.Max(c.WindowDays, 1);
        if (rate == 0)
        {
            return output with { Rating = new RunwayRating.Idle(stock < reserve), Target = reserve };
        }
        var usable = Math.Max(0, stock - reserve);
        var days = (usable + (double)ore) / rate;
        var deficit = days < ColonyDiagnosticsRules.MaintenanceHorizonDays;
        output = output with { Rating = new RunwayRating.Burning(rate, usable / rate, days, deficit) };
        if (deficit)
        {
            output = output with { Target = reserve + (long)Math.Ceiling(rate * ColonyDiagnosticsRules.MaintenanceHorizonDays) };
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
    /// adds nothing. Every other resource the consumption
    /// ring shows recurring spend on (MaterialRunwayKeys) is a row with no reserve: its protected line is the
    /// observed rate over the horizon.
    /// </summary>
    public static Dictionary<Resource, long> RunwayReserves(
        this RoundsPolicy p, ItemFacts items, long colonists, ResourceConsumption? consumption)
    {
        var materials = MaterialRunwayKeys(items, consumption);
        var output = new Dictionary<Resource, long>();
        var rank = 0;
        foreach (var medicine in items.MedicineTiers())
        {
            output[medicine] = 0;
            if (rank == 0)
            {
                output[medicine] = Math.Max(0, colonists) * p.MedicalReserve.TargetPerColonist;
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
    public static IReadOnlyList<Resource> MaterialRunwayKeys(ItemFacts items, ResourceConsumption? consumption)
    {
        if (consumption is not { } c)
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

    /// <summary>The yield of the safe, reachable mine sources, each thing once; a negative or repeated source throws <see cref="DomainException"/>.</summary>
    public static long SurfaceOre(IReadOnlyList<ResourceSource> sources)
    {
        long total = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (source.Method != ResourceSourceMethod.Mine || !MineSafe(source.Safety) || source.Buried)
            {
                continue;
            }
            if (!seen.Add(source.ThingId) || source.Yield < 0 || source.Yield > long.MaxValue - total)
            {
                throw new DomainException($"surface ore source {source.ThingId} is repeated, negative or overflows");
            }
            total += source.Yield;
        }
        return total;
    }
}
