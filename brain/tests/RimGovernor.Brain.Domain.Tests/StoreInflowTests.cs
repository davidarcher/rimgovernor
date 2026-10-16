using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>#3234: a store's net inflow is the slope of its groups' held stacks in the native occupancy ring.</summary>
public sealed class StoreInflowTests
{
    private const int Now = 200;

    private static readonly Rectangle YardRoom = new() { X = 10, Z = 10, Width = 4, Height = 3 };
    private static readonly Rectangle WarehouseRoom = new() { X = 30, Z = 10, Width = 4, Height = 3 };

    private static readonly Policy.Store Yard = new() { Site = new StoreSite { Role = "yard", Interior = YardRoom } };
    private static readonly Policy.Store Warehouse = new() { Site = new StoreSite { Role = "general", Interior = WarehouseRoom } };

    private static StockpileZone Zone(string id, string role, Rectangle room) =>
        new() { Id = id, Role = role, Cells = RectCells(room), Filter = new StockpileFilter(FilterBase.Nothing) };

    private static StockpileRequest Request(Dictionary<string, EquatableArray<OccupancySample>> groups) => new()
    {
        Zones = [Zone("Zone_yard", "yard", YardRoom), Zone("Zone_warehouse", "general", WarehouseRoom)],
        Shelves = [new StockpileShelf { Building = "Shelf_7", Zone = "Zone_warehouse" }],
        Occupancy = Known(new StoreOccupancy { CurrentHour = Now, Groups = groups }),
    };

    private static EquatableArray<OccupancySample> Ring(int from, int to, Func<int, int> stacks) =>
        EquatableArray.From(Enumerable.Range(from, to - from + 1).Select(h => new OccupancySample { Hour = h, Stacks = stacks(h) }));

    private static double Rate(Fact<double> inflow)
    {
        Assert.True(inflow.TryGetValue(out var rate), "inflow unknown");
        return rate;
    }

    [Fact]
    public void AFillingYardReadsItsSlopePerDay()
    {
        // Two stacks an hour inside the window; samples older than the window are ignored.
        var request = Request(new()
        {
            ["Zone_yard"] = Ring(0, Now, h => h <= Now - (StoreInflowWindowDays * 24) ? 500 - h : 2 * h),
        });
        Assert.Equal(48, Rate(StoreInflow(request, Yard)), 6);
    }

    [Fact]
    public void ADrainingStoreReadsANegativeKnownInflow()
    {
        var request = Request(new()
        {
            ["Zone_warehouse"] = Ring(Now - 71, Now, h => 300 - h),
            ["Shelf_7"] = Ring(Now - 71, Now, _ => 6),
        });
        Assert.Equal(-24, Rate(StoreInflow(request, Warehouse)), 6);
    }

    [Fact]
    public void UnderADayOfSamplesReadsUnknownNotZero()
    {
        var request = Request(new() { ["Zone_yard"] = Ring(Now - 10, Now, h => h) });
        Assert.False(StoreInflow(request, Yard).IsKnown);
    }

    [Fact]
    public void AStoreWhoseGroupHasNoSamplesReadsUnknown()
    {
        var request = Request(new() { ["Zone_yard"] = Ring(Now - 71, Now, _ => 3) });
        Assert.Equal(0, Rate(StoreInflow(request, Yard)), 6);
        Assert.False(StoreInflow(request, Warehouse).IsKnown);
    }

    [Fact]
    public void AShelfBuiltMidWindowAddsItsOwnSlopeOnceItHasADay()
    {
        var request = Request(new()
        {
            ["Zone_warehouse"] = Ring(Now - 71, Now, _ => 10),
            ["Shelf_7"] = Ring(Now - 40, Now, h => h - (Now - 40)),
        });
        Assert.Equal(24, Rate(StoreInflow(request, Warehouse)), 6);

        var young = Request(new()
        {
            ["Zone_warehouse"] = Ring(Now - 71, Now, _ => 10),
            ["Shelf_7"] = Ring(Now - 5, Now, h => h),
        });
        Assert.False(StoreInflow(young, Warehouse).IsKnown);
    }

    [Fact]
    public void ADeletedGroupDropsOut()
    {
        // The old yard zone's samples remain in the ring, but no zone names it now.
        var request = Request(new()
        {
            ["Zone_yard"] = Ring(Now - 71, Now, _ => 4),
            ["Zone_old"] = Ring(Now - 71, Now, h => 10 * h),
        });
        Assert.Equal(0, Rate(StoreInflow(request, Yard)), 6);
    }

    [Fact]
    public void AnUnreadRingReadsUnknown()
    {
        var request = Request([]) with { Occupancy = Unknown<StoreOccupancy>() };
        Assert.False(StoreInflow(request, Yard).IsKnown);
    }

    [Fact]
    public void TheReadAsksForTheInflowWindow()
    {
        Assert.Equal(Now - (StoreInflowWindowDays * 24), StoreInflowSinceHour(new Tick(((long)Now * ConsumptionHourTicks) + 7)));
    }
}
