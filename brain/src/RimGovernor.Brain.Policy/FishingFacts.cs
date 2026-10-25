namespace RimGovernor.Brain.Policy;

/// <summary>
/// The Fishing project: researched, pending with the days the fastest researcher needs to finish it, or without
/// progress (no researcher, or the pace is not a finite positive rate).
/// </summary>
public abstract record FishingResearch
{
    private protected FishingResearch() { }

    public sealed record Researched : FishingResearch;

    public sealed record Pending(double LeadDays) : FishingResearch;

    public sealed record NoProgress : FishingResearch;
}

/// <summary>
/// The colony's fishers at one water body: none able to work (no fisher, or every FishingSpeed is zero), or the
/// slowest working fisher's batch (fish per batch and work ticks per batch) and the crew's raw nutrition per day.
/// </summary>
public abstract record FishingCrew
{
    private protected FishingCrew() { }

    public sealed record NoFisher : FishingCrew;

    public sealed record Fishers(double FishPerBatch, double WorkTicksPerBatch, double CapacityPerDay) : FishingCrew;
}


public static class FishingFactsRules
{
    /// <summary>The planning assumption for one fisher or researcher day: eight work hours of 2500 ticks.</summary>
    public const int FishingWorkTicksPerDay = 8 * 2500;

    /// <summary>
    /// Proposes the zone footprint: the reachable fishable cells grown by cardinal
    /// flood fill from the cell nearest the colony centre (ties by x then z), at most one cell per available
    /// fisher. It is access capacity, never a population or yield multiplier.
    /// </summary>
    public static IReadOnlyList<Cell> FishingFootprint(IReadOnlyList<RimGovernor.Brain.GameFacts.Observations.FishableCell> cells, int fishers)
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
            foreach (var d in ExcavationReviewRules.OrthogonalSteps)
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
    /// The region verdict: some fisher reaches the water, and an unzoned body also
    /// offers a footprint of one cell per fisher.
    /// </summary>
    public static bool FishingReachable(bool reaches, bool zoned, int footprint, int fishers) =>
        reaches && (zoned || footprint >= fishers);

    /// <summary>The observed zone state: some zone is allowed, fishes forever and has fishable cells.</summary>
    public static bool FishingDelivering(IReadOnlyList<RimGovernor.Brain.GameFacts.Observations.FishingZoneFacts> zones) => zones.Any(z => z.Allowed && z.DoForever && z.HasFishableCells);

    /// <summary>
    /// Researched, or pending with the remaining project cost over the fastest researcher's daily points; no
    /// progress without a researcher or when that pace is not a finite positive rate.
    /// </summary>
    public static FishingResearch FishingResearchState(RimGovernor.Brain.GameFacts.Observations.FishableWater input)
    {
        if (input.FishingResearched)
        {
            return new FishingResearch.Researched();
        }
        var speed = 0.0;
        foreach (var s in input.ResearcherSpeeds)
        {
            speed = Math.Max(speed, s);
        }
        var lead = Math.Max(0, input.ResearchBaseCost - input.ResearchProgress) * input.ResearchCostFactor / (speed * input.ResearchPointsPerWorkTick * FishingWorkTicksPerDay * input.ResearchDifficultySpeedFactor);
        return speed > 0 && FoodForecastRules.FoodNumber(lead) ? new FishingResearch.Pending(lead) : new FishingResearch.NoProgress();
    }

    /// <summary>
    /// The fishers working one body: the slowest working fisher's batch (the population's yield at the lowest
    /// FishingYield, at least one fish; the base duration over the lowest FishingSpeed), and per fisher a day of
    /// batches at the population's yield as raw nutrition. A fisher with zero speed does not work.
    /// </summary>
    public static FishingCrew FishingCrewAt(IReadOnlyList<RimGovernor.Brain.GameFacts.Observations.FisherFacts> fishers, double yieldCurve, double nutritionPerFish, double baseTicks)
    {
        var working = fishers.Where(f => f.FishingSpeed > 0).ToList();
        if (working.Count == 0 || !(baseTicks > 0))
        {
            return new FishingCrew.NoFisher();
        }
        var sum = 0.0;
        foreach (var f in working)
        {
            sum += FishingWorkTicksPerDay * Math.Max(1, Math.Round(yieldCurve * f.FishingYield, MidpointRounding.ToEven)) * nutritionPerFish * f.FishingSpeed / baseTicks;
        }
        var batch = Math.Max(1, Math.Round(yieldCurve * working.Min(f => f.FishingYield), MidpointRounding.ToEven));
        return new FishingCrew.Fishers(batch, baseTicks / working.Min(f => f.FishingSpeed), sum);
    }
}
