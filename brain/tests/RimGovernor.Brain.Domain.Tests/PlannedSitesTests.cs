using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using O = RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class PlannedSitesTests
{
    private static readonly Resource Wood = new("WoodLog");
    private static readonly Resource Steel = new("Steel");
    private static readonly HashSet<(string, Cell)> NonePlaced = [];

    private static PlannedRoom Room(int id, int x) =>
        new() { Id = id, Role = PlannedShelter, Region = RectRegion(new Rectangle { X = x, Z = 0, Width = 3, Height = 3 }) };

    /// <summary>A def made from WoodLog (5) or Steel (2); anything else is unavailable.</summary>
    private static (string, IReadOnlyList<Amount>)? Ring(string def, string stuff) => stuff switch
    {
        "WoodLog" => (stuff, [new Amount(Wood, 5)]),
        "Steel" => (stuff, [new Amount(Steel, 2)]),
        _ => null,
    };

    private static (string, IReadOnlyList<Amount>)? Piece(string def) => ("WoodLog", [new Amount(Wood, 20)]);

    private static IReadOnlyList<Amount>? Floor(string def) => [new Amount(Wood, 1)];

    private static IReadOnlyList<PlannedBuildSite> Sites(IEnumerable<(PlannedRoom, IReadOnlyList<Operation>)> rooms, IReadOnlySet<(string, Cell)>? placed = null) =>
        PlannedSitesOf(rooms, _ => ["WoodLog", "Steel"], Ring, Piece, Floor, "Vent", placed ?? NonePlaced);

    private static readonly WantedPiece Bench = new()
    {
        DefName = "TableButcher", Minimum = new Cell(1, 1), Maximum = new Cell(1, 1), Slot = "bench", Size = new Cell(1, 1), Rot = Rotation.East,
    };

    private static (PlannedRoom, IReadOnlyList<Operation>) Owed() => (Room(1, 0),
    [
        new Operation { Kind = OpKind.WallIn, Cells = [new(3, 0), new(-1, 0), new(-1, 1)] },
        new Operation { Kind = OpKind.DoorIn, Cells = [new(1, -1)] },
        new Operation { Kind = OpKind.FloorIn, Cells = [new(0, 0), new(1, 0)],
            Floors = [new ClearanceFloor { Cell = new(0, 0), DefName = "WoodPlankFloor" }, new ClearanceFloor { Cell = new(1, 0), DefName = "WoodPlankFloor" }] },
        new Operation { Kind = OpKind.Build, Cells = [new(1, 1)], Pieces = [Bench] },
        new Operation { Kind = OpKind.RoofIn, Cells = [new(0, 0), new(1, 0), new(2, 0)] },
    ]);

    [Fact]
    public void EachOwedOpIsOneSitePerStuffAndFloorsPiecesAndTheRoofKeepTheirOwn()
    {
        var sites = Sites([Owed()]);
        Assert.Equal(
            ["planned/room1/wall/WoodLog", "planned/room1/wall/Steel", "planned/room1/door/WoodLog", "planned/room1/door/Steel",
                "planned/room1/floor/WoodPlankFloor", "planned/room1/build/TableButcher/1/1", "planned/room1/roof"],
            sites.Select(s => s.Id));
        var wall = sites.Single(s => s.Id == "planned/room1/wall/Steel");
        Assert.Equal((OpKind.WallIn, ShellWallDefinition, "Steel"), (wall.Op, wall.Definition, wall.Stuff));
        Assert.Equal([new Cell(-1, 0), new Cell(-1, 1), new Cell(3, 0)], wall.Positions);
        Assert.Equal([new Amount(Steel, 6)], wall.Material);
        Assert.Equal([new Amount(Wood, 2)], sites.Single(s => s.Op == OpKind.FloorIn).Material);
        var bench = sites.Single(s => s.Op == OpKind.Build);
        Assert.Equal((Rotation.East, "WoodLog", 1), (bench.Rotation, bench.Stuff, bench.Positions.Count));
        var roof = sites.Single(s => s.Op == OpKind.RoofIn);
        Assert.Equal(3, roof.Positions.Count);
        Assert.Empty(roof.Material);
        Assert.All(sites, s => Assert.False(s.CanPlace.IsKnown));

        // The roof has no native placement: everything else is asked, a few sites to a read.
        var (batches, findings) = PlannedSiteBatches(sites);
        Assert.Empty(findings);
        Assert.Equal([4, 2], batches.Select(b => b.Count));
        Assert.DoesNotContain(batches.SelectMany(b => b), a => a.Id == "planned/room1/roof");
        Assert.Equal("WoodPlankFloor", batches.SelectMany(b => b).Single(a => a.Id == "planned/room1/floor/WoodPlankFloor").Definition);
    }

    [Fact]
    public void TheJoinAttachesRowsVerdictAndBlockers()
    {
        var sites = Sites([Owed()]);
        var (batches, findings) = PlannedSiteBatches(sites);
        var row = new O.ConstructionActivityOption("planned/room1/wall/Steel", ShellWallDefinition, new Cell(-1, 0), "Pawn_1", "Steel", "Thing_Steel1",
            new Cell(5, 5), 6, 75, "ConstructFinishFrames", Known(10d), Known(20d), Known(300d));
        var blocker = new O.PlannedSiteBlocker(new Cell(3, 0), "Plant_1", "Plant_TreeOak", "Plant");
        var clearing = new O.PlannedSiteClearing("planned/room1/wall/Steel/clear/cut", O.PlannedClearingKind.Cut, [new Cell(3, 0)]);
        var answered = new O.ConstructionActivityRead
        {
            Known = true,
            Findings = ["planned_quality_minimum_unset"],
            Planned =
            [
                new O.PlannedSiteAnswer("planned/room1/wall/Steel", Known(false))
                {
                    Blockers = [blocker], Rejections = [new O.PlannedSiteRejection(new Cell(3, 0), "blocked")], Options = [row],
                    Verdict = O.PlannedSiteVerdict.Feasible, Clearing = [clearing],
                },
            ],
        };
        var facts = JoinPlannedSites(sites, findings, [(batches[0], answered), (batches[1], null)]);
        var wall = facts.Sites.Single(s => s.Id == "planned/room1/wall/Steel");
        Assert.Equal(Known(false), wall.CanPlace);
        Assert.Equal([row], wall.Options);
        Assert.Equal([blocker], wall.Blockers);
        // Feasibility once cleared, and the clearing it owes, come through the join (#3276).
        Assert.Equal(O.PlannedSiteVerdict.Feasible, wall.Verdict);
        Assert.Equal([clearing], wall.Clearing);
        Assert.Single(wall.Rejections);
        // A site in the same read that no answer names, and every site of a read that never came, stay unknown.
        Assert.False(facts.Sites.Single(s => s.Id == "planned/room1/wall/WoodLog").CanPlace.IsKnown);
        Assert.False(facts.Sites.Single(s => s.Op == OpKind.Build).CanPlace.IsKnown);
        Assert.Equal(["planned_quality_minimum_unset", "planned_site_read_unknown"], facts.Findings);
    }

    [Fact]
    public void ATruncatedReadAnswersNoneOfItsSites()
    {
        var sites = Sites([Owed()]);
        var (batches, findings) = PlannedSiteBatches(sites);
        var truncated = new O.ConstructionActivityRead
        {
            Known = true, Truncated = true,
            Planned = [new O.PlannedSiteAnswer("planned/room1/wall/Steel", Known(true))],
        };
        var facts = JoinPlannedSites(sites, findings, [(batches[0], truncated)]);
        Assert.False(facts.Sites.Single(s => s.Id == "planned/room1/wall/Steel").CanPlace.IsKnown);
        Assert.Contains("planned_site_read_truncated", facts.Findings);
    }

    [Fact]
    public void TheRoofAndVentsAreOwedAsIfTheRingStood()
    {
        // Bare ground: Reconcile owes the ring but no roof yet, since nothing encloses the room.
        var room = Room(1, 0);
        var plan = new LayoutPlan { Rooms = [room], Roofed = room.Region };
        var input = new ReconcileInput { Plan = plan, Room = room, Ground = new GroundCensus(), Rows = [], Unroofed = room.Region.Cells().ToHashSet() };
        var now = Reconcile(input).Owed;
        Assert.DoesNotContain(now, o => o.Kind == OpKind.RoofIn);
        var stood = RingStandsGround(input.Ground, now);
        Assert.Equal(now.Where(o => o.Kind is OpKind.WallIn).SelectMany(o => o.Cells).ToHashSet(), stood.Walls.ToHashSet());
        Assert.Empty(input.Ground.Walls);
        var stands = Reconcile(input with { Ground = stood }).Owed;
        var sited = PlannedSitedOps(now, stands);
        Assert.Equal(9, sited.Single(o => o.Kind == OpKind.RoofIn).Cells.Count);
        Assert.Equal(now.Where(o => o.Kind == OpKind.WallIn), sited.Where(o => o.Kind == OpKind.WallIn));
        Assert.Contains(Sites([(room, sited)]), s => s.Id == "planned/room1/roof" && s.Positions.Count == 9);
    }

    [Fact]
    public void ASharedWallBelongsToTheLowerRoomOnly()
    {
        var plan = new LayoutPlan { Rooms = [Room(2, 4), Room(1, 0)] };
        var owed = plan.Rooms.Select(room => (room, Reconcile(new ReconcileInput { Plan = plan, Room = room, Ground = new GroundCensus(), Rows = [] }).Owed));
        var sites = PlannedSitesOf(owed, _ => ["WoodLog"], Ring, Piece, Floor, "Vent", NonePlaced);
        var ring = sites.Where(s => s.Op is OpKind.WallIn or OpKind.DoorIn).ToList();
        // Two 3x3 rooms with a wall between: 16 + 16 - 5 shared ring cells, as PlannedOwedCost counts them.
        Assert.Equal(27, ring.Sum(s => s.Positions.Count));
        Assert.Equal(16, ring.Where(s => s.Room == 1).Sum(s => s.Positions.Count));
        Assert.Empty(ring.Where(s => s.Room == 2).SelectMany(s => s.Positions).Intersect(ring.Where(s => s.Room == 1).SelectMany(s => s.Positions)));
    }

    [Fact]
    public void PlacedBlueprintsAndFramesAreLeftOut()
    {
        var placed = new HashSet<(string, Cell)> { (ShellWallDefinition, new Cell(3, 0)), ("TableButcher", new Cell(1, 1)), (ShellDoorDefinition, new Cell(1, -1)) };
        var sites = Sites([Owed()], placed);
        Assert.Equal([new Cell(-1, 0), new Cell(-1, 1)], sites.Single(s => s.Id == "planned/room1/wall/WoodLog").Positions);
        Assert.DoesNotContain(sites, s => s.Op is OpKind.Build or OpKind.DoorIn);
    }

    [Fact]
    public void AnOverlongSiteIsNotAskedAndSaysWhy()
    {
        var cells = Enumerable.Range(0, PlannedSitePositionCap + 1).Select(x => new Cell(x, -1)).ToArray();
        var sites = Sites([(Room(1, 0), [new Operation { Kind = OpKind.WallIn, Cells = cells }])]);
        var (batches, findings) = PlannedSiteBatches(sites);
        Assert.Empty(batches);
        Assert.Equal(["planned_site_positions_exceeded", "planned_site_positions_exceeded"], findings);
    }

    [Fact]
    public void RingStuffsAreTheCheapestStockedFirstElseTheCheapest()
    {
        Assert.Equal(["Steel", "WoodLog"], PlannedRingStuffs(
        [
            new PlannedRingStuff("Plasteel", 1, false), new PlannedRingStuff("WoodLog", 5, true),
            new PlannedRingStuff("Steel", 3, true), new PlannedRingStuff("BlocksGranite", 9, true),
        ]));
        Assert.Equal(["Plasteel"], PlannedRingStuffs([new PlannedRingStuff("Steel", 3, false), new PlannedRingStuff("Plasteel", 1, false)]));
        Assert.Equal([""], PlannedRingStuffs([]));
    }

    [Fact]
    public void AShelfCandidateIsAOnePiecePlannedSite()
    {
        var candidate = new ShelfCandidate
        {
            Zone = "Zone_3", Role = "general",
            Piece = new InteriorPiece { Slot = "shelf", Def = ShelfDefinition, Size = new Cell(2, 1), Rect = new Rectangle { X = 4, Z = 2, Width = 2, Height = 1 } },
        };
        var site = Assert.Single(ShelfPlannedSites([candidate, candidate], ("Steel", [new Amount(Steel, 10)])));
        Assert.Equal((0, "Zone_3", OpKind.Build, ShelfDefinition, "Steel"), (site.Room, site.Zone, site.Op, site.Definition, site.Stuff));
        Assert.Equal([candidate.Piece.Wanted().Anchor()], site.Positions);
        Assert.Equal([new Amount(Steel, 10)], site.Material);
        Assert.Empty(ShelfPlannedSites([candidate], null));
    }
}
