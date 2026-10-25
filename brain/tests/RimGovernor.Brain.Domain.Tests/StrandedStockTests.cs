using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>#3235: stranded stock is attributed to the store whose zones or shelves accept a stack with no room.</summary>
public sealed class StrandedStockTests
{
    private static readonly Rectangle YardRoom = new() { X = 10, Z = 10, Width = 4, Height = 3 };
    private static readonly Rectangle WarehouseRoom = new() { X = 30, Z = 10, Width = 4, Height = 3 };

    private static readonly Policy.Store Yard = new() { Site = new StoreSite { Role = "yard", Interior = YardRoom } };
    private static readonly Policy.Store Warehouse = new() { Site = new StoreSite { Role = "general", Interior = WarehouseRoom } };

    private static StockpileZone Zone(string id, string role, Rectangle room) =>
        new() { Id = id, Role = role, Cells = PlannedRules.RectCells(room), Filter = new StockpileFilter(FilterBase.Nothing) };

    private static StockpileRequest Request(params LootItem[] rows) => new()
    {
        Zones = [Zone("Zone_yard", "yard", YardRoom), Zone("Zone_warehouse", "general", WarehouseRoom)],
        Shelves = [new StockpileShelf { Building = "Shelf_7", Zone = "Zone_warehouse" }],
        Loot = EquatableArray.From(rows),
    };

    private static LootItem Row(string thing, long headroom, params string[] acceptedBy) =>
        LootFixtures.Stack(thing, "Steel", new Cell(50, 50), false,
            new RimGovernor.Contracts.Wire.Observations.HaulSafety.Safe { PathLength = 0 }, 10, headroom, acceptedBy, null);

    [Fact]
    public void AStackOnlyTheYardAcceptsIsStrandedAtTheYardAlone()
    {
        var request = Request(Row("Thing_1", 0, "Zone_yard"), Row("Thing_2", 0, "Zone_yard"));
        Assert.Equal(2L, StoreRules.StrandedStacks(request, Yard));
        Assert.Equal(0L, StoreRules.StrandedStacks(request, Warehouse));
    }

    [Fact]
    public void AShelfInAStoresZoneCountsForThatStore()
    {
        var request = Request(Row("Thing_1", 0, "Shelf_7"));
        Assert.Equal(1L, StoreRules.StrandedStacks(request, Warehouse));
        Assert.Equal(0L, StoreRules.StrandedStacks(request, Yard));
    }

    [Fact]
    public void AStackNoStoreAcceptsIsStrandedNowhere()
    {
        var request = Request(Row("Thing_1", 0));
        Assert.Equal(0L, StoreRules.StrandedStacks(request, Yard));
        Assert.Equal(0L, StoreRules.StrandedStacks(request, Warehouse));
    }

    [Fact]
    public void RoomForbiddenAndUnsafeStacksAreNotStranded()
    {
        var forbidden = Row("Thing_2", 0, "Zone_yard");
        var unsafeStack = Row("Thing_3", 0, "Zone_yard");
        var request = Request(
            Row("Thing_1", 75, "Zone_yard"),
            forbidden with { Observed = forbidden.Observed with { Forbidden = true } },
            unsafeStack with { Observed = unsafeStack.Observed with { Safety = new RimGovernor.Contracts.Wire.Observations.HaulSafety.Unsafe() } });
        Assert.Equal(0L, StoreRules.StrandedStacks(request, Yard));
    }

    [Fact]
    public void AnUnreachedStackCannotBeHauledToEitherStore()
    {
        var stack = Row("Thing_1", 0, "Zone_yard");
        var request = Request(stack with { Observed = stack.Observed with { Safety = new RimGovernor.Contracts.Wire.Observations.HaulSafety.Unreached() } });
        Assert.Equal(0L, StoreRules.StrandedStacks(request, Yard));
        Assert.Equal(0L, StoreRules.StrandedStacks(request, Warehouse));
    }

}
