using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The pure side of the planned-room gate (#3278): when a room counts as started, how the in-memory record follows the
/// reviews, and which reconcile operations a selection keeps.
/// </summary>
public sealed class PlannedRoomSelectionTests
{
    /// <summary>Rooms 1 and 2 side by side, 3x3 interiors sharing the ring column x = 8; room 1 has a bed piece.</summary>
    private static PlannedRoom Room(int id, int x) => new()
    {
        Id = id, Role = PlannedRole.Bedroom, Region = RegionRules.RectRegion(new Rectangle { X = x, Z = 5, Width = 3, Height = 3 }),
        Door = new Cell(x + 1, 4), DoorRot = Rotation.South,
    };

    private static readonly LayoutPlan Plan = new()
    {
        Rooms = [Room(1, 5), Room(2, 9)],
        NextRoomId = 3,
        Pieces = new Dictionary<int, IReadOnlyList<InteriorPiece>>
        {
            [1] = [new InteriorPiece { Def = "Bed", Rect = new Rectangle { X = 5, Z = 5, Width = 1, Height = 2 }, Size = new Cell(1, 2), Rot = Rotation.North }],
        },
    };

    private static bool Started(GroundCensus? ground = null, Cell[]? sites = null, (string, Cell)[]? standing = null) =>
        PlannedRules.PlannedRoomStarted(Plan, Plan.Rooms[0], ground ?? new GroundCensus(), sites ?? [], (standing ?? []).ToHashSet());

    [Fact]
    public void ARoomStartsOnItsOwnRingItsGroundOrAPieceAtItsAnchorButNotOnASharedWall()
    {
        Assert.False(Started());
        // The shared column belongs to both rooms: a wall there starts neither.
        Assert.False(Started(new GroundCensus { Walls = [new(8, 6)] }));
        Assert.False(Started(sites: [new(8, 6)]));
        // Natural rock is not a colony-built wall.
        Assert.False(Started(new GroundCensus { Rock = [new(4, 6)] }));
        Assert.True(Started(new GroundCensus { Walls = [new(4, 6)] }));
        Assert.True(Started(new GroundCensus { Doors = [new(6, 4)] }));
        Assert.True(Started(sites: [new(4, 4)]));
        Assert.True(Started(sites: [new(6, 6)]));
        var bed = Plan.Pieces[1][0].Wanted().Anchor();
        Assert.True(Started(standing: [("Bed", bed)]));
        Assert.False(Started(standing: [("Table", bed)]));
    }

    private static SelectedPlannedSite Wall(int room, string stuff) =>
        new(room, OpKind.WallIn, ContainmentCellRules.ShellWallDefinition, stuff, [new(4, 4)]);

    [Fact]
    public void TheLatestKnownSelectionReplacesTheRecordAndAStartedRoomClears()
    {
        var previous = new PlannedSelection { Assessed = [1, 2], Sites = [Wall(1, "WoodLog"), Wall(2, "WoodLog")] };
        // A review that compared nothing keeps the record.
        Assert.Equal(previous, PlannedRules.PlannedSelectionMemory(previous, null, _ => false));
        // A later selection that drops room 2's site while it is unstarted drops it.
        var latest = new PlannedSelection { Assessed = [1, 2], Sites = [Wall(1, "BlocksGranite")] };
        Assert.Equal(latest, PlannedRules.PlannedSelectionMemory(previous, latest, _ => false));
        // Observed placement clears the room's sites; the room stays assessed, and the reconcile places it as started.
        var placed = PlannedRules.PlannedSelectionMemory(previous, null, room => room == 1);
        Assert.Equal([Wall(2, "WoodLog")], placed.Sites);
        Assert.Equal(PlannedRoomGate.Place, PlannedRules.PlannedRoomGateOf(placed, new PlannedRoom { Id = 1 }, started: true).Gate);
        Assert.Equal(PlannedRoomGate.Selected, PlannedRules.PlannedRoomGateOf(placed, new PlannedRoom { Id = 2 }, started: false).Gate);
        Assert.Equal(PlannedRoomGate.Hold, PlannedRules.PlannedRoomGateOf(placed, new PlannedRoom { Id = 3 }, started: false).Gate);
    }

    /// <summary>
    /// A review whose comparison could not reach a room (#3676; a running method held every colonist, so no planned
    /// room was a candidate) has not reassessed it: the room keeps its selected sites, in their stuff, rather than reading
    /// as never assessed, which the reconcile holds. A room it did reassess follows the latest;
    /// a started room or one the plan dropped is not carried.
    /// </summary>
    [Fact]
    public void ARoomTheLatestComparisonDidNotAssessKeepsItsPreviousSelection()
    {
        var previous = new PlannedSelection { Assessed = [1, 2, 3], Sites = [Wall(1, "WoodLog"), Wall(2, "WoodLog"), Wall(3, "WoodLog")] };
        var none = new PlannedSelection();
        var kept = PlannedRules.PlannedSelectionMemory(previous, none, _ => false);
        Assert.Equal(previous, kept);
        Assert.Equal(PlannedRoomGate.Selected, PlannedRules.PlannedRoomGateOf(kept, new PlannedRoom { Id = 2 }, started: false).Gate);
        // Room 1 reassessed and held; room 2 started; room 3 left the plan.
        var latest = new PlannedSelection { Assessed = [1] };
        var next = PlannedRules.PlannedSelectionMemory(previous, latest, room => room == 2, room => room != 3);
        Assert.Equal([1], next.Assessed);
        Assert.Empty(next.Sites);
        Assert.Equal(PlannedRoomGate.Hold, PlannedRules.PlannedRoomGateOf(next, new PlannedRoom { Id = 1 }, started: false).Gate);
    }

    [Fact]
    public void ASelectionKeepsOnlyItsSitesCellsPiecesAndTheClearingOverThem()
    {
        var piece = new WantedPiece { DefName = "Bed", Minimum = new(5, 5), Maximum = new(5, 6), Size = new(1, 2), Rot = Rotation.North };
        var other = piece with { DefName = "Dresser", Minimum = new(7, 7), Maximum = new(7, 7), Size = new(1, 1) };
        IReadOnlyList<Operation> ops =
        [
            new() { Kind = OpKind.WallIn, Cells = [new(4, 4), new(4, 5), new(8, 5)] },
            new() { Kind = OpKind.DoorIn, Cells = [new(6, 4)] },
            new() { Kind = OpKind.FloorIn, Floors = [new() { Cell = new(5, 5), DefName = "WoodPlankFloor", Designated = false }, new() { Cell = new(6, 6), DefName = "WoodPlankFloor", Designated = false }] },
            new() { Kind = OpKind.Build, Pieces = [piece, other] },
            new() { Kind = OpKind.Cut, Targets = [new() { EntityId = "plant-1", Minimum = new(4, 5), Maximum = new(4, 5) }, new() { EntityId = "plant-2", Minimum = new(7, 7), Maximum = new(7, 7) }] },
            new() { Kind = OpKind.WallUp, Cells = [new(4, 4)] },
        ];
        SelectedPlannedSite[] sites =
        [
            new(1, OpKind.WallIn, ContainmentCellRules.ShellWallDefinition, "BlocksGranite", [new(4, 4), new(4, 5)]),
            new(1, OpKind.FloorIn, "WoodPlankFloor", "", [new(5, 5)]),
            new(1, OpKind.Build, "Bed", "WoodLog", [piece.Anchor()]),
        ];
        var kept = PlannedRules.SelectedPlannedOps(ops, sites);
        Assert.Equal([new Cell(4, 4), new Cell(4, 5)], Assert.Single(kept, o => o.Kind == OpKind.WallIn).Cells);
        Assert.DoesNotContain(kept, o => o.Kind is OpKind.DoorIn or OpKind.WallUp);
        Assert.Equal([new Cell(5, 5)], Assert.Single(kept, o => o.Kind == OpKind.FloorIn).Floors.Select(f => f.Cell));
        Assert.Equal(["Bed"], Assert.Single(kept, o => o.Kind == OpKind.Build).Pieces.Select(p => p.DefName));
        Assert.Equal(["plant-1"], Assert.Single(kept, o => o.Kind == OpKind.Cut).Targets.Select(t => t.EntityId));

        Assert.Equal(("BlocksGranite", "BlocksGranite"), PlannedRules.SelectedRingStuff(sites));
        Assert.Null(PlannedRules.SelectedRingStuff([sites[1]]));
    }

    /// <summary>
    /// #3754: an unstarted ringed room no comparison assessed is held, not placed; a ringless role (a training range) has
    /// no ring to open and is placed; a started room is placed whatever the record says.
    /// </summary>
    [Fact]
    public void AnUnassessedRingedRoomIsHeldARangeIsPlacedAndAStartedRoomIsPlaced()
    {
        var empty = new PlannedSelection();
        Assert.Equal((PlannedRoomGate.Hold, 0), Gate(empty, Plan.Rooms[0], started: false));
        Assert.Equal((PlannedRoomGate.Place, 0), Gate(empty, Plan.Rooms[0] with { Role = PlannedRole.TrainingRange }, started: false));
        Assert.Equal((PlannedRoomGate.Place, 0), Gate(empty, Plan.Rooms[0], started: true));
        var held = new PlannedSelection { Assessed = [1] };
        Assert.Equal((PlannedRoomGate.Place, 0), Gate(held, Plan.Rooms[0], started: true));
    }

    private static (PlannedRoomGate, int) Gate(PlannedSelection record, PlannedRoom room, bool started)
    {
        var (gate, sites) = PlannedRules.PlannedRoomGateOf(record, room, started);
        return (gate, sites.Count);
    }

    /// <summary>Room 1 alone, its ring standing but for <paramref name="missing"/>, its template one bed (none without <paramref name="bed"/>).</summary>
    private static IReadOnlyList<Operation> Owed(bool bed = true, params Cell[] missing)
    {
        var room = Plan.Rooms[0];
        var plan = Plan with { Rooms = [room], NextRoomId = 2 };
        var doors = plan.ShellDoors(room).ToHashSet();
        var ground = new GroundCensus
        {
            Walls = [.. room.WallRing().Cells().Where(c => !doors.Contains(c) && !missing.Contains(c))],
            Doors = [.. doors.Where(c => !missing.Contains(c))],
        };
        IReadOnlyList<WantedPiece> furniture = bed ? [Plan.Pieces[1][0].Wanted()] : [];
        return ReconcileRules.Reconcile(new ReconcileInput { Rooms = RoomObservation.NoRooms, Plan = plan, Room = room, Ground = ground, Furniture = furniture }).Owed;
    }

    /// <summary>
    /// #3754 (D3): a started room is open until its reconcile owes nothing a builder can do now. Owing only a bed no
    /// builder can work (its research missing), it is done and frees its builders; the same bed workable keeps it open;
    /// owing a wall as well, it is open; owing nothing, it is done. The runtime's blocked reading is
    /// PlannedRoomGateTests.ABedBlockedOnComplexFurnitureLeavesTheRoomDone.
    /// </summary>
    [Fact]
    public void ARoomOwingOnlyABlockedBedIsDoneAndOneOwingAWallIsOpen()
    {
        static bool BedBlocked(WantedPiece piece) => piece.DefName == "Bed";
        var bedOnly = Owed();
        Assert.Equal([OpKind.Build], bedOnly.Select(op => op.Kind));
        Assert.True(PlannedRules.PlannedRoomDone(bedOnly, BedBlocked));
        Assert.False(PlannedRules.PlannedRoomDone(bedOnly, _ => false));
        var wall = Owed(true, new Cell(4, 6));
        Assert.Contains(wall, op => op.Kind == OpKind.WallIn);
        Assert.False(PlannedRules.PlannedRoomDone(wall, BedBlocked));
        Assert.Empty(Owed(bed: false));
        Assert.True(PlannedRules.PlannedRoomDone(Owed(bed: false), _ => false));
        Assert.True(PlannedRules.PlannedRoomDone([], _ => false));
    }
}
