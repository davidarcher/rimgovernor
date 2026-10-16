namespace RimGovernor.Brain.Policy;

// Store inflow. Native samples the stacks each storage group holds at every hour's first tick into a saved ring;
// a store's net inflow is the slope of its groups' held stacks over a recent window.

/// <summary>One ring sample: the stacks a storage group held at the start of Hour.</summary>
public sealed record OccupancySample
{
    public int Hour { get; init; }

    public int Stacks { get; init; }
}

/// <summary>
/// One native read: each requested storage group's samples (ascending) inside the inflow window. A group missing
/// here has no samples; CurrentHour is the hour running when it was read.
/// </summary>
public sealed record StoreOccupancy
{
    public int CurrentHour { get; init; }

    public IReadOnlyDictionary<string, EquatableArray<OccupancySample>> Groups { get; init; } = new Dictionary<string, EquatableArray<OccupancySample>>();
}

public static partial class PolicyFunctions
{
    /// <summary>The window a store's inflow is read over: recent enough to follow a change in hauling.</summary>
    public const int StoreInflowWindowDays = 3;

    /// <summary>The shortest span of one group's samples that gives a rate: one game day.</summary>
    public const int StoreInflowMinHours = 24;

    /// <summary>The last hour an inflow read does not need, for the read at <paramref name="tick"/>.</summary>
    public static int StoreInflowSinceHour(Tick tick) => (int)(tick.Value / ConsumptionHourTicks) - (StoreInflowWindowDays * 24);

    /// <summary>
    /// The store's net inflow in stacks per day: the sum, over the zones serving it and their built shelves, of each
    /// group's least-squares slope of held stacks over the window. Negative is a known reading (the store drains).
    /// Unknown, never zero, when the ring is unread, the store has no group, or any group's samples span less than
    /// a day (a group created mid-window counts once it has a day of its own samples).
    /// </summary>
    public static Fact<double> StoreInflow(StockpileRequest request, Store store)
    {
        if (!request.Occupancy.TryGetValue(out var occupancy))
        {
            return Unknown<double>();
        }
        var groups = StoreGroups(request, store);
        if (groups.Count == 0)
        {
            return Unknown<double>();
        }
        var total = 0.0;
        foreach (var group in groups)
        {
            if (!GroupInflow(occupancy, group).TryGetValue(out var rate))
            {
                return Unknown<double>();
            }
            total += rate;
        }
        return Known(total);
    }

    /// <summary>One group's held-stack slope in stacks per day over the window; unknown under a day of samples.</summary>
    internal static Fact<double> GroupInflow(StoreOccupancy occupancy, string group)
    {
        if (!occupancy.Groups.TryGetValue(group, out var samples))
        {
            return Unknown<double>();
        }
        var start = occupancy.CurrentHour - (StoreInflowWindowDays * 24);
        var points = samples.Where(s => s.Hour > start && s.Hour <= occupancy.CurrentHour).ToList();
        if (points.Count < 2 || points[^1].Hour - points[0].Hour < StoreInflowMinHours)
        {
            return Unknown<double>();
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
        return Known(covariance / variance * 24);
    }
}
