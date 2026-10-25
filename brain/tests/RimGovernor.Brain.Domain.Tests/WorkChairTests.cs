using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using Xunit;

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
            [Bench] = new(Bench, new Cell(3, 1), RoomRole.Workshop, new InteractionCell.At(new Cell(0, -1))),
            [Chair] = new(Chair, new Cell(1, 1), RoomRole.None, new InteractionCell.None()),
        },
    };

    private static PlannedRoom Workshop(IReadOnlyDictionary<string, string> chairs) => new()
    {
        Id = 1,
        Role = PlannedRole.Workshop,
        Region = RegionRules.RectRegion(new Rectangle { X = 10, Z = 10, Width = 6, Height = 6 }),
        Door = new Cell(12, 9),
        DoorSide = new DoorSide.Doored(Rotation.South),
        WorkChairs = chairs,
    };

    private static readonly Dictionary<string, string> Chosen = new() { [Bench] = Chair };

    private static CurrentBuilding Standing(string def, Cell cell, Rotation rot, params Cell[] cells) => ConstructionObservationFixtures.Built(new Building(def, cell, rot, ""), id: $"{def}{cell.X}_{cell.Z}", cells: [.. cells.Length > 0 ? cells : [cell]]);

    private static readonly CurrentBuilding BenchNorth = Standing(Bench, new Cell(12, 12), Rotation.North, new(11, 12), new(12, 12), new(13, 12));

    private static CurrentConstruction Census(params CurrentBuilding[] built) => CurrentConstruction.Empty with { Buildings = [.. built] };

    private static EquatableArray<WorkChairSeat> Owed(PlannedRoom room, CurrentConstruction census) =>
        WorkChairRules.OwedSeats(new LayoutPlan { Rooms = [room] }, census, Shapes);

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
        var blueprint = CurrentConstruction.Empty with
        {
            Buildings = [BenchNorth],
            Sites = [ConstructionObservationFixtures.Site(new Building(Chair, new Cell(12, 11), Rotation.North, ""), stage: "blueprint")],
        };
        Assert.Empty(Owed(Workshop(Chosen), blueprint));

        // A bench outside the planned room is not that room's.
        Assert.Empty(Owed(Workshop(Chosen), Census(Standing(Bench, new Cell(30, 30), Rotation.North, new(29, 30), new(30, 30), new(31, 30)))));
    }

    private static ColonyReading Projection(PlannedRoom room, CurrentConstruction? census)
    {
        var projection = ColonyFactsCoreTests.Reading() with
        {
            Facts = ColonyFactsCoreTests.Reading().Facts with
            {
                LayoutPlan = new LayoutPlan
                {
                    Rooms = [room],
                    NextRoomId = 2
                },
                Shapes = Shapes
            }
        };
        return projection with
        {
            Facts = projection.Facts with
            {
                CurrentConstruction = census!,
                Census = projection.Facts.Census with { ColonistCount = 1 }
            }
        };
    }

    private static BuildingPlanner Planner() => new() { Concern = RoomsFamily.WorkChairs.FilingKey, Need = RoomsFamily.WorkChairs, Phase = Phase.ComfortWorkChair };

    [Fact]
    public void ThePlannerOrdersTheChairOnlyOnTheBenchInteractionCell()
    {
        var facts = Projection(Workshop(Chosen), Census(BenchNorth));
        var (resolved, reason) = Planner().SelectWorkChair(facts);
        Assert.True(reason.IsZero, reason.ToString());
        Assert.Equal((Chair, ColonyPlanningRules.BuildStuff(facts.Facts, ResourceStockRules.Count(facts.Snapshot.Resources), Chair)), (resolved!.Definition, resolved.Stuff));
        Assert.Equal([new Cell(12, 11)], resolved.Cells);
        Assert.Equal([new Cell(12, 11)], resolved.Adjacent);
        var (count, method, selection) = resolved.Selection(facts);
        Assert.True(selection.IsZero, selection.ToString());
        Assert.Equal((1L, new MethodId("work-chair-DiningChair-12-11")), (count, method));
        Assert.True(RoomsReview.WorkChairsOwed(facts));
    }

    [Fact]
    public void ARoomWithNoChairChosenOrdersNothing()
    {
        var facts = Projection(Workshop(new Dictionary<string, string>()), Census(BenchNorth));
        var (resolved, reason) = Planner().SelectWorkChair(facts);
        Assert.Null(resolved);
        Assert.Equal(Verdict.BuildingReasonNoDeficit, reason);
        Assert.False(RoomsReview.WorkChairsOwed(facts));
    }

    [Fact]
    public void AnUnreadCensusRefusesChairPlanning()
    {
        var facts = Projection(Workshop(Chosen), null);
        Assert.Throws<RequiredFactException>(() => RoomsReview.WorkChairsOwed(facts));
        Assert.Throws<RequiredFactException>(() => Planner().SelectWorkChair(facts));
    }
}
