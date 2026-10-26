using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class CoupleBedCatalogTests
{
    private static readonly PawnId A = new("a");
    private static readonly PawnId B = new("b");
    private static readonly PlannedRoom Planned = new()
    {
        Id = 1, Role = PlannedRole.Bedroom, Region = RegionRules.RectRegion(new Rectangle(0, 0, 5, 5)),
        Door = new Cell(2, -1), DoorSide = new DoorSide.Doored(Rotation.South),
    };
    private static readonly LayoutPlan Plan = new() { Rooms = [Planned] };

    private static SleepingPerson Person(PawnId pawn, PawnId partner, string? bed) => new()
    {
        Native = SleepingNativeFixtures.Person(pawn, [partner], true), Pawn = SleepingNativeFixtures.Pawn(pawn, bed),
    };

    private static SleepingObservation Sleeping(bool owned) => new()
    {
        People = [Person(A, B, owned ? "single" : null), Person(B, A, null)],
        Beds = owned ? [new SleepingBed
        {
            Native = SleepingNativeFixtures.Bed("single", owners: [A]),
            Building = SleepingNativeFixtures.Building("single", new Resource("single-bed")), Humanlike = true,
        }] : [],
    };

    private static PieceShapes Shapes(params (string Definition, Cell Size)[] beds)
    {
        var definitions = beds.ToDictionary(b => b.Definition, b => new InteriorPieceDef(b.Definition, b.Size, RoomRole.Bedroom, new InteractionCell.None()));
        foreach (var name in new[] { "EndTable", "Dresser", InteriorRegionRules.InteriorDefs().LampDef })
            definitions.Add(name, new(name, new Cell(1, 1), RoomRole.None, new InteractionCell.None()));
        return new()
        {
            Defs = definitions,
            Furniture = new RoomFurniture
            {
                Beds = EquatableArray.From(beds.Select(b => new FurnitureBed { Def = b.Definition, Slots = 2 })),
                EndTable = new FacilityLink { Def = "EndTable" }, Dresser = new FacilityLink { Def = "Dresser" },
            },
        };
    }

    private static FurnitureRoom Room(PieceShapes shapes, IReadOnlyList<FurniturePiece>? pieces = null) => new()
    {
        Id = "room", Room = new InteriorRoom { Interior = Planned.Region.Bounds(), Role = RoomRole.Bedroom, Shapes = shapes }, Pieces = pieces ?? [],
    };

    [Theory]
    [InlineData(25)]
    [InlineData(97)]
    public void EveryAvailableCoupleBedCanReachTheTemplateAndNativeConstructionIntent(int count)
    {
        var names = Enumerable.Range(0, count).Select(i => $"InstalledCoupleBed{i:D3}").ToArray();
        var shapes = Shapes(names.Select(name => (name, new Cell(2, 3))).ToArray());
        foreach (var name in names)
        {
            var step = Assert.IsType<CoupleBed>(CoupleBedRules.NextCoupleBed(Sleeping(false), Plan, [Room(shapes)], [new Cell(1, 1)], [], def => def == name));
            Assert.Equal(CoupleBedKind.CoupleInstall, step.Kind);
            var piece = Assert.Single(step.Template);
            Assert.Equal(name, piece.DefName);
            Assert.Equal(6, piece.Cells().Count);
            var action = DomainAction.NewBuildingAction(new("couple-0"), new Building(piece.DefName, piece.Minimum, Rotation.North, ""), ConstructionTier.Sustain);
            var intent = Assert.IsType<RimGovernor.Brain.GameOrders.Operations.ActionIntent.BuildingCase>(IntentActionRules.IntentAction("couple", action).Intent).Value;
            Assert.Equal(name, intent.Placement!.DefName);
        }
    }

    [Fact]
    public void PackingRequiresAnActualFittingBedAndIgnoresOnlyTheBedsBeingPacked()
    {
        var shapes = Shapes(("too-large", new Cell(20, 20)), ("fits", new Cell(2, 3)));
        var wholeRoom = new FurniturePiece { Thing = "single", Def = "single-bed", Rect = Planned.Region.Bounds(), Size = new Cell(5, 5) };
        var old = ConstructionObservationFixtures.Built(new Building(wholeRoom.Def, new Cell(0, 0), Rotation.North, ""), id: wholeRoom.Thing, cells: [.. Planned.Region.Cells()]);
        var room = Room(shapes, [wholeRoom]);
        Assert.Equal(CoupleBedKind.CouplePack, CoupleBedRules.NextCoupleBed(Sleeping(true), Plan, [room], [], [old], _ => true)!.Kind);
        Assert.Null(CoupleBedRules.NextCoupleBed(Sleeping(true), Plan, [room], [], [old], def => def == "too-large"));
        Assert.Null(CoupleBedRules.NextCoupleBed(Sleeping(true), Plan, [room], [], [old], _ => false));
        // Furniture remaining after the old bed is packed still blocks its cells.
        var blocker = old with { Observed = old.Observed with { Building = old.Observed.Building with { Id = "blocker", DefName = "unrelated" } } };
        Assert.Null(CoupleBedRules.NextCoupleBed(Sleeping(true), Plan, [room], [], [old, blocker], _ => true));
        var install = CoupleBedRules.NextCoupleBed(Sleeping(false), Plan, [Room(shapes)], [new Cell(1, 1)], [], _ => true)!;
        Assert.Equal("fits", Assert.Single(install.Template).DefName);
    }

    [Fact]
    public void AStandingLeverDoesNotMakeAnOversizedBedFitAndMissingCandidateShapesFail()
    {
        var shapes = Shapes(("too-large", new Cell(20, 20)));
        var lever = ConstructionObservationFixtures.Built(new Building("EndTable", new Cell(4, 4), Rotation.North, ""), id: "lever");
        shapes = shapes with { Furniture = shapes.Furniture with { EndTable = new FacilityLink { Def = "EndTable" } } };
        Assert.Null(CoupleBedRules.NextCoupleBed(Sleeping(false), Plan, [Room(shapes)], [new Cell(1, 1)], [lever], _ => true));
        var missing = shapes with { Defs = new Dictionary<string, InteriorPieceDef>() };
        Assert.Throws<RequiredFactException>(() => CoupleBedRules.NextCoupleBed(Sleeping(false), Plan, [Room(missing)], [new Cell(1, 1)], [], _ => true));
    }
}
