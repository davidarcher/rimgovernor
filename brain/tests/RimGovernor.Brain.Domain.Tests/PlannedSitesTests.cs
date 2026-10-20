using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using O = RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class PlannedSitesTests
{
    private static readonly Resource Wood = new("WoodLog");
    private static readonly Resource Steel = new("Steel");
    private static readonly HashSet<(string, Cell)> NonePlaced = [];

    private static PlannedRoom Room(int id, int x) =>
        new() { Id = id, Role = LayoutSurveyRules.PlannedShelter, Region = RegionRules.RectRegion(new Rectangle { X = x, Z = 0, Width = 3, Height = 3 }) };

    /// <summary>A def made from WoodLog (5) or Steel (2); anything else is unavailable.</summary>
    private static (string, IReadOnlyList<Amount>)? Ring(string def, string stuff) => stuff switch
    {
        "WoodLog" => (stuff, [new Amount(Wood, 5)]),
        "Steel" => (stuff, [new Amount(Steel, 2)]),
        _ => null,
    };

    private static (string, IReadOnlyList<Amount>)? Piece(string def) => ("WoodLog", [new Amount(Wood, 20)]);

    private static IReadOnlyList<Amount>? Floor(string def) => [new Amount(Wood, 1)];

    private static IReadOnlyList<PlannedBuildSite> Sites(IEnumerable<(PlannedRoom, IReadOnlyList<Operation>)> rooms, IReadOnlySet<(string, Cell)>? placed = null,
        IReadOnlyDictionary<Cell, string>? standing = null) =>
        PlannedRules.PlannedSitesOf(rooms, _ => ["WoodLog", "Steel"], Ring, Piece, Floor, "Vent", placed ?? NonePlaced, standing ?? new Dictionary<Cell, string>());

    private static readonly WantedPiece Bench = new()
    {
        DefName = "TableButcher", Minimum = new Cell(1, 1), Maximum = new Cell(1, 1), Size = new Cell(1, 1), Rot = Rotation.East,
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
        Assert.Equal((OpKind.WallIn, ContainmentCellRules.ShellWallDefinition, "Steel"), (wall.Op, wall.Definition, wall.Stuff));
        Assert.Equal([new Cell(-1, 0), new Cell(-1, 1), new Cell(3, 0)], wall.Positions);
        Assert.Equal([new Amount(Steel, 6)], wall.Material);
        Assert.Equal([new Amount(Wood, 2)], sites.Single(s => s.Op == OpKind.FloorIn).Material);
        var bench = sites.Single(s => s.Op == OpKind.Build);
        Assert.Equal((Rotation.East, "WoodLog", 1), (bench.Rotation, bench.Stuff, bench.Positions.Count));
        var roof = sites.Single(s => s.Op == OpKind.RoofIn);
        Assert.Equal(3, roof.Positions.Count);
        Assert.Empty(roof.Material);
        Assert.All(sites, s => Assert.Equal(O.PlannedSiteVerdict.Unspecified, s.Verdict));

        // The roof has no native placement: everything else is asked, in one read.
        var asked = PlannedRules.PlannedSiteRequest(sites);
        Assert.Equal(6, asked.Count);
        Assert.DoesNotContain(asked, a => a.Id == "planned/room1/roof");
        Assert.Equal("WoodPlankFloor", asked.Single(a => a.Id == "planned/room1/floor/WoodPlankFloor").Definition);
    }

    [Fact]
    public void TheJoinAttachesRowsAndVerdict()
    {
        var sites = Sites([Owed()]);
        var row = new O.ConstructionActivityOption("planned/room1/wall/Steel", ContainmentCellRules.ShellWallDefinition, new Cell(-1, 0), "Pawn_1", "Steel", "Thing_Steel1",
            new Cell(5, 5), 6, 75, "ConstructFinishFrames", Known(10d), Known(20d), Known(300d));
        var clearing = new O.PlannedSiteClearing("planned/room1/wall/Steel/clear/cut", O.PlannedClearingKind.Cut);
        var answered = new O.ConstructionActivityRead
        {
            Known = true,
            Findings = ["planned_quality_minimum_unset"],
            Planned =
            [
                new O.PlannedSiteAnswer("planned/room1/wall/Steel")
                {
                    Options = [row],
                    Verdict = O.PlannedSiteVerdict.Feasible, Clearing = [clearing],
                },
            ],
        };
        var facts = PlannedRules.JoinPlannedSites(sites, answered);
        var wall = facts.Sites.Single(s => s.Id == "planned/room1/wall/Steel");
        Assert.Equal([row], wall.Options);
        // Feasibility once cleared, and the clearing it owes, come through the join (#3276).
        Assert.Equal(O.PlannedSiteVerdict.Feasible, wall.Verdict);
        Assert.Equal([clearing], wall.Clearing);
        // A site no answer names stays unknown.
        Assert.Equal(O.PlannedSiteVerdict.Unspecified, facts.Sites.Single(s => s.Id == "planned/room1/wall/WoodLog").Verdict);
        Assert.Equal(["planned_quality_minimum_unset"], facts.Findings);
        // A read that never came leaves every site unknown, saying so.
        var missing = PlannedRules.JoinPlannedSites(sites, null);
        Assert.All(missing.Sites, s => Assert.Equal(O.PlannedSiteVerdict.Unspecified, s.Verdict));
        Assert.Equal(["planned_site_read_unknown"], missing.Findings);
    }

    [Fact]
    public void TheRoofAndVentsAreOwedAsIfTheRingStood()
    {
        // Bare ground: Reconcile owes the ring but no roof yet, since nothing encloses the room.
        var room = Room(1, 0);
        var plan = new LayoutPlan { Rooms = [room], Roofed = room.Region };
        var input = new ReconcileInput { Plan = plan, Room = room, Ground = new GroundCensus(), Rows = [], Unroofed = room.Region.Cells().ToHashSet() };
        var now = ReconcileRules.Reconcile(input).Owed;
        Assert.DoesNotContain(now, o => o.Kind == OpKind.RoofIn);
        var stood = PlannedRules.RingStandsGround(input.Ground, now);
        Assert.Equal(now.Where(o => o.Kind is OpKind.WallIn).SelectMany(o => o.Cells).ToHashSet(), stood.Walls.ToHashSet());
        Assert.Empty(input.Ground.Walls);
        var stands = ReconcileRules.Reconcile(input with { Ground = stood }).Owed;
        var sited = PlannedRules.PlannedSitedOps(now, stands);
        Assert.Equal(9, sited.Single(o => o.Kind == OpKind.RoofIn).Cells.Count);
        Assert.Equal(now.Where(o => o.Kind == OpKind.WallIn), sited.Where(o => o.Kind == OpKind.WallIn));
        Assert.Contains(Sites([(room, sited)]), s => s.Id == "planned/room1/roof" && s.Positions.Count == 9);
    }

    /// <summary>
    /// #3657: a wall stands in the stuff it was built in. Asked for swaps, a standing ring owes every wall, one ready at a
    /// time, and the sites offer each other ring stuff as one upgrade over the walls built in another.
    /// </summary>
    [Fact]
    public void AStandingRingOwesSwapsOnlyWhenAskedAndEachOtherRingStuffIsOneUpgradeSite()
    {
        var room = Room(1, 0);
        var input = new ReconcileInput { Plan = new LayoutPlan { Rooms = [room] }, Room = room, Ground = new GroundCensus(), Rows = [] };
        var ground = PlannedRules.RingStandsGround(input.Ground, ReconcileRules.Reconcile(input).Owed);
        foreach (var c in ground.Walls)
        {
            ground.Stuff[c] = "WoodLog";
        }
        var steel = ground.Walls.OrderBy(c => c.X).ThenBy(c => c.Z).First();
        ground.Stuff[steel] = "Steel";
        var standing = input with { Ground = ground };
        Assert.DoesNotContain(ReconcileRules.Reconcile(standing).Owed, o => o.Kind == OpKind.WallUp);

        var swaps = ReconcileRules.Reconcile(standing with { WallUpgrade = _ => true });
        var owed = Assert.Single(swaps.Owed, o => o.Kind == OpKind.WallUp);
        Assert.Equal(ground.Walls.Count, owed.Cells.Count);
        Assert.Single(Assert.Single(swaps.Ready, o => o.Kind == OpKind.WallUp).Cells);

        var sites = Sites([(room, [owed])], standing: ground.Stuff);
        var toSteel = sites.Single(s => s.Id == "planned/room1/wallup/Steel");
        Assert.Equal((OpKind.WallUp, ContainmentCellRules.ShellWallDefinition, ground.Walls.Count - 1), (toSteel.Op, toSteel.Definition, toSteel.Positions.Count));
        Assert.DoesNotContain(steel, toSteel.Positions);
        Assert.Equal([new Amount(Steel, 2 * (ground.Walls.Count - 1))], toSteel.Material);
        Assert.Equal([steel], sites.Single(s => s.Id == "planned/room1/wallup/WoodLog").Positions);
    }

    [Fact]
    public void ASharedWallBelongsToTheLowerRoomOnly()
    {
        var plan = new LayoutPlan { Rooms = [Room(2, 4), Room(1, 0)] };
        var owed = plan.Rooms.Select(room => (room, ReconcileRules.Reconcile(new ReconcileInput { Plan = plan, Room = room, Ground = new GroundCensus(), Rows = [] }).Owed));
        var sites = PlannedRules.PlannedSitesOf(owed, _ => ["WoodLog"], Ring, Piece, Floor, "Vent", NonePlaced, new Dictionary<Cell, string>());
        var ring = sites.Where(s => s.Op is OpKind.WallIn or OpKind.DoorIn).ToList();
        // Two 3x3 rooms with a wall between: 16 + 16 - 5 shared ring cells, as PlannedOwedCost counts them.
        Assert.Equal(27, ring.Sum(s => s.Positions.Count));
        Assert.Equal(16, ring.Where(s => s.Room == 1).Sum(s => s.Positions.Count));
        Assert.Empty(ring.Where(s => s.Room == 2).SelectMany(s => s.Positions).Intersect(ring.Where(s => s.Room == 1).SelectMany(s => s.Positions)));
    }

    [Fact]
    public void PlacedBlueprintsAndFramesAreLeftOut()
    {
        var placed = new HashSet<(string, Cell)> { (ContainmentCellRules.ShellWallDefinition, new Cell(3, 0)), ("TableButcher", new Cell(1, 1)), (ContainmentCellRules.ShellDoorDefinition, new Cell(1, -1)) };
        var sites = Sites([Owed()], placed);
        Assert.Equal([new Cell(-1, 0), new Cell(-1, 1)], sites.Single(s => s.Id == "planned/room1/wall/WoodLog").Positions);
        Assert.DoesNotContain(sites, s => s.Op is OpKind.Build or OpKind.DoorIn);
    }

    /// <summary>A site of any length is asked (#3341): the read has no position cap.</summary>
    [Fact]
    public void ALongSiteIsAsked()
    {
        var cells = Enumerable.Range(0, 200).Select(x => new Cell(x, -1)).ToArray();
        var sites = Sites([(Room(1, 0), [new Operation { Kind = OpKind.WallIn, Cells = cells }])]);
        var asked = PlannedRules.PlannedSiteRequest(sites);
        Assert.NotEmpty(asked);
        Assert.All(asked, s => Assert.Equal(200, s.Positions.Count));
    }
    [Fact]
    public void RingStuffsAreTheCheapestStockedFirstElseTheCheapest()
    {
        Assert.Equal(["Steel", "WoodLog"], PlannedRules.PlannedRingStuffs(
        [
            new PlannedRingStuff("Plasteel", 1, false), new PlannedRingStuff("WoodLog", 5, true),
            new PlannedRingStuff("Steel", 3, true), new PlannedRingStuff("BlocksGranite", 9, true),
        ]));
        Assert.Equal(["Plasteel"], PlannedRules.PlannedRingStuffs([new PlannedRingStuff("Steel", 3, false), new PlannedRingStuff("Plasteel", 1, false)]));
        Assert.Equal([""], PlannedRules.PlannedRingStuffs([]));
    }

    [Fact]
    public void AShelfCandidateIsAOnePiecePlannedSite()
    {
        var candidate = new ShelfCandidate
        {
            Zone = "Zone_3",
            Piece = new InteriorPiece { Slot = "shelf", Def = StorageRules.ShelfDefinition, Size = new Cell(2, 1), Rect = new Rectangle { X = 4, Z = 2, Width = 2, Height = 1 } },
        };
        var site = Assert.Single(PlannedRules.ShelfPlannedSites([candidate, candidate], ("Steel", [new Amount(Steel, 10)])));
        Assert.Equal((0, "Zone_3", OpKind.Build, StorageRules.ShelfDefinition, "Steel"), (site.Room, site.Zone, site.Op, site.Definition, site.Stuff));
        Assert.Equal([candidate.Piece.Wanted().Anchor()], site.Positions);
        Assert.Equal([new Amount(Steel, 10)], site.Material);
        Assert.Empty(PlannedRules.ShelfPlannedSites([candidate], null));
    }

    /// <summary>A room 1 site at cells with one native row delivering from origin (count 0: no material) and the given clearing.</summary>
    private static PlannedBuildSite Answered(string id, OpKind op, Cell[] cells, Cell origin, int count, params O.PlannedSiteClearing[] clearing) =>
        new(id, 1, op, "Wall", "WoodLog", Rotation.North, EquatableArray.From(cells))
        {
            Options = [new O.ConstructionActivityOption(id, "Wall", cells[0], "Pawn_1", "WoodLog", count > 0 ? "stack" : "", origin, count, 75, "Finish",
                Known(1d), Known(1d), Known(10d))],
            Clearing = EquatableArray.From(clearing),
        };

    /// <summary>
    /// Routes between planned sites (#3313): from each activity end of the room (a site's first position, a clearing
    /// row's location) to each other start (a site, reaching its pieces; a source; a clearing start), and the roof from
    /// each pawn's start. A room whose sites no native row answers, and a shelf, are not probed.
    /// </summary>
    [Fact]
    public void RouteProbesRunFromEachEndOfARoomToEachOtherStartAndTheRoofFromStart()
    {
        var cut = new O.PlannedSiteClearing("planned/room1/wall/WoodLog/clear/cut", O.PlannedClearingKind.Cut)
            { Options = [new O.ConstructionActivityOption("planned/room1/wall/WoodLog/clear/cut", "", new Cell(0, 3), "Pawn_1", "", "", new Cell(0, 3), 0, 0, "Cut",
                Known(1d), default, Known(10d))] };
        var wall = Answered("planned/room1/wall/WoodLog", OpKind.WallIn, [new(0, 0), new(0, 1)], new Cell(9, 9), 10, cut);
        var door = Answered("planned/room1/door/WoodLog", OpKind.DoorIn, [new(1, 0)], new Cell(1, 0), 0);
        var roof = new PlannedBuildSite("planned/room1/roof", 1, OpKind.RoofIn, "", "", Rotation.North, [new(1, 1)]);
        var unanswered = new PlannedBuildSite("planned/room2/wall/WoodLog", 2, OpKind.WallIn, "Wall", "WoodLog", Rotation.North, [new(10, 0)]);
        var probes = PlannedRules.PlannedRouteProbes([wall, door, roof, unanswered]);
        // The roof from each pawn's start, reaching its nearest cell.
        var start = Assert.Single(probes, p => p.FromStart);
        Assert.Equal((new Cell(1, 1), new Cell(1, 1)), (start.From, start.To));
        // Ends: door (1,0), roof (1,1), wall (0,0), cut (0,3). Starts: those sites, the wall's source (9,9), the cut (0,3).
        Cell[] ends = [new(1, 0), new(1, 1), new(0, 0), new(0, 3)];
        Cell[] starts = [new(1, 0), new(1, 1), new(9, 9), new(0, 0), new(0, 3)];
        var expected = ends.SelectMany(e => starts.Where(s => s != e).Select(s => (e, s))).ToArray();
        var actual = probes.Where(p => !p.FromStart).Select(p => (p.From, p.To)).ToArray();
        Assert.Equal(expected.Length, actual.Length);
        Assert.True(expected.ToHashSet().SetEquals(actual));
        Assert.Equal(wall.Positions, probes.First(p => p.To == new Cell(0, 0)).Reach);
        Assert.Empty(probes.First(p => p.To == new Cell(9, 9)).Reach);
        Assert.DoesNotContain(probes, p => p.To == new Cell(10, 0) || p.From == new Cell(10, 0));
    }

    /// <summary>The join keeps the read's rows and findings; a missing read leaves every route unknown, with a finding.</summary>
    [Fact]
    public void JoinedRoutesKeepTheReadsRowsAndAMissingReadAddsNone()
    {
        EquatableArray<O.PlannedRouteProbe> asked = [new(new(0, 0), new(1, 0), [new(1, 0)]), new(new(1, 1), new(1, 1), [new(1, 1)]) { FromStart = true }];
        var known = new O.CoupledRoute("Pawn_1", new(0, 0), new(1, 0), Known(2d));
        var fromStart = new O.CoupledRoute("Pawn_1", new(7, 7), new(1, 1), Known(12d)) { FromStart = true };
        var read = new O.ConstructionActivityRead { Known = true, Routes = [known, fromStart], Findings = ["planned_route_out_of_bounds"] };
        var sites = new PlannedSiteFacts { Findings = ["planned_site_read_unknown"] };
        var facts = PlannedRules.JoinPlannedRoutes(sites, asked, read);
        Assert.Equal([known, fromStart], facts.Routes);
        Assert.Equal(["planned_route_out_of_bounds", "planned_site_read_unknown"], facts.Findings);
        var missing = PlannedRules.JoinPlannedRoutes(sites, asked, null);
        Assert.Empty(missing.Routes);
        Assert.Equal(["planned_route_read_unknown", "planned_site_read_unknown"], missing.Findings);
        Assert.Equal(["planned_site_read_unknown"], PlannedRules.JoinPlannedRoutes(sites, [], null).Findings);
    }
}
