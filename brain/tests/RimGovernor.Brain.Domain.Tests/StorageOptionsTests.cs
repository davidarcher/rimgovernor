using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

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
        Haul = new RimGovernor.Contracts.Wire.Observations.HaulSafety.Safe { PathLength = 0 },
        StorageHeadroom = 0L,
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
        var zone = PlannedRules.RectCells(Warehouse).Where(c => c.X <= 1).ToList();
        cells.AddRange(PlannedRules.RectCells(Warehouse).Select(c => Floor(c, zone.Contains(c), true)));
        cells.AddRange(PlannedRules.RectCells(NextStorage).Select(c => Floor(c, false, true)));
        cells.AddRange(PlannedRules.RectCells(Yard).Select(c => Floor(c, true, false)));
        cells.AddRange(PlannedRules.RectCells(NextYard).Select(c => Floor(c, false, false)));
        cells.AddRange(Enumerable.Range(20, 3).Select(x => Floor(new Cell(x, 0), false, false)));
        return new StockpileRequest
        {
            Bounds = new Bounds(100, 100),
            Cells = cells,
            Zones =
            [
                new StockpileZone { Id = "Zone_w", Role = StockpileRolesConstants.GeneralRole, Cells = zone, Filter = StockpileFilter.GeneralFilter(), Capacity = Known(6), Used = Known(6) },
                new StockpileZone { Id = "Zone_y", Role = StockpileRolesConstants.YardRole, Cells = PlannedRules.RectCells(Yard), Filter = StockpileFilter.YardFilter(), Capacity = Known(12), Used = Known(3) },
            ],
            Stores =
            [
                new Policy.Store { Site = new StoreSite { Role = StockpileRolesConstants.GeneralRole, Interior = Warehouse, Filter = StockpileFilter.GeneralFilter() } },
                new Policy.Store { Site = new StoreSite { Role = StockpileRolesConstants.YardRole, Interior = Yard, Filter = StockpileFilter.YardFilter() } },
            ],
            Walkways = Known(EquatableArray.From(new List<Cell>())),
            ShelfStacks = Known(6),
            ShelfCost = Known(new StorageBuildCost { Stuff = Wood.Value, Costs = [new Amount(Wood, 25)], WorkTicks = Known(1200.0) }),
            StoreRooms = Known<IReadOnlyList<PlannedStoreRoom>>(
            [
                new PlannedStoreRoom { Room = 3, Role = PlannedRole.Storage, Cells = PlannedRules.RectCells(Warehouse), Material = Known<IReadOnlyDictionary<Resource, long>>(new Dictionary<Resource, long>()) },
                new PlannedStoreRoom { Room = 7, Role = PlannedRole.Storage, Cells = PlannedRules.RectCells(NextStorage), Material = Known<IReadOnlyDictionary<Resource, long>>(new Dictionary<Resource, long> { [Steel] = 40 }) },
                new PlannedStoreRoom { Room = 4, Role = PlannedRole.Yard, Cells = PlannedRules.RectCells(Yard), Outdoor = true, Material = Known<IReadOnlyDictionary<Resource, long>>(new Dictionary<Resource, long>()) },
                new PlannedStoreRoom { Room = 8, Role = PlannedRole.Yard, Cells = PlannedRules.RectCells(NextYard), Outdoor = true, Material = Known<IReadOnlyDictionary<Resource, long>>(new Dictionary<Resource, long>()) },
            ]),
            Loot = EquatableArray.From(new List<LootItem>
            {
                Stranded("Thing_cloth", Cloth, new Cell(20, 0)),
                Stranded("Thing_steel", Steel, new Cell(21, 0)),
                Stranded("Thing_chunk", Granite, new Cell(22, 0)),
            }),
            Items = Items(),
        };
    }

    private static List<StoreOptions> Report(StockpileRequest r)
    {
        var open = StockpileRules.NewStockpileOpen(r);
        return StorageRules.StorageOptions(r, StorageRules.ShelfCandidates(r, open.Cells), open);
    }

    private static StoreOptions Store(List<StoreOptions> report, string role) => Assert.Single(report, s => s.Role == role);

    [Fact]
    public void AShelfIsPricedFromItsStuffRowAndSavesOnlyTheDeterioratingStacksItCanHold()
    {
        var warehouse = Store(Report(Request()), StockpileRolesConstants.GeneralRole);
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
        var warehouse = Store(Report(Request()), StockpileRolesConstants.GeneralRole);
        var growth = Assert.Single(warehouse.Options, o => o.Kind == StorageOptionKind.ZoneGrowth);

        Assert.Equal("Zone_w", growth.Zone);
        Assert.Empty(growth.Material.ValueOrDefault!);
        Assert.Equal(Known(ColonyDiagnosticsRules.MaintenanceHorizonDays), growth.LeadDays);
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
        var warehouse = Store(Report(Request()), StockpileRolesConstants.GeneralRole);
        var room = Assert.Single(warehouse.Options, o => o.Kind == StorageOptionKind.SuccessorRoom);

        Assert.Equal(7, room.Room);
        Assert.Equal(40, room.Material.ValueOrDefault![Steel]);
        Assert.False(room.WorkTicks.IsKnown);
        Assert.False(room.LeadDays.IsKnown);
        Assert.Contains(StorageRules.FindingRoomWorkUnknown, room.Findings);
        Assert.Equal(Known(9), room.CellsTaken);
        Assert.Equal(Known(9), room.Stacks);
    }

    [Fact]
    public void ASuccessorRoomWithKnownSiteWorkHasALeadTimeAndNamesItsRoofApart()
    {
        var request = Request();
        var rooms = request.StoreRooms.Value.Select(r => r.Room == 7 ? r with { Work = Known(3000.0), RoofOwed = true } : r).ToList();
        var room = Assert.Single(Store(Report(request with { StoreRooms = Known<IReadOnlyList<PlannedStoreRoom>>(rooms) }), StockpileRolesConstants.GeneralRole).Options,
            o => o.Kind == StorageOptionKind.SuccessorRoom);

        Assert.Equal(Known(3000.0), room.WorkTicks);
        Assert.True(room.LeadDays.IsKnown);
        Assert.DoesNotContain(StorageRules.FindingRoomWorkUnknown, room.Findings);
        Assert.Contains(StorageRules.FindingRoomRoofWorkUnassessed, room.Findings);
    }

    [Fact]
    public void AFurtherYardBuildsNothingAndALeftUnsizedRoomIsUnknown()
    {
        var report = Report(Request());
        var yard = Assert.Single(Store(report, StockpileRolesConstants.YardRole).Options, o => o.Kind == StorageOptionKind.FurtherRoom);

        Assert.Equal(8, yard.Room);
        Assert.Empty(yard.Material.ValueOrDefault!);
        Assert.Equal(Known(ColonyDiagnosticsRules.MaintenanceHorizonDays), yard.LeadDays);
        Assert.Equal(Known(6), yard.Stacks);
        Assert.Equal(Known(0.0), yard.DeteriorationAvoided);

        var unsized = Assert.Single(Store(Report(Request() with { StoreRooms = Known<IReadOnlyList<PlannedStoreRoom>>([]) }), StockpileRolesConstants.GeneralRole).Options, o => o.Kind == StorageOptionKind.SuccessorRoom);
        Assert.Equal([StorageRules.FindingRoomUnsized], unsized.Findings);
        Assert.False(unsized.Material.IsKnown);
        Assert.False(unsized.Stacks.IsKnown);
        Assert.False(unsized.LeadDays.IsKnown);
    }

    [Fact]
    public void UnknownShelfWorkLeavesTheLeadTimeUnknownWithAFinding()
    {
        var request = Request() with { ShelfCost = Known(new StorageBuildCost { Stuff = Wood.Value, Costs = [new Amount(Wood, 25)] }) };
        var shelf = Store(Report(request), StockpileRolesConstants.GeneralRole).Options.First(o => o.Kind == StorageOptionKind.Shelf);

        Assert.True(shelf.Material.IsKnown);
        Assert.False(shelf.LeadDays.IsKnown);
        Assert.Equal([StorageRules.FindingWorkUnknown], shelf.Findings);

        var priced = Store(Report(Request() with { ShelfCost = Unknown<StorageBuildCost>() }), StockpileRolesConstants.GeneralRole).Options.First(o => o.Kind == StorageOptionKind.Shelf);
        Assert.False(priced.Material.IsKnown);
        Assert.Contains(StorageRules.FindingShelfCostUnknown, priced.Findings);
    }

    [Fact]
    public void DeteriorationIsUnknownNeverZeroWhenARateIsUnknown()
    {
        var noRate = Store(Report(Request() with { Items = Items(clothRate: false) }), StockpileRolesConstants.GeneralRole);
        Assert.All(noRate.Options, o => Assert.False(o.DeteriorationAvoided.IsKnown));
        Assert.Contains(StorageRules.FindingDeteriorationUnknown, noRate.Options.First(o => o.Kind == StorageOptionKind.Shelf).Findings);
    }

    [Fact]
    public void OnlyTheWorstRowsUpToTheStacksGainedCount()
    {
        // One stack gained takes the worst stranded row only: cloth at 2 HP/day.
        var avoided = StorageRules.DeteriorationAvoided(Items(), StoreRules.StrandedRows(Request(), Request().Stores[0]), Known(1), [Known(0.0)], false, Request().Cells.ToDictionary(c => c.Cell));
        Assert.Equal(Known(2.0), avoided);
    }
}
