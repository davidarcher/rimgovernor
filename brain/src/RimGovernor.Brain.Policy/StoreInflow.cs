
namespace RimGovernor.Brain.Policy;

// Store inflow. Native samples the stacks each storage group holds at every hour's first tick into a saved ring
// (RimGovernor.Brain.GameFacts.Observations.StorageOccupancySnapshot); a store's net inflow is the slope of its groups' held stacks over a recent window.

public static partial class StoreRules
{
    /// <summary>The window a store's inflow is read over: recent enough to follow a change in hauling.</summary>
    public const int StoreInflowWindowDays = 3;

    /// <summary>The shortest span of one group's samples that gives a rate: one game day.</summary>
    public const int StoreInflowMinHours = 24;

    /// <summary>The last hour an inflow read does not need, for the read at <paramref name="tick"/>.</summary>
    public static int StoreInflowSinceHour(Tick tick) => (int)(tick.Value / ResourceRules.ConsumptionHourTicks) - (StoreInflowWindowDays * 24);

    /// <summary>
    /// The store's net inflow in stacks per day: the sum, over the zones serving it and their built shelves, of each
    /// group's least-squares slope of held stacks over the window. Negative is a known reading (the store drains).
    /// The native ring is required. A store without groups is Unestablished; one whose history spans less than a day
    /// is Accumulating. A group created mid-window counts once it has a day of its own samples.
    /// </summary>
    public static StorageInflow StoreInflow(StockpileRequest request, Store store)
    {
        var occupancy = request.Occupancy ?? throw new RequiredFactException("storage occupancy");
        var groups = StoreGroups(request, store);
        if (groups.Count == 0)
        {
            return new StorageInflow.Unestablished();
        }
        var total = 0.0;
        foreach (var group in groups)
        {
            if (GroupInflow(occupancy, group) is not StorageInflow.Measured(var rate))
            {
                return new StorageInflow.Accumulating();
            }
            total += rate;
        }
        return new StorageInflow.Measured(total);
    }

    /// <summary>One group's held-stack slope in stacks per day over the window; Accumulating under a day of samples.</summary>
    internal static StorageInflow GroupInflow(RimGovernor.Brain.GameFacts.Observations.StorageOccupancySnapshot occupancy, string group)
    {
        if (occupancy.Groups.FirstOrDefault(g => g.Group == group)?.Samples is not { } samples)
        {
            return new StorageInflow.Accumulating();
        }
        var start = occupancy.CurrentHour - (StoreInflowWindowDays * 24);
        var points = samples.Where(s => s.Hour > start && s.Hour <= occupancy.CurrentHour).ToList();
        if (points.Count < 2 || points[^1].Hour - points[0].Hour < StoreInflowMinHours)
        {
            return new StorageInflow.Accumulating();
        }
        var meanHour = points.Average(s => (double)s.Hour);
        var meanStacks = points.Average(s => (double)s.Stacks);
        var covariance = 0.0;
        var variance = 0.0;
        foreach (var s in points)
        {
            covariance += (s.Hour - meanHour) * (s.Stacks - meanStacks);
            variance += (s.Hour - meanHour) * (s.Hour - meanHour);
        }
        return new StorageInflow.Measured(covariance / variance * 24);
    }
}

/// <summary>Rate evidence is unavailable until a store exists and its groups have a day of history.</summary>
public abstract record StorageInflow
{
    private StorageInflow() { }
    public sealed record Unestablished : StorageInflow;
    public sealed record Accumulating : StorageInflow;
    public sealed record Measured(double StacksPerDay) : StorageInflow;
}
