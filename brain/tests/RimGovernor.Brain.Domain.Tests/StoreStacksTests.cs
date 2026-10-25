using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Facts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3233: a store's capacity and use are counted in native stacks. A built shelf's cells leave the zone natively,
/// so the owned-zone view adds them back: capacity is the zone's floor cells plus 3 per shelf cell, used is the
/// stacks observed on all of them, and a cell the census does not hold rejects the incomplete census.
/// </summary>
public sealed class StoreStacksTests
{
    private const string ZoneId = "Zone_Stockpile_1";

    private static readonly OwnedZone Owned = new(
        ZoneId, ZoneKind.Stockpile, "", EquatableArray.From(new List<Cell>()), "general", AdminFamily.Stockpiles.FilingKey,
        new StockpileFilter(FilterBase.Nothing), StockpilePriority.Normal);

    // A 4-cell floor zone (0..3, 0) holding 1 stack, and a built 2x1 shelf on (0..1, 1) holding 2 then 0 stacks.
    private static List<SiteCell> Census(int shelfStacks) =>
    [
        Floor(0, 1), Floor(1, 0), Floor(2, 0), Floor(3, 0),
        SiteCellFixtures.At(new Cell(0, 1), storageStacks: shelfStacks, storageCapacity: 3),
        SiteCellFixtures.At(new Cell(1, 1), storageCapacity: 3),
    ];

    private static SiteCell Floor(int x, int stacks) =>
        SiteCellFixtures.At(new Cell(x, 0), zoneId: ZoneId, storageStacks: stacks);

    private static readonly Dictionary<string, List<ShelfRecord>> Shelves = new()
    {
        [ZoneId] = [new ShelfRecord { Zone = ZoneId, Building = "Shelf1", Cells = [new Cell(0, 1), new Cell(1, 1)] }],
    };

    private static StockpileZone Zone(int shelfStacks, IReadOnlyDictionary<string, List<ShelfRecord>> shelves)
    {
        var projection = ColonyFactsCoreTests.Reading() with
        {
            Window = new Held<PlanningCells>
            {
                Value = new PlanningCells
                {
                    Cells = [.. Census(shelfStacks)]
                }
            }
        };
        return Assert.Single(StockpileZones.OwnedStockpileZones(projection, StockpileZones.CensusByCell(projection), [Owned], null, shelves));
    }

    [Fact]
    public void ZoneWithBuiltShelfCountsFloorAndShelfStacks()
    {
        var zone = Zone(2, Shelves);

        Assert.Equal(4, zone.Cells.Count);
        Assert.Equal(4 + (3 * 2), zone.Capacity);
        Assert.Equal(1 + 2, zone.Used);
    }

    [Fact]
    public void OpenShelfIsNotCapacityYet()
    {
        var open = new Dictionary<string, List<ShelfRecord>> { [ZoneId] = [Shelves[ZoneId][0] with { Open = true }] };

        var zone = Zone(2, open);

        Assert.Equal(4, zone.Capacity);
        Assert.Equal(1, zone.Used);
    }


    [Fact]
    public void ShelfCellMissingFromCensusIsRejected()
    {
        Assert.Throws<RequiredFactException>(() => StockpileRules.StorageStacks([new Cell(0, 0), new Cell(9, 9)], new Dictionary<Cell, SiteCell> { [new Cell(0, 0)] = Floor(0, 1) }));
    }
}
