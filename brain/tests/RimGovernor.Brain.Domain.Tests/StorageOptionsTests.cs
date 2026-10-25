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

    private static SiteCell Floor(Cell c, bool zoned, bool sheltered) => SiteCellFixtures.At(c) with
    {
        Zone = zoned,
        Roofed = sheltered,
        Indoors = sheltered,
    };

    private static LootItem Stranded(string thing, Resource def, Cell at) => new()
    {
        Supply = new StartingSupply { Thing = thing, Definition = def.Value, Cell = at },
        Haul = new RimGovernor.Contracts.Wire.Observations.HaulSafety.Safe { PathLength = 0 },
        Deterioration = def == Steel ? null : new RimGovernor.Contracts.Wire.Observations.LootDeterioration { BaseRatePerDay = def == Cloth ? 2 : 1, Environmental = true, Outdoors = true },
        StorageHeadroom = 0L,
        AcceptedBy = EquatableArray.From(new List<string> { "Zone_w" }),
    };

    private static ItemFacts Items()
    {
        return new ItemFacts
        {
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
                new StockpileZone { Id = "Zone_w", Role = StockpileRolesConstants.GeneralRole, Cells = zone, Filter = StockpileFilter.GeneralFilter(), Capacity = 6, Used = 6 },
                new StockpileZone { Id = "Zone_y", Role = StockpileRolesConstants.YardRole, Cells = PlannedRules.RectCells(Yard), Filter = StockpileFilter.YardFilter(), Capacity = 12, Used = 3 },
            ],
            Stores =
            [
                new Policy.Store { Site = new StoreSite { Role = StockpileRolesConstants.GeneralRole, Interior = Warehouse, Filter = StockpileFilter.GeneralFilter() } },
                new Policy.Store { Site = new StoreSite { Role = StockpileRolesConstants.YardRole, Interior = Yard, Filter = StockpileFilter.YardFilter() } },
            ],
            ShelfStacks = 6,
            ShelfCost = new StorageBuildCost { Stuff = Wood.Value, Costs = [new Amount(Wood, 25)], WorkTicks = 1200.0 },
            Layout = new StorageLayout.Planned([],
            [
                new PlannedStoreRoom { Room = 3, Role = PlannedRole.Storage, Cells = PlannedRules.RectCells(Warehouse), Material = new Dictionary<Resource, long>() },
                new PlannedStoreRoom { Room = 7, Work = 2400, Role = PlannedRole.Storage, Cells = PlannedRules.RectCells(NextStorage), Material = new Dictionary<Resource, long> { [Steel] = 40 } },
                new PlannedStoreRoom { Room = 4, Role = PlannedRole.Yard, Cells = PlannedRules.RectCells(Yard), Outdoor = true, Material = new Dictionary<Resource, long>() },
                new PlannedStoreRoom { Room = 8, Role = PlannedRole.Yard, Cells = PlannedRules.RectCells(NextYard), Outdoor = true, Material = new Dictionary<Resource, long>() },
            ]),
            Loot = EquatableArray.From(new List<LootItem>
            {
                Stranded("Thing_cloth", Cloth, new Cell(20, 0)),
                Stranded("Thing_steel", Steel, new Cell(21, 0)),
                Stranded("Thing_chunk", Granite, new Cell(22, 0)),
            }),
            Occupancy = new RimGovernor.Contracts.Wire.Observations.StorageOccupancySnapshot { CurrentHour = 0, Groups = [] },
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
        Assert.Equal(25, shelf.Material[Wood]);
        Assert.Equal(1200.0, shelf.WorkTicks);
        Assert.Equal(5.0 + (1200.0 / GameTime.TicksPerDay), shelf.LeadDays);
        Assert.Equal(2, shelf.CellsLost);
        Assert.Equal(0, shelf.CellsTaken);
        Assert.Equal(4, shelf.Stacks);
        // Cloth 2 HP/day in open air comes off; steel does not deteriorate; the chunk cannot go on a shelf.
        Assert.Equal(2.0, shelf.DeteriorationAvoided);
        Assert.Empty(shelf.Findings);
    }

    [Fact]
    public void ZoneGrowthTakesTheOpenFootprintAtNoCostWithTheHorizonForLeadTime()
    {
        var warehouse = Store(Report(Request()), StockpileRolesConstants.GeneralRole);
        var growth = Assert.Single(warehouse.Options, o => o.Kind == StorageOptionKind.ZoneGrowth);

        Assert.Equal("Zone_w", growth.Zone);
        Assert.Empty(growth.Material);
        Assert.Equal(ColonyDiagnosticsRules.MaintenanceHorizonDays, growth.LeadDays);
        Assert.Equal(6, growth.CellsTaken);
        Assert.Equal(6, growth.Stacks);
        // Every stranded stack moves under the roof: cloth 2 plus the chunk 1.
        Assert.Equal(3.0, growth.DeteriorationAvoided);
        Assert.Equal(6, warehouse.Capacity.Capacity);
        Assert.Equal(3L, warehouse.Capacity.Stranded);
    }

    [Fact]
    public void ASuccessorRoomIsPricedFromItsRequiredMaterialAndWork()
    {
        var warehouse = Store(Report(Request()), StockpileRolesConstants.GeneralRole);
        var room = Assert.Single(warehouse.Options, o => o.Kind == StorageOptionKind.SuccessorRoom);

        Assert.Equal(7, room.Room);
        Assert.Equal(40, room.Material[Steel]);
        Assert.Equal(2400.0, room.WorkTicks);
        Assert.Equal(StoreRules.StorageLeadDays(2400), room.LeadDays);
        Assert.Empty(room.Findings);
        Assert.Equal(9, room.CellsTaken);
        Assert.Equal(9, room.Stacks);
    }

    [Fact]
    public void ASuccessorRoomWithKnownSiteWorkHasALeadTimeAndNamesItsRoofApart()
    {
        var request = Request();
        var rooms = ((StorageLayout.Planned)request.Layout).Rooms.Select(r => r.Room == 7 ? r with { Work = 3000.0, RoofOwed = true } : r).ToList();
        var room = Assert.Single(Store(Report(request with { Layout = new StorageLayout.Planned([], rooms) }), StockpileRolesConstants.GeneralRole).Options,
            o => o.Kind == StorageOptionKind.SuccessorRoom);

        Assert.Equal(3000.0, room.WorkTicks);
        Assert.Equal(StoreRules.StorageLeadDays(3000), room.LeadDays);
        Assert.Contains(StorageRules.FindingRoomRoofWorkUnassessed, room.Findings);
    }

    [Fact]
    public void AFurtherYardBuildsNothingAndNoRoomOptionExistsBeforeLayoutSizesIt()
    {
        var report = Report(Request());
        var yard = Assert.Single(Store(report, StockpileRolesConstants.YardRole).Options, o => o.Kind == StorageOptionKind.FurtherRoom);

        Assert.Equal(8, yard.Room);
        Assert.Empty(yard.Material);
        Assert.Equal(ColonyDiagnosticsRules.MaintenanceHorizonDays, yard.LeadDays);
        Assert.Equal(6, yard.Stacks);
        Assert.Equal(0.0, yard.DeteriorationAvoided);

        Assert.DoesNotContain(Store(Report(Request() with { Layout = new StorageLayout.Planned([], []) }), StockpileRolesConstants.GeneralRole).Options, o => o.Kind == StorageOptionKind.SuccessorRoom);
    }

    [Fact]
    public void ActualStackRatePricesStuffWithoutACatalogRate()
    {
        var request = Request();
        var rows = request.Loot.Select(r => r.Supply.Definition == Cloth.Value ? r with { Deterioration = r.Deterioration! with { BaseRatePerDay = 7 } } : r).ToArray();
        var warehouse = Store(Report(request with { Loot = EquatableArray.From(rows) }), StockpileRolesConstants.GeneralRole);
        Assert.Equal(7.0, warehouse.Options.First(o => o.Kind == StorageOptionKind.Shelf).DeteriorationAvoided);
    }
    [Fact]
    public void AStrandedStackOutsidePlanningCellsUsesItsNativeExposure()
    {
        var request = Request();
        var remote = Stranded("Thing_remote", Cloth, new Cell(90, 90));
        var warehouse = Store(Report(request with { Loot = [remote] }), StockpileRolesConstants.GeneralRole);
        Assert.Equal(2.0, warehouse.Options.First(o => o.Kind == StorageOptionKind.Shelf).DeteriorationAvoided);
    }
    [Fact]
    public void InitialReviewCanReadCapacityWhileWaitingForItsFirstPlan()
    {
        var pending = new StorageLayout.AwaitingFirstPlan();
        Assert.IsType<StorageLayout.AwaitingFirstPlan>(RimGovernor.Brain.BuildingRuntime.RoundsStockpilePlanner.StorageLayout(ColonyFactsCoreTests.Projection(), new FlooringPolicy()));
        var request = Request() with { Layout = pending, ShelfAvailable = true };
        var report = StockpileRules.PlanStockpileMaintenance(request);
        Assert.Empty(report.ShelfCandidates);
        Assert.NotEqual(ShelfStepKind.Build, report.Shelf.Kind);
        Assert.DoesNotContain(report.Options.SelectMany(s => s.Options), o => o.Kind is StorageOptionKind.Shelf or StorageOptionKind.SuccessorRoom or StorageOptionKind.FurtherRoom);
        Assert.Equal(6, Store(report.Options.ToList(), StockpileRolesConstants.GeneralRole).Capacity.Capacity);
        var planned = StockpileRules.PlanStockpileMaintenance(request with { Layout = Request().Layout });
        Assert.NotEmpty(planned.ShelfCandidates);
        Assert.Contains(planned.Options.SelectMany(s => s.Options), o => o.Kind == StorageOptionKind.SuccessorRoom);
    }
    [Fact]
    public void OnlyTheWorstRowsUpToTheStacksGainedCount()
    {
        // One stack gained takes the worst stranded row only: cloth at 2 HP/day.
        var avoided = StorageRules.DeteriorationAvoided(Items(), StoreRules.StrandedRows(Request(), Request().Stores[0]), 1, [0.0], false);
        Assert.Equal(2.0, avoided);
    }
}
