namespace RimGovernor.Brain.Policy;

/// <summary>A fishable water cell with whether any available fisher can reach it, and its squared distance to the colony centre. (Go <c>FishingCell</c>.)</summary>
public sealed record FishingCell
{
    public Cell Cell { get; init; }

    public bool Reachable { get; init; }

    public double DistanceSquared { get; init; }
}

/// <summary>A fishing zone touching a water body. (Go <c>FishingZoneState</c>.)</summary>
public sealed record FishingZoneState(bool Allowed, bool DoForever, bool HasFishableCells);

/// <summary>The raw native facts of the Fishing project. (Go <c>FishingResearchInputs</c>.)</summary>
public sealed record FishingResearchInputs
{
    public Fact<bool> Researched { get; init; }

    public Fact<double> BaseCost { get; init; }

    public Fact<double> Progress { get; init; }

    public Fact<double> CostFactor { get; init; }

    public IReadOnlyList<double> ResearcherSpeeds { get; init; } = [];

    public Fact<double> PointsPerWorkTick { get; init; }

    public Fact<double> Difficulty { get; init; }
}

/// <summary>One available fisher's native rates. (Go <c>Fisher</c>.)</summary>
public sealed record Fisher(double Yield, double Speed);

public static partial class PolicyFunctions
{
    /// <summary>The planning assumption for one fisher or researcher day: eight work hours of 2500 ticks.</summary>
    public const int FishingWorkTicksPerDay = 8 * 2500;

    /// <summary>
    /// Go <c>FishingFootprint</c>: proposes the zone footprint: the reachable fishable cells grown by cardinal
    /// flood fill from the cell nearest the colony centre (ties by x then z), at most one cell per available
    /// fisher. It is access capacity, never a population or yield multiplier.
    /// </summary>
    public static IReadOnlyList<Cell> FishingFootprint(IReadOnlyList<FishingCell> cells, int fishers)
    {
        var free = cells.Where(c => c.Reachable).ToList();
        if (free.Count == 0 || fishers <= 0)
        {
            return [];
        }
        free = [.. free.OrderBy(c => c.DistanceSquared).ThenBy(c => c.Cell.X).ThenBy(c => c.Cell.Z)];
        var available = new HashSet<Cell>(free.Select(c => c.Cell));
        var first = free[0].Cell;
        available.Remove(first);
        var pending = new Queue<Cell>();
        pending.Enqueue(first);
        var result = new List<Cell>();
        while (pending.Count > 0 && result.Count < fishers)
        {
            var c = pending.Dequeue();
            result.Add(c);
            foreach (var d in OrthogonalSteps)
            {
                var n = new Cell(c.X + d.X, c.Z + d.Z);
                if (available.Remove(n))
                {
                    pending.Enqueue(n);
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Go <c>FishingReachable</c>: the region verdict: some fisher reaches the water, and an unzoned body also
    /// offers a footprint of one cell per fisher.
    /// </summary>
    public static Fact<bool> FishingReachable(Fact<bool> reaches, Fact<bool> zoned, int footprint, int fishers) =>
        zoned.TryGetValue(out var z) && !z && footprint < fishers ? Known(false) : reaches;

    /// <summary>Go <c>FishingDelivering</c>: the observed zone state: some zone is allowed, fishes forever and has fishable cells.</summary>
    public static bool FishingDelivering(IReadOnlyList<FishingZoneState> zones) => zones.Any(z => z.Allowed && z.DoForever && z.HasFishableCells);

    /// <summary>
    /// Go <c>FishingResearchLeadDays</c>: zero once researched; otherwise the remaining project cost over the
    /// fastest researcher's daily points. It is unknown without a researcher or an unread input.
    /// </summary>
    public static Fact<double> FishingResearchLeadDays(FishingResearchInputs input)
    {
        if (!input.Researched.TryGetValue(out var researched))
        {
            return Unknown<double>();
        }
        if (researched)
        {
            return Known(0.0);
        }
        var speed = 0.0;
        foreach (var s in input.ResearcherSpeeds)
        {
            speed = Math.Max(speed, s);
        }
        var bk = input.BaseCost.TryGetValue(out var baseCost);
        var pk = input.Progress.TryGetValue(out var progress);
        var fk = input.CostFactor.TryGetValue(out var factor);
        var tk = input.PointsPerWorkTick.TryGetValue(out var points);
        var sk = input.Difficulty.TryGetValue(out var scale);
        if (!bk || !pk || !fk || !tk || !sk || speed <= 0)
        {
            return Unknown<double>();
        }
        var lead = Math.Max(0, baseCost - progress) * factor / (speed * points * FishingWorkTicksPerDay * scale);
        return double.IsNaN(lead) || double.IsInfinity(lead) ? Unknown<double>() : Known(lead);
    }

    /// <summary>
    /// Go <c>FishingWorkCapacity</c>: the raw nutrition per day the fishers can draw from a body: per fisher, a
    /// day of batches at the population's yield. It is unknown without a known nutrition per fish, yield curve
    /// value or batch duration.
    /// </summary>
    public static Fact<double> FishingWorkCapacity(IReadOnlyList<Fisher> fishers, Fact<double> yieldCurve, Fact<double> nutritionPerFish, Fact<double> baseTicks)
    {
        var ck = yieldCurve.TryGetValue(out var curve);
        var nk = nutritionPerFish.TryGetValue(out var nutrition);
        var bk = baseTicks.TryGetValue(out var baseValue);
        if (!ck || !nk || !bk || baseValue <= 0)
        {
            return Unknown<double>();
        }
        var sum = 0.0;
        foreach (var f in fishers)
        {
            sum += FishingWorkTicksPerDay * Math.Max(1, Math.Round(curve * f.Yield, MidpointRounding.ToEven)) * nutrition * f.Speed / baseValue;
        }
        return double.IsNaN(sum) || double.IsInfinity(sum) ? Unknown<double>() : Known(sum);
    }
}
