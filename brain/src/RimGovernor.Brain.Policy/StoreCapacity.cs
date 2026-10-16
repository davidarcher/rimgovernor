namespace RimGovernor.Brain.Policy;

// A store's capacity facts and the yard's further-room ask. Runway (free stacks over net inflow) is read against
// an option's lead time, and stranded stock is the "now" signal; an unknown fact stays unknown and asks nothing.

/// <summary>
/// One declared store's capacity now: its stacks held and holdable (the serving zones', built shelves included), its
/// net inflow (StoreInflow) and its stranded stacks (StrandedStacks). Capacity and Used are unknown while no zone
/// serves the store or any serving zone's count is unknown.
/// </summary>
public sealed record StoreCapacity
{
    public Fact<int> Capacity { get; init; }

    public Fact<int> Used { get; init; }

    /// <summary>Stacks per day; negative while the store drains.</summary>
    public Fact<double> Inflow { get; init; }

    public Fact<long> Stranded { get; init; }

    /// <summary>The stacks the store can still take; unknown while either count is.</summary>
    public Fact<int> Free =>
        Capacity.TryGetValue(out var capacity) && Used.TryGetValue(out var used) ? Known(Math.Max(0, capacity - used)) : Unknown<int>();

    /// <summary>
    /// The days until the store fills: free stacks over net inflow. Known infinite when inflow is not positive (the
    /// store never fills at this rate); unknown while free stacks or inflow are.
    /// </summary>
    public Fact<double> RunwayDays
    {
        get
        {
            if (!Free.TryGetValue(out var free) || !Inflow.TryGetValue(out var inflow))
            {
                return Unknown<double>();
            }
            return Known(PolicyFunctions.StoreRunwayDays(free, inflow));
        }
    }
}

public static partial class PolicyFunctions
{
    /// <summary>The build work of a further yard: zoning outdoor ground builds nothing.</summary>
    public const double FurtherYardWorkTicks = 0;

    /// <summary>The store's capacity facts from the maintenance request.</summary>
    public static StoreCapacity StoreCapacity(StockpileRequest request, Store store)
    {
        var standing = false;
        var capacity = 0;
        var used = 0;
        var capacityKnown = true;
        var usedKnown = true;
        foreach (var z in request.Zones)
        {
            if (!store.Serves(z))
            {
                continue;
            }
            standing = true;
            capacityKnown &= z.Capacity.TryGetValue(out var c);
            usedKnown &= z.Used.TryGetValue(out var u);
            capacity += c;
            used += u;
        }
        return new StoreCapacity
        {
            Capacity = standing && capacityKnown ? Known(capacity) : Unknown<int>(),
            Used = standing && usedKnown ? Known(used) : Unknown<int>(),
            Inflow = StoreInflow(request, store),
            Stranded = StrandedStacks(request, store),
        };
    }

    /// <summary>
    /// A capacity option's lead time in days: its build work at base construction speed (one work tick per game
    /// tick) plus MaintenanceHorizonDays. Unknown work leaves the option unvalued.
    /// </summary>
    public static Fact<double> StorageLeadDays(Fact<double> workTicks) =>
        workTicks.TryGetValue(out var work) ? Known((work / GameTime.TicksPerDay) + MaintenanceHorizonDays) : Unknown<double>();

    /// <summary>
    /// The materials yards layout should hold (RoomDemand.Yard): one more than the declared yards once every one
    /// stands and either a yard has stranded stacks (now) or the yards' runway (their free stacks summed over their
    /// inflow summed) is shorter than a further yard's lead time; 0 asks for none. A yard whose zone does not stand
    /// yet is a wait, and an unknown runway with no known stranded stock asks nothing.
    /// </summary>
    public static int YardDemand(StockpileRequest request)
    {
        var yards = 0;
        var stranded = false;
        var free = 0;
        var inflow = 0.0;
        var runwayKnown = true;
        foreach (var s in request.Stores)
        {
            if (s.Retired || s.Role != DomainConstants.YardRole)
            {
                continue;
            }
            if (!request.Zones.Any(s.Serves))
            {
                return 0;
            }
            yards++;
            var c = StoreCapacity(request, s);
            stranded |= c.Stranded.TryGetValue(out var n) && n > 0;
            runwayKnown &= c.Free.TryGetValue(out var f) & c.Inflow.TryGetValue(out var i);
            free += f;
            inflow += i;
        }
        if (yards == 0)
        {
            return 0;
        }
        var due = runwayKnown && StorageLeadDays(Known(FurtherYardWorkTicks)).TryGetValue(out var lead) && StoreRunwayDays(free, inflow) < lead;
        return stranded || due ? yards + 1 : 0;
    }

    /// <summary>Free stacks over net inflow in days; infinite when inflow is not positive.</summary>
    internal static double StoreRunwayDays(int free, double inflow) => inflow <= 0 ? double.PositiveInfinity : free / inflow;
}
