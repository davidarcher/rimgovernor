using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The second wall layer's lifecycle (#4047, epic #2795): a room with a standing ring owes a wall on each layer cell,
/// ready, and the climate envelope builds them; with an open ring the ring's walls are ready and the layer's are not,
/// and the room is built (GroundMatches) without its layer; a wall of the colony's on a layer cell stands as the layer
/// and is no foreign thing, while a tree there is cut first; a dropped room's layer is retired with its ring and a
/// kept room's is kept ground; and a replan keeps a standing room's layer where the room stays.
/// </summary>
public sealed class ShellBuildTests
{
    private static readonly Rectangle Hut = new(10, 10, 3, 3);
    private static readonly Cell Door = new(11, 9);
    private static readonly Cell EastLayer = new(14, 11);

    private static PlannedRoom Room(Rectangle rect, Cell door) =>
        new() { Id = 1, Role = PlannedRole.Bedroom, Region = RegionRules.RectRegion(rect), Door = door, DoorSide = new DoorSide.Doored(Rotation.South) };

    /// <summary>The hut's plan with its whole layer: eleven cells one out from the ring, the cell beyond the door and the corners excepted.</summary>
    private static LayoutPlan Plan()
    {
        var room = Room(Hut, Door);
        var plan = new LayoutPlan { NextRoomId = 2, Rooms = [room], Roofed = room.Region };
        return plan with { OuterWalls = RegionRules.RegionOf(plan.OutdoorWallCells(room)) };
    }

    /// <summary>The hut's ring standing with its door; with <paramref name="gap"/> that ring cell is missing.</summary>
    private static GroundCensus Ring(LayoutPlan plan, Cell? gap = null)
    {
        var g = new GroundCensus();
        foreach (var c in plan.Rooms[0].WallRing().Cells())
        {
            if (c == Door)
            {
                g.Doors.Add(c);
            }
            else if (c != gap)
            {
                g.Walls.Add(c);
            }
        }
        return g;
    }

    private static ReconcileInput Input(LayoutPlan plan, GroundCensus ground, IReadOnlyList<SiteCell>? cells = null) =>
        new() { Rooms = RoomTestFacts.NoRooms, Plan = plan, Room = plan.Rooms[0], Ground = ground, Rows = [], Cells = cells };

    private static HashSet<Cell> Cells(IEnumerable<Operation> ops, OpKind kind) => [.. ops.Where(op => op.Kind == kind).SelectMany(op => op.Cells)];

    [Fact]
    public void AStandingRingOwesItsLayerReadyAndTheEnvelopeBuildsIt()
    {
        var plan = Plan();
        Assert.Equal(11, plan.OuterWalls.Cells().Count);
        Assert.True(plan.Valid());
        var ground = Ring(plan);

        var rec = ReconcileRules.Reconcile(Input(plan, ground));
        var wallIn = Assert.Single(rec.Ready, op => op.Kind == OpKind.WallIn);
        Assert.Equal(plan.OuterWalls.Cells().ToHashSet(), wallIn.Cells.ToHashSet());
        Assert.Empty(rec.Holds);
        // The room is built without its layer.
        Assert.True(plan.GroundMatches(plan.Rooms[0], ground));
        // The envelope's call builds the layer as it builds vents and roofs.
        var envelope = ClimateEnvelopeRules.EnvelopeOps(ReconcileRules.ReconcileRoom(Input(plan, ground)), plan);
        Assert.Equal(wallIn.Cells.ToHashSet(), Cells(envelope, OpKind.WallIn));
    }

    [Fact]
    public void AnOpenRingBuildsItselfBeforeItsLayer()
    {
        var plan = Plan();
        var gap = new Cell(10, 9);
        var ground = Ring(plan, gap);

        var rec = ReconcileRules.Reconcile(Input(plan, ground));
        Assert.Equal([gap], Cells(rec.Ready, OpKind.WallIn));
        var owed = Cells(rec.Owed, OpKind.WallIn);
        Assert.Contains(gap, owed);
        Assert.True(plan.OuterWalls.Cells().All(owed.Contains), "the layer is owed, not ready");
        Assert.Empty(ClimateEnvelopeRules.EnvelopeOps(ReconcileRules.ReconcileRoom(Input(plan, ground)), plan));
    }

    private static Thing PlayerWall() => new()
    {
        Def = ContainmentCellRules.ShellWallDefinition,
        Id = 7,
        Category = ThingCategory.Building,
        Faction = ThingFaction.Player,
        Flags = ThingMarks.Edifice | ThingMarks.HoldsRoof | ThingMarks.Deconstructible | ThingMarks.Impassable,
    };

    private static Thing Tree() => new() { Def = "Plant_TreeOak", Id = 8, Category = ThingCategory.Plant, Flags = ThingMarks.Impassable };

    [Fact]
    public void AStandingLayerWallIsNoForeignThingAndATreeOnTheLayerIsCutFirst()
    {
        var plan = Plan();
        var ground = Ring(plan);
        ground.Walls.Add(EastLayer);
        var tree = new Cell(10, 14);
        var cells = new[] { SiteCellFixtures.At(EastLayer, things: [PlayerWall()]), SiteCellFixtures.At(tree, things: [Tree()]) };

        var rec = ReconcileRules.Reconcile(Input(plan, ground, cells));
        var wallIn = Cells(rec.Ready, OpKind.WallIn);
        Assert.DoesNotContain(EastLayer, Cells(rec.Owed, OpKind.WallIn));
        Assert.DoesNotContain(rec.Owed, op => op.Targets.Any(t => t.Class == "foreign" && t.Minimum == EastLayer));
        // The tree is foreign work on the layer: cut first, the wall waits.
        Assert.Contains(tree, Cells(rec.Ready, OpKind.Cut));
        Assert.DoesNotContain(tree, wallIn);
        Assert.Contains(tree, Cells(rec.Owed, OpKind.WallIn));
        Assert.Equal(9, wallIn.Count);
    }

    [Fact]
    public void ADroppedRoomsLayerIsRetiredWithItsRingAndAKeptRoomsIsKept()
    {
        var prev = Plan();
        var layer = RegionRules.RegionOf(prev.OuterWallCells(prev.Rooms[0]));
        Assert.Equal(11, layer.Cells().Count);

        var kept = ClearanceGroundRules.RetiredGroundOf(prev);
        Assert.True(layer.Cells().All(kept.IsKept));

        var next = new LayoutPlan { NextRoomId = 3, Rooms = [Room(new Rectangle(20, 10, 3, 3), new Cell(21, 9)) with { Id = 2 }] };
        var retired = LayoutDeriveRules.RetiredGround(prev, next, new HashSet<int> { 1 });
        Assert.Contains(retired, g => g.Equal(layer));
        Assert.Contains(retired, g => g.Equal(RegionRules.RectRegion(ClearanceGroundRules.RoomGround(Hut))));
        // A room that never started leaves nothing to retire.
        Assert.Empty(LayoutDeriveRules.RetiredGround(prev, next, new HashSet<int>()));
    }

    [Fact]
    public void AReplanKeepsAStandingRoomsLayerWhereItStaysAndDropsItWhereItMoves()
    {
        var prev = Plan();
        var stays = new LayoutPlan { NextRoomId = 2, Rooms = [Room(Hut, Door)], Roofed = prev.Roofed };
        var carried = LayoutPlan.WithShellOf(prev, stays);
        Assert.Equal(prev.OuterWalls.Cells(), carried.OuterWalls.Cells());
        Assert.True(carried.Valid());

        var moved = new LayoutPlan { NextRoomId = 2, Rooms = [Room(new Rectangle(15, 10, 3, 3), new Cell(16, 9))] };
        Assert.True(LayoutPlan.WithShellOf(prev, moved).OuterWalls.Empty());

        // A new room whose ring stands on the east layer cells takes them; the rest of the layer stays.
        var neighbour = Room(new Rectangle(15, 10, 3, 3), new Cell(16, 9)) with { Id = 2 };
        var crowded = LayoutPlan.WithShellOf(prev, stays with { NextRoomId = 3, Rooms = [stays.Rooms[0], neighbour] });
        Assert.Equal(8, crowded.OuterWalls.Cells().Count);
        Assert.DoesNotContain(crowded.OuterWalls.Cells(), c => c.X == 14);
        Assert.True(crowded.Valid());
    }
}
