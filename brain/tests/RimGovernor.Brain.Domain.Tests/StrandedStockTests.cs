using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>#3235: stranded stock is attributed to the store whose zones or shelves accept a stack with no room.</summary>
public sealed class StrandedStockTests
{
    private static readonly Rectangle YardRoom = new() { X = 10, Z = 10, Width = 4, Height = 3 };
    private static readonly Rectangle WarehouseRoom = new() { X = 30, Z = 10, Width = 4, Height = 3 };

    private static readonly Policy.Store Yard = new() { Site = new StoreSite { Role = "yard", Interior = YardRoom } };
    private static readonly Policy.Store Warehouse = new() { Site = new StoreSite { Role = "general", Interior = WarehouseRoom } };

    private static StockpileZone Zone(string id, string role, Rectangle room) =>
        new() { Id = id, Role = role, Cells = RectCells(room), Filter = new StockpileFilter(FilterBase.Nothing) };

    private static StockpileRequest Request(params LootItem[] rows) => new()
    {
        Zones = [Zone("Zone_yard", "yard", YardRoom), Zone("Zone_warehouse", "general", WarehouseRoom)],
        Shelves = [new StockpileShelf { Building = "Shelf_7", Zone = "Zone_warehouse" }],
        Loot = Known(EquatableArray.From(rows)),
    };

    private static LootItem Row(string thing, long headroom, params string[] acceptedBy) => new()
    {
        Supply = new StartingSupply { Thing = thing, Definition = "Steel", Cell = new Cell(50, 50) },
        SafeToHaul = true,
        SafetyKnown = true,
        Count = 10,
        StorageHeadroom = Known(headroom),
        AcceptedBy = EquatableArray.From(acceptedBy),
    };

    [Fact]
    public void AStackOnlyTheYardAcceptsIsStrandedAtTheYardAlone()
    {
        var request = Request(Row("Thing_1", 0, "Zone_yard"), Row("Thing_2", 0, "Zone_yard"));
        Assert.Equal(Known(2L), StrandedStacks(request, Yard));
        Assert.Equal(Known(0L), StrandedStacks(request, Warehouse));
    }

    [Fact]
    public void AShelfInAStoresZoneCountsForThatStore()
    {
        var request = Request(Row("Thing_1", 0, "Shelf_7"));
        Assert.Equal(Known(1L), StrandedStacks(request, Warehouse));
        Assert.Equal(Known(0L), StrandedStacks(request, Yard));
    }

    [Fact]
    public void AStackNoStoreAcceptsIsStrandedNowhere()
    {
        var request = Request(Row("Thing_1", 0));
        Assert.Equal(Known(0L), StrandedStacks(request, Yard));
        Assert.Equal(Known(0L), StrandedStacks(request, Warehouse));
    }

    [Fact]
    public void RoomForbiddenAndUnsafeStacksAreNotStranded()
    {
        var request = Request(
            Row("Thing_1", 75, "Zone_yard"),
            Row("Thing_2", 0, "Zone_yard") with { Forbidden = true },
            Row("Thing_3", 0, "Zone_yard") with { SafeToHaul = false });
        Assert.Equal(Known(0L), StrandedStacks(request, Yard));
    }

    [Fact]
    public void UnknownHeadroomReadsUnknownNotZero()
    {
        var request = Request(Row("Thing_1", 0, "Zone_yard"), Row("Thing_2", 0) with { StorageHeadroom = Unknown<long>() });
        Assert.False(StrandedStacks(request, Yard).IsKnown);
        Assert.False(StrandedStacks(request, Warehouse).IsKnown);
    }

    [Fact]
    public void UnknownSafetyReadsUnknownAtTheAcceptingStoreOnly()
    {
        var request = Request(Row("Thing_1", 0, "Zone_yard") with { SafeToHaul = false, SafetyKnown = false });
        Assert.False(StrandedStacks(request, Yard).IsKnown);
        Assert.Equal(Known(0L), StrandedStacks(request, Warehouse));
    }

    [Fact]
    public void AnUnreadCensusReadsUnknown()
    {
        var request = Request() with { Loot = Unknown<EquatableArray<LootItem>>() };
        Assert.False(StrandedStacks(request, Yard).IsKnown);
    }
}
