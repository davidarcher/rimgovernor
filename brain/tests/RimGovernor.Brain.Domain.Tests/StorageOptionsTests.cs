using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3238: the storage option report prices each option kind from synthetic catalog rows and reports its effect apart
/// (cells, stacks, deterioration avoided); unknown work leaves the lead time unknown with a finding, and
/// deterioration is avoided only for the stranded stacks that deteriorate.
/// </summary>
public sealed class StorageOptionsTests
{
    private static readonly Rectangle Warehouse = new() { X = 0, Z = 0, Width = 4, Height = 3 };
    private static readonly Rectangle Yard = new() { X = 30, Z = 0, Width = 4, Height = 3 };
    private static readonly Rectangle NextStorage = new() { X = 10, Z = 0, Width = 3, Height = 3 };
    private static readonly Rectangle NextYard = new() { X = 40, Z = 0, Width = 3, Height = 2 };

    private static readonly Resource Cloth = new("Cloth");
    private static readonly Resource Steel = new("Steel");
    private static readonly Resource Granite = new("ChunkGranite");
    private static readonly Resource Wood = new("WoodLog");

    private static SiteCell Floor(Cell c, bool zoned, bool sheltered) => new()
    {
        Cell = c,
        Walkable = Known(true),
        Zone = Known(zoned),
        Roofed = Known(sheltered),
        Indoors = Known(sheltered),
        Doorway = Known(false),
        StorageCapacity = Known(1),
        StorageStacks = Known(0),
    };

    private static LootItem Stranded(string thing, Resource def, Cell at) => new()
    {
        Supply = new StartingSupply { Thing = thing, Definition = def.Value, Cell = at },
        SafeToHaul = true,
        SafetyKnown = true,
        StorageHeadroom = Known(0L),
        AcceptedBy = EquatableArray.From(new List<string> { "Zone_w" }),
    };

    private static ItemFacts Items(bool clothRate = true)
    {
        var rates = new Dictionary<Resource, double> { [Steel] = 0, [Granite] = 1 };
        if (clothRate)
        {
            rates[Cloth] = 2;
        }
        return new ItemFacts
        {
            DeteriorationRate = rates,
            Categories = new Dictionary<Resource, IReadOnlyList<string>> { [Cloth] = ["Textiles"], [Steel] = ["ResourcesRaw"], [Granite] = ["Chunks", "StoneChunks"] },
        };
    }

    // The warehouse zone covers its two west columns; its east columns are open, roofed floor. The yard zone
    // covers the whole yard. Three stranded stacks the warehouse accepts lie in open air.
    private static StockpileRequest Request()
    {
        var cells = new List<SiteCell>();
        var zone = RectCells(Warehouse).Where(c => c.X <= 1).ToList();
        cells.AddRange(RectCells(Warehouse).Select(c => Floor(c, zone.Contains(c), true)));
        cells.AddRange(RectCells(NextStorage).Select(c => Floor(c, false, true)));
        cells.AddRange(RectCells(Yard).Select(c => Floor(c, true, false)));
        cells.AddRange(RectCells(NextYard).Select(c => Floor(c, false, false)));
        cells.AddRange(Enumerable.Range(20, 3).Select(x => Floor(new Cell(x, 0), false, false)));
        return new StockpileRequest
        {
            Bounds = new Bounds(100, 100),
            Cells = cells,
            Zones =
            [
                new StockpileZone { Id = "Zone_w", Role = DomainConstants.GeneralRole, Cells = zone, Filter = StockpileFilter.GeneralFilter(), Capacity = Known(6), Used = Known(6) },
                new StockpileZone { Id = "Zone_y", Role = DomainConstants.YardRole, Cells = RectCells(Yard), Filter = StockpileFilter.YardFilter(), Capacity = Known(12), Used = Known(3) },
            ],
            Stores =
            [
                new Policy.Store { Site = new StoreSite { Role = DomainConstants.GeneralRole, Interior = Warehouse, Filter = StockpileFilter.GeneralFilter() } },
                new Policy.Store { Site = new StoreSite { Role = DomainConstants.YardRole, Interior = Yard, Filter = StockpileFilter.YardFilter() } },
            ],
            Walkways = Known(EquatableArray.From(new List<Cell>())),
            ShelfStacks = Known(6),
            ShelfCost = Known(new StorageBuildCost { Stuff = Wood.Value, Costs = [new Amount(Wood, 25)], WorkTicks = Known(1200.0) }),
            StoreRooms = Known<IReadOnlyList<PlannedStoreRoom>>(
            [
                new PlannedStoreRoom { Room = 3, Role = PlannedRole.Storage, Cells = RectCells(Warehouse), Material = Known<IReadOnlyDictionary<Resource, long>>(new Dictionary<Resource, long>()) },
                new PlannedStoreRoom { Room = 7, Role = PlannedRole.Storage, Cells = RectCells(NextStorage), Material = Known<IReadOnlyDictionary<Resource, long>>(new Dictionary<Resource, long> { [Steel] = 40 }) },
                new PlannedStoreRoom { Room = 4, Role = PlannedRole.Yard, Cells = RectCells(Yard), Outdoor = true, Material = Known<IReadOnlyDictionary<Resource, long>>(new Dictionary<Resource, long>()) },
                new PlannedStoreRoom { Room = 8, Role = PlannedRole.Yard, Cells = RectCells(NextYard), Outdoor = true, Material = Known<IReadOnlyDictionary<Resource, long>>(new Dictionary<Resource, long>()) },
            ]),
            Loot = Known(EquatableArray.From(new List<LootItem>
            {
                Stranded("Thing_cloth", Cloth, new Cell(20, 0)),
                Stranded("Thing_steel", Steel, new Cell(21, 0)),
                Stranded("Thing_chunk", Granite, new Cell(22, 0)),
            })),
            Items = Items(),
        };
    }

    private static List<StoreOptions> Report(StockpileRequest r) => StorageOptions(r, ShelfCandidates(r));

    private static StoreOptions Store(List<StoreOptions> report, string role) => Assert.Single(report, s => s.Role == role);

    [Fact]
    public void AShelfIsPricedFromItsStuffRowAndSavesOnlyTheDeterioratingStacksItCanHold()
    {
        var warehouse = Store(Report(Request()), DomainConstants.GeneralRole);
        var shelf = warehouse.Options.First(o => o.Kind == StorageOptionKind.Shelf);

        Assert.Equal("Zone_w", shelf.Zone);
        Assert.Equal(Wood.Value, shelf.Stuff);
        Assert.Equal(25, shelf.Material.ValueOrDefault![Wood]);
        Assert.Equal(Known(1200.0), shelf.WorkTicks);
        Assert.Equal(Known(5.0 + (1200.0 / GameTime.TicksPerDay)), shelf.LeadDays);
        Assert.Equal(Known(2), shelf.CellsLost);
        Assert.Equal(Known(0), shelf.CellsTaken);
        Assert.Equal(Known(4), shelf.Stacks);
        // Cloth 2 HP/day in open air comes off; steel does not deteriorate; the chunk cannot go on a shelf.
        Assert.Equal(Known(2.0), shelf.DeteriorationAvoided);
        Assert.Empty(shelf.Findings);
    }

    [Fact]
    public void ZoneGrowthTakesTheOpenFootprintAtNoCostWithTheHorizonForLeadTime()
    {
        var warehouse = Store(Report(Request()), DomainConstants.GeneralRole);
        var growth = Assert.Single(warehouse.Options, o => o.Kind == StorageOptionKind.ZoneGrowth);

        Assert.Equal("Zone_w", growth.Zone);
        Assert.Empty(growth.Material.ValueOrDefault!);
        Assert.Equal(Known(MaintenanceHorizonDays), growth.LeadDays);
        Assert.Equal(Known(6), growth.CellsTaken);
        Assert.Equal(Known(6), growth.Stacks);
        // Every stranded stack moves under the roof: cloth 2 plus the chunk 1.
        Assert.Equal(Known(3.0), growth.DeteriorationAvoided);
        Assert.Equal(Known(6), warehouse.Capacity.Capacity);
        Assert.Equal(Known(3L), warehouse.Capacity.Stranded);
    }

    [Fact]
    public void ASuccessorRoomIsPricedFromThePlanAndItsUnknownWorkLeavesTheLeadTimeUnknown()
    {
        var warehouse = Store(Report(Request()), DomainConstants.GeneralRole);
        var room = Assert.Single(warehouse.Options, o => o.Kind == StorageOptionKind.SuccessorRoom);

        Assert.Equal(7, room.Room);
        Assert.Equal(40, room.Material.ValueOrDefault![Steel]);
        Assert.False(room.WorkTicks.IsKnown);
        Assert.False(room.LeadDays.IsKnown);
        Assert.Contains(FindingRoomWorkUnknown, room.Findings);
        Assert.Equal(Known(9), room.CellsTaken);
        Assert.Equal(Known(9), room.Stacks);
    }

    [Fact]
    public void AFurtherYardBuildsNothingAndALeftUnsizedRoomIsUnknown()
    {
        var report = Report(Request());
        var yard = Assert.Single(Store(report, DomainConstants.YardRole).Options, o => o.Kind == StorageOptionKind.FurtherRoom);

        Assert.Equal(8, yard.Room);
        Assert.Empty(yard.Material.ValueOrDefault!);
        Assert.Equal(Known(MaintenanceHorizonDays), yard.LeadDays);
        Assert.Equal(Known(6), yard.Stacks);
        Assert.Equal(Known(0.0), yard.DeteriorationAvoided);

        var unsized = Assert.Single(Store(Report(Request() with { StoreRooms = Known<IReadOnlyList<PlannedStoreRoom>>([]) }), DomainConstants.GeneralRole).Options, o => o.Kind == StorageOptionKind.SuccessorRoom);
        Assert.Equal([FindingRoomUnsized], unsized.Findings);
        Assert.False(unsized.Material.IsKnown);
        Assert.False(unsized.Stacks.IsKnown);
        Assert.False(unsized.LeadDays.IsKnown);
    }

    [Fact]
    public void UnknownShelfWorkLeavesTheLeadTimeUnknownWithAFinding()
    {
        var request = Request() with { ShelfCost = Known(new StorageBuildCost { Stuff = Wood.Value, Costs = [new Amount(Wood, 25)] }) };
        var shelf = Store(Report(request), DomainConstants.GeneralRole).Options.First(o => o.Kind == StorageOptionKind.Shelf);

        Assert.True(shelf.Material.IsKnown);
        Assert.False(shelf.LeadDays.IsKnown);
        Assert.Equal([FindingWorkUnknown], shelf.Findings);

        var priced = Store(Report(Request() with { ShelfCost = Unknown<StorageBuildCost>() }), DomainConstants.GeneralRole).Options.First(o => o.Kind == StorageOptionKind.Shelf);
        Assert.False(priced.Material.IsKnown);
        Assert.Contains(FindingShelfCostUnknown, priced.Findings);
    }

    [Fact]
    public void DeteriorationIsUnknownNeverZeroWhenARateOrTheCensusIsUnknown()
    {
        var noRate = Store(Report(Request() with { Items = Items(clothRate: false) }), DomainConstants.GeneralRole);
        Assert.All(noRate.Options, o => Assert.False(o.DeteriorationAvoided.IsKnown));
        Assert.Contains(FindingDeteriorationUnknown, noRate.Options.First(o => o.Kind == StorageOptionKind.Shelf).Findings);

        var noCensus = Store(Report(Request() with { Loot = Unknown<EquatableArray<LootItem>>() }), DomainConstants.GeneralRole);
        Assert.All(noCensus.Options, o => Assert.False(o.DeteriorationAvoided.IsKnown));
    }

    [Fact]
    public void OnlyTheWorstRowsUpToTheStacksGainedCount()
    {
        // One stack gained takes the worst stranded row only: cloth at 2 HP/day.
        var avoided = DeteriorationAvoided(Items(), StrandedRows(Request(), Request().Stores[0]), Known(1), [Known(0.0)], false, Request().Cells.ToDictionary(c => c.Cell));
        Assert.Equal(Known(2.0), avoided);
    }
}
