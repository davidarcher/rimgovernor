using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3233: a store's capacity and use are counted in native stacks. A built shelf's cells leave the zone natively,
/// so the owned-zone view adds them back: capacity is the zone's floor cells plus 3 per shelf cell, used is the
/// stacks observed on all of them, and an unknown count stays unknown rather than reading as 0.
/// </summary>
public sealed class StoreStacksTests
{
    private const string ZoneId = "Zone_Stockpile_1";

    private static readonly OwnedZone Owned = new(
        ZoneId, ZoneKind.Stockpile, "", EquatableArray.From(new List<Cell>()), "general", AdminFamily.Stockpiles.FilingKey,
        new StockpileFilter(FilterBase.Nothing), StockpilePriority.Normal);

    // A 4-cell floor zone (0..3, 0) holding 1 stack, and a built 2x1 shelf on (0..1, 1) holding 2 then 0 stacks.
    private static List<SiteCell> Census(Fact<int> shelfStacks) =>
    [
        Floor(0, 1), Floor(1, 0), Floor(2, 0), Floor(3, 0),
        new SiteCell { Cell = new Cell(0, 1), StorageStacks = shelfStacks, StorageCapacity = Known(3) },
        new SiteCell { Cell = new Cell(1, 1), StorageStacks = Known(0), StorageCapacity = Known(3) },
    ];

    private static SiteCell Floor(int x, int stacks) =>
        new() { Cell = new Cell(x, 0), ZoneId = Known(ZoneId), StorageStacks = Known(stacks), StorageCapacity = Known(1) };

    private static readonly Dictionary<string, List<ShelfRecord>> Shelves = new()
    {
        [ZoneId] = [new ShelfRecord { Zone = ZoneId, Building = "Shelf1", Cells = [new Cell(0, 1), new Cell(1, 1)] }],
    };

    private static StockpileZone Zone(Fact<int> shelfStacks, IReadOnlyDictionary<string, List<ShelfRecord>> shelves)
    {
        var projection = ColonyFactsCoreTests.Projection() with { Cells = Census(shelfStacks) };
        return Assert.Single(StockpileZones.OwnedStockpileZones(projection, StockpileZones.CensusByCell(projection), [Owned], null, shelves));
    }

    [Fact]
    public void ZoneWithBuiltShelfCountsFloorAndShelfStacks()
    {
        var zone = Zone(Known(2), Shelves);

        Assert.Equal(4, zone.Cells.Count);
        Assert.Equal(Known(4 + (3 * 2)), zone.Capacity);
        Assert.Equal(Known(1 + 2), zone.Used);
    }

    [Fact]
    public void OpenShelfIsNotCapacityYet()
    {
        var open = new Dictionary<string, List<ShelfRecord>> { [ZoneId] = [Shelves[ZoneId][0] with { Open = true }] };

        var zone = Zone(Known(2), open);

        Assert.Equal(Known(4), zone.Capacity);
        Assert.Equal(Known(1), zone.Used);
    }

    [Fact]
    public void UnknownStackCountStaysUnknown()
    {
        var zone = Zone(Unknown<int>(), Shelves);

        Assert.Equal(Known(10), zone.Capacity);
        Assert.False(zone.Used.IsKnown);
        var count = Assert.Single(StockpileRules.StockpileRoleCounts([zone]));
        Assert.False(count.Used.IsKnown);
        Assert.Equal(Known(10), count.Capacity);
    }

    [Fact]
    public void ShelfCellMissingFromCensusIsUnknown()
    {
        var (capacity, used) = StockpileRules.StorageStacks([new Cell(0, 0), new Cell(9, 9)], new Dictionary<Cell, SiteCell> { [new Cell(0, 0)] = Floor(0, 1) });

        Assert.False(capacity.IsKnown);
        Assert.False(used.IsKnown);
    }
}
