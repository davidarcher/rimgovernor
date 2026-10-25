using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// #3237: every declared store a shelf can hold gets shelf candidates, each worth the shelf's stacks less the floor
/// stacks it covers (6 - 2 = +4); a chunk-only store gets none, and no candidate stands on a planned walkway or
/// beside a doorway.
/// </summary>
public sealed class ShelfCandidatesTests
{
    private static readonly StockpileFilter ChunkFilter = new(FilterBase.Nothing, [FilterSelector.CategoryDef("Chunks")]);

    private static readonly (string Role, StockpileFilter Filter)[] Kinds =
    [
        ("yard", StockpileFilter.YardFilter()),
        ("bench:Stove_1", StockpileFilter.RawFoodFilter()),
        ("meals", StockpileFilter.MealShelfFilter()),
        ("medicine", new StockpileFilter(FilterBase.Nothing, [FilterSelector.CategoryDef("Medicine")])),
        ("general", StockpileFilter.GeneralFilter()),
        ("weapons", new StockpileFilter(FilterBase.Nothing, [FilterSelector.CategoryDef("Weapons")])),
        ("chunks", ChunkFilter),
    ];

    // The store role of a candidate's zone: zone i serves Kinds[i].
    private static string RoleOf(ShelfCandidate c) => Kinds[int.Parse(c.Zone["Zone_".Length..])].Role;

    // A 4x3 zone per store kind, 10 cells apart, on open walkable floor holding one stack per cell.
    private static Rectangle Room(int i) => new() { X = i * 10, Z = 0, Width = 4, Height = 3 };

    private static StockpileRequest Request(EquatableArray<Cell> walkways, params Cell[] doors)
    {
        var zones = new List<StockpileZone>();
        var stores = new List<Policy.Store>();
        var cells = new List<SiteCell>();
        for (var i = 0; i < Kinds.Length; i++)
        {
            var (role, filter) = Kinds[i];
            var room = Room(i);
            zones.Add(new StockpileZone { Id = $"Zone_{i}", Role = role, Cells = PlannedRules.RectCells(room), Filter = filter });
            stores.Add(new Policy.Store { Site = new StoreSite { Role = role, Interior = room, Filter = filter } });
            foreach (var c in PlannedRules.RectCells(room))
            {
                cells.Add(SiteCellFixtures.At(c, doorway: doors.Contains(c)));
            }
        }
        return new StockpileRequest { Zones = zones, Stores = stores, Cells = cells, Layout = new StorageLayout.Planned(walkways) };
    }

    private static readonly EquatableArray<Cell> NoWalkways = EquatableArray.From(new List<Cell>());

    private static List<ShelfCandidate> Candidates(StockpileRequest r) => StorageRules.ShelfCandidates(r, StockpileRules.CellIndex(r.Cells));

    [Fact]
    public void EveryStoreAShelfCanHoldGetsCandidatesWorthFourStacks()
    {
        var candidates = Candidates(Request(NoWalkways));

        var roles = candidates.Select(RoleOf).ToHashSet();
        Assert.Equal(["yard", "bench:Stove_1", "meals", "medicine", "general", "weapons"], Kinds.Select(k => k.Role).Where(roles.Contains));
        Assert.DoesNotContain("chunks", roles);
    }

    [Fact]
    public void ShelfHoldsNoChunkOnlyOrPlantOrBuildingOnlyFilter()
    {
        Assert.False(StorageRules.ShelfHolds(ChunkFilter));
        Assert.False(StorageRules.ShelfHolds(new StockpileFilter(FilterBase.Nothing, [FilterSelector.CategoryDef("Plants"), FilterSelector.CategoryDef("Buildings")])));
        Assert.False(StorageRules.ShelfHolds(new StockpileFilter(FilterBase.Nothing, [FilterSelector.SpecialFilter("AllowFresh")])));
        Assert.True(StorageRules.ShelfHolds(StockpileFilter.OpeningStoreFilter()));
        Assert.True(StorageRules.ShelfHolds(new StockpileFilter(FilterBase.Nothing, [FilterSelector.ThingDef("Steel")])));
    }

    [Fact]
    public void NoCandidateOnAPlannedWalkwayOrBesideADoorway()
    {
        // The warehouse (zone 4, x 40..43) has a walkway down its middle row and a doorway at its corner.
        var walk = Enumerable.Range(40, 4).Select(x => new Cell(x, 1)).ToList();
        var door = new Cell(40, 0);
        var candidates = Candidates(Request(EquatableArray.From(walk), door)).Where(c => RoleOf(c) == "general").ToList();

        Assert.NotEmpty(candidates);
        foreach (var c in candidates)
        {
            foreach (var cell in PlannedRules.RectCells(c.Piece.Rect))
            {
                Assert.DoesNotContain(cell, walk);
                Assert.False(Math.Abs(cell.X - door.X) <= 1 && Math.Abs(cell.Z - door.Z) <= 1, $"{c.Piece.Slot} stands beside the doorway");
            }
        }
    }

    [Fact]
    public void MissingStorageCellCannotBecomeZeroCapacity()
    {
        Assert.Throws<RequiredFactException>(() => StockpileRules.StorageStacks([new Cell(1, 1)], new Dictionary<Cell, SiteCell>()));
    }
    [Fact]
    public void ARetiredStoreGetsNoCandidate()
    {
        var request = Request(NoWalkways);
        request = request with { Stores = [.. request.Stores.Select(s => s.Role == "yard" ? s with { Retired = true } : s)] };

        Assert.DoesNotContain(Candidates(request), c => RoleOf(c) == "yard");
    }
}
