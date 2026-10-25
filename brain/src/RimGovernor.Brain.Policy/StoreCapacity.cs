namespace RimGovernor.Brain.Policy;

// A store's capacity facts and the yard's further-room ask. Runway (free stacks over net inflow) is read against
// an option's lead time, and stranded stock is the "now" signal; missing observations fail the review.

/// <summary>
/// One declared store's capacity now: its stacks held and holdable (the serving zones', built shelves included), its
/// net inflow (StoreInflow) and its stranded stacks (StrandedStacks). A standing zone and all its cell counts are required.
/// </summary>
public sealed record StoreCapacity
{
    public int Capacity { get; init; }

    public int Used { get; init; }

    /// <summary>Stacks per day; negative while the store drains.</summary>
    public StorageInflow Inflow { get; init; } = new StorageInflow.Unestablished();

    public long Stranded { get; init; }

    /// <summary>The stacks the store can still take.</summary>
    public int Free =>
        Math.Max(0, Capacity - Used);
}

public static partial class StoreRules
{
    /// <summary>The build work of a further yard: zoning outdoor ground builds nothing.</summary>
    public const double FurtherYardWorkTicks = 0;

    /// <summary>The store's capacity facts from the maintenance request.</summary>
    public static StoreCapacity StoreCapacity(StockpileRequest request, Store store)
    {
        var standing = false;
        var capacity = 0;
        var used = 0;

        foreach (var z in request.Zones)
        {
            if (!store.Serves(z))
            {
                continue;
            }
            standing = true;

            capacity += z.Capacity;
            used += z.Used;
        }
        return new StoreCapacity
        {
            Capacity = standing ? capacity : throw new RequiredFactException("standing store capacity"),
            Used = used,
            Inflow = StoreInflow(request, store),
            Stranded = StrandedStacks(request, store),
        };
    }

    /// <summary>
    /// A capacity option's lead time in days: its build work at base construction speed (one work tick per game
    /// tick) plus MaintenanceHorizonDays. Work is required for a priced option.
    /// </summary>
    public static double StorageLeadDays(double workTicks) =>
        (workTicks / GameTime.TicksPerDay) + ColonyDiagnosticsRules.MaintenanceHorizonDays;

    /// <summary>
    /// The materials yards layout should hold (RoomDemand.Yard): one more than the declared yards once every one
    /// stands and either a yard has stranded stacks (now) or the yards' runway (their free stacks summed over their
    /// inflow summed) is shorter than a further yard's lead time; 0 asks for none. A yard whose zone does not stand
    /// yet is a wait, and a store accumulating history asks only for stranded stock.
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
            if (s.Retired || s.Role != StockpileRolesConstants.YardRole)
            {
                continue;
            }
            if (!request.Zones.Any(s.Serves))
            {
                return 0;
            }
            yards++;
            var c = StoreCapacity(request, s);
            stranded |= c.Stranded > 0;
            free += c.Free;
            switch (c.Inflow)
            {
                case StorageInflow.Measured(var rate): inflow += rate; break;
                case StorageInflow.Accumulating: runwayKnown = false; break;
                case StorageInflow.Unestablished: runwayKnown = false; break;
            }
        }
        if (yards == 0)
        {
            return 0;
        }
        var due = runwayKnown && StoreRunwayDays(free, inflow) < StorageLeadDays(FurtherYardWorkTicks);
        return stranded || due ? yards + 1 : 0;
    }

    /// <summary>Free stacks over net inflow in days; infinite when inflow is not positive.</summary>
    internal static double StoreRunwayDays(int free, double inflow) => inflow <= 0 ? double.PositiveInfinity : free / inflow;
}
