using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The rooms/work-chairs Need (#3039): a planned room that chose a work chair for a bench definition gets that chair
/// ordered on the interaction cell of each such bench once it stands; a room with none chosen orders nothing.
/// </summary>
public sealed class WorkChairTests
{
    private const string Bench = "HandTailoringBench";
    private const string Chair = "DiningChair";

    /// <summary>A 3x1 bench worked from the cell south of its centre at rotation North, and the 1x1 chair.</summary>
    private static readonly PieceShapes Shapes = new()
    {
        Defs = new Dictionary<string, InteriorPieceDef>
        {
            [Bench] = new(Bench, new Cell(3, 1), RoomRole.Workshop, new Cell(0, -1)),
            [Chair] = new(Chair, new Cell(1, 1), RoomRole.None, null),
        },
    };

    private static PlannedRoom Workshop(IReadOnlyDictionary<string, string> chairs) => new()
    {
        Id = 1,
        Role = PlannedRole.Workshop,
        Region = RegionRules.RectRegion(new Rectangle { X = 10, Z = 10, Width = 6, Height = 6 }),
        Door = new Cell(12, 9),
        DoorRot = Rotation.South,
        WorkChairs = chairs,
    };

    private static readonly Dictionary<string, string> Chosen = new() { [Bench] = Chair };

    private static CurrentBuilding Standing(string def, Cell cell, Rotation rot, params Cell[] cells) => new()
    {
        Id = $"{def}{cell.X}_{cell.Z}",
        Building = new Building(def, cell, rot, ""),
        Cells = [.. cells.Length > 0 ? cells : [cell]],
    };

    private static readonly CurrentBuilding BenchNorth = Standing(Bench, new Cell(12, 12), Rotation.North, new(11, 12), new(12, 12), new(13, 12));

    private static Fact<CurrentConstruction> Census(params CurrentBuilding[] built) => Known(new CurrentConstruction { Colony = true, Buildings = [.. built] });

    private static EquatableArray<WorkChairSeat> Owed(PlannedRoom room, Fact<CurrentConstruction> census)
    {
        Assert.True(WorkChairRules.OwedSeats(new LayoutPlan { Rooms = [room] }, census, Shapes).TryGetValue(out var seats));
        return seats;
    }

    [Fact]
    public void AStandingBenchOwesItsChosenChairOnItsInteractionCell()
    {
        Assert.Equal([new WorkChairSeat(Chair, new Cell(12, 11))], Owed(Workshop(Chosen), Census(BenchNorth)));

        // The cell turns with the bench: east of north is the west side.
        var east = Standing(Bench, new Cell(12, 12), Rotation.East, new(12, 11), new(12, 12), new(12, 13));
        Assert.Equal([new WorkChairSeat(Chair, new Cell(11, 12))], Owed(Workshop(Chosen), Census(east)));
    }

    [Fact]
    public void NothingIsOwedWithNoChairChosenNoBenchStandingOrTheSeatTaken()
    {
        Assert.Empty(Owed(Workshop(new Dictionary<string, string>()), Census(BenchNorth)));
        Assert.Empty(Owed(Workshop(Chosen), Census()));
        Assert.Empty(Owed(Workshop(Chosen), Census(BenchNorth, Standing(Chair, new Cell(12, 11), Rotation.North))));

        // A blueprint on the seat is the chair's pending work.
        var blueprint = Known(new CurrentConstruction
        {
            Colony = true,
            Buildings = [BenchNorth],
            Sites = [new ConstructionSite { Building = new Building(Chair, new Cell(12, 11), Rotation.North, ""), Stage = "blueprint" }],
        });
        Assert.Empty(Owed(Workshop(Chosen), blueprint));

        // A bench outside the planned room is not that room's.
        Assert.Empty(Owed(Workshop(Chosen), Census(Standing(Bench, new Cell(30, 30), Rotation.North, new(29, 30), new(30, 30), new(31, 30)))));

        Assert.False(WorkChairRules.OwedSeats(new LayoutPlan { Rooms = [Workshop(Chosen)] }, Unknown<CurrentConstruction>(), Shapes).IsKnown);
    }

    private static ColonyProjection Projection(PlannedRoom room, Fact<CurrentConstruction> census)
    {
        var projection = new ColonyProjection { LayoutPlan = Known(new LayoutPlan { Rooms = [room], NextRoomId = 2 }), Shapes = Shapes };
        return projection with { Facts = projection.Facts with { CurrentConstruction = census, Colonists = Known(1L) } };
    }

    private static RoundsBuildingPlanner Planner() => new() { Concern = RoomsFamily.WorkChairs.FilingKey, Need = RoomsFamily.WorkChairs, Phase = Phase.ComfortWorkChair };

    [Fact]
    public void ThePlannerOrdersTheChairOnlyOnTheBenchInteractionCell()
    {
        var facts = Projection(Workshop(Chosen), Census(BenchNorth));
        var (resolved, reason) = Planner().SelectWorkChair(facts);
        Assert.True(reason.IsZero, reason.ToString());
        Assert.Equal((Chair, facts.BuildStuff(Chair)), (resolved!.Definition, resolved.Stuff));
        Assert.Equal([new Cell(12, 11)], resolved.Cells);
        Assert.Equal([new Cell(12, 11)], resolved.Adjacent);
        var (count, method, selection) = resolved.Selection(facts);
        Assert.True(selection.IsZero, selection.ToString());
        Assert.Equal((1L, new MethodId("work-chair-DiningChair-12-11")), (count, method));
        Assert.Equal(Known(true), RoomsRounds.WorkChairsOwed(facts));
    }

    [Fact]
    public void ARoomWithNoChairChosenOrdersNothing()
    {
        var facts = Projection(Workshop(new Dictionary<string, string>()), Census(BenchNorth));
        var (resolved, reason) = Planner().SelectWorkChair(facts);
        Assert.Null(resolved);
        Assert.Equal(Verdict.BuildingReasonNoDeficit, reason);
        Assert.Equal(Known(false), RoomsRounds.WorkChairsOwed(facts));
    }
}
