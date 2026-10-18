using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>The clear side leaves a planned room's interior furniture alone while its ring is unfinished (#3479).</summary>
public sealed class InteriorFurnitureClearTests
{
    private static Rectangle Rect(int x, int z) => new() { X = x, Z = z, Width = 3, Height = 3 };

    private static ClearanceTarget Row(string id, string def, Cell c, string cls = "") =>
        new() { EntityId = id, DefName = def, Minimum = c, Maximum = c, Class = cls, Player = true, Deconstructible = true };

    private static GroundStep? Step(PlannedRole role, IReadOnlyList<ClearanceTarget> rows, RetiredGround? retired = null) =>
        ClearanceReconcileRules.PlannedGroundStep(
            new LayoutPlan { Rooms = [new PlannedRoom { Id = 1, Role = role, Region = RegionRules.RectRegion(Rect(0, 0)) }] },
            new GroundCensus(),
            rows,
            [],
            new RoomObservation(),
            retired ?? new RetiredGround(),
            null
        );

    public static TheoryData<PlannedRole> Roles => new() { PlannedRole.Hospital, PlannedRole.Bedroom, PlannedRole.Throne };

    [Theory]
    [MemberData(nameof(Roles))]
    public void InteriorFurnitureIsNeverATargetWhileRingWorkProceeds(PlannedRole role)
    {
        var interior = Row("a", "Bed", new Cell(1, 1));
        var step = Step(role, [interior]);
        Assert.True(step is null || step.Targets.Items.All(t => t.EntityId != "a"));
    }

    [Theory]
    [MemberData(nameof(Roles))]
    public void RingCellFurnitureStillClears(PlannedRole role)
    {
        var step = Step(role, [Row("a", "Bed", new Cell(1, 1)), Row("b", "Bed", new Cell(-1, 0))]);
        Assert.Equal(["b"], step!.Targets.Items.Select(t => t.EntityId));
    }

    [Fact]
    public void AnAncientWallInTheInteriorStillReachesReconcile()
    {
        // Owed, not ready: an interior wall waits for the ring to stand.
        var work = ClearanceReconcileRules.PlannedGroundWork(
            new LayoutPlan { Rooms = [new PlannedRoom { Id = 1, Role = PlannedRole.Bedroom, Region = RegionRules.RectRegion(Rect(0, 0)) }] },
            new GroundCensus(),
            [Row("w", "AncientWall", new Cell(1, 1), "ancient_wall_door")],
            [],
            new RoomObservation(),
            new RetiredGround(),
            null
        );
        Assert.Contains("w", work);
    }

    [Fact]
    public void RetiredGroundFurnitureStillClears()
    {
        var retired = new RetiredGround { Ground = EquatableArray.From(new[] { RegionRules.RectRegion(Rect(10, 10)) }) };
        var step = Step(PlannedRole.Bedroom, [Row("r", "Bed", new Cell(11, 11))], retired);
        Assert.Equal(["r"], step!.Targets.Items.Select(t => t.EntityId));
    }
}
