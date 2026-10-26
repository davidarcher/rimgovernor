using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Runtime;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class BedReplacementCatalogTests
{
    private static readonly PawnId Owner = new("owner");

    private static SleepingObservation Sleeping(string definition, RoyalTitle? title = null) => new()
    {
        People = [new SleepingPerson { Native = SleepingNativeFixtures.Person(Owner), Pawn = SleepingNativeFixtures.Pawn(Owner, "owned"), Title = title }],
        Beds = [new SleepingBed
        {
            Native = SleepingNativeFixtures.Bed("owned", room: "room", owners: [Owner],
                quality: new RimGovernor.Brain.GameFacts.Observations.BedQuality.Graded(RimGovernor.Brain.GameFacts.Defs.QualityCategory.Poor)),
            Building = SleepingNativeFixtures.Building("owned", new Resource(definition)), Humanlike = true,
        }],
        Rooms = [new UpkeepRoom
        {
            StatKeys = SleepingNativeFixtures.StatKeys,
            Native = SleepingNativeFixtures.Room("room", RoomRole.Bedroom, new RoomQuality(100, 0, 10, 10, 0), 64), Beds = ["owned"],
        }],
    };

    private static FurnitureRoom Room(PieceShapes shapes) => new()
    {
        Id = "room", Room = new InteriorRoom { Role = RoomRole.Bedroom, Interior = new Rectangle(0, 0, 8, 8), Shapes = shapes },
    };

    private static PieceShapes Shapes(params string[] definitions) => new()
    {
        Defs = definitions.ToDictionary(def => def, def => new InteriorPieceDef(def, new Cell(1, 3), RoomRole.Bedroom, new InteractionCell.None())),
        Furniture = new RoomFurniture { Beds = EquatableArray.From(definitions.Select(def => new FurnitureBed { Def = def, Slots = 1 })) },
    };

    [Theory]
    [InlineData(25)]
    [InlineData(97)]
    public void EveryCatalogBedCanReachReplacementReinstallAndTheNativeIntent(int count)
    {
        var definitions = Enumerable.Range(0, count).Select(i => $"InstalledBed{i:D3}").ToArray();
        var shapes = Shapes(definitions);
        foreach (var definition in definitions)
        {
            var room = Room(shapes);
            var (replacement, due) = RoomQualityRules.NextBedReplacement(Sleeping(definition),
                new Dictionary<string, RoomTarget> { [room.Id] = new() { Min = 10 } }, [room], _ => true, new BedMaterials(), new PreferencesRecord());
            Assert.True(due);
            Assert.Equal(BedReplacementStep.Build, replacement.Step);
            var piece = replacement.Piece(shapes);
            Assert.Equal(new Cell(1, 3), piece.Size);
            Assert.NotNull(CoupleBedRules.BedSpot(room, new Resource(definition)));
            var action = DomainAction.NewBuildingAction(new("replacement-0"), new Building(piece.Def, piece.Anchor, piece.Rot, piece.Stuff), ConstructionTier.Sustain);
            var intent = Assert.IsType<RimGovernor.Brain.GameOrders.Operations.ActionIntent.BuildingCase>(IntentActionRules.IntentAction("replacement", action).Intent).Value;
            Assert.Equal(definition, intent.Placement!.DefName);
            var plan = new PlanSpec(new("replacement"), new(1), [action]);
            var progress = new Progress(plan, action.Id);
            Assert.True(BuildingPlanner.PendingBed(progress, shapes.Furniture));
            Assert.False(BuildingPlanner.PendingBed(progress.Cancel(), shapes.Furniture));
        }
    }

    [Fact]
    public void RoyalBedAlternativesUseCatalogMembershipAndCurrentAvailability()
    {
        var shapes = Shapes("owned-bed", "locked-bed", "available-bed");
        var title = new RoyalTitle { BedroomThings = [new() { AnyOf = [new("locked-bed"), new("available-bed")], Count = 1 }] };
        var (replacement, due) = RoomQualityRules.NextBedReplacement(Sleeping("owned-bed", title),
            new Dictionary<string, RoomTarget>(), [Room(shapes)], def => def != "locked-bed", new BedMaterials(), new PreferencesRecord());
        Assert.True(due);
        Assert.Equal("available-bed", replacement.Def);
        Assert.Equal(BedReplacementStep.Build, replacement.Step);
        Assert.False(RoomQualityRules.NextBedReplacement(Sleeping("owned-bed", title),
            new Dictionary<string, RoomTarget>(), [Room(shapes)], _ => false, new BedMaterials(), new PreferencesRecord()).Ok);
    }

    [Fact]
    public void UnknownBedsDoNotInventShapesAndKnownBedsRequireTheirShape()
    {
        var shapes = Shapes("known-bed");
        Assert.Null(CoupleBedRules.BedSpot(Room(shapes), new Resource("unknown-bed")));
        var missing = shapes with { Defs = new Dictionary<string, InteriorPieceDef>() };
        Assert.Throws<RequiredFactException>(() => CoupleBedRules.BedSpot(Room(missing), new Resource("known-bed")));
        Assert.Throws<RequiredFactException>(() => new BedReplacement { Def = "known-bed" }.Piece(missing));
        Assert.Null(CoupleBedRules.BedSpot(Room(shapes) with { Room = Room(shapes).Room with { Interior = new Rectangle(0, 0, 1, 1) } }, new Resource("known-bed")));
    }
}
