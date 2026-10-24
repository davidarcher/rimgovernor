using System.Text.Json;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store.Codec;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using Op = RimGovernor.Contracts.Wire.Operations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// A dropped started room's blueprints are cancelled and its frames deconstructed (#3742): retired-ground clearance
/// reads the blueprints from the construction census and its step cancels them (THING_DESIGNATION_CANCEL), while a
/// frame, a building in the clearance census, is deconstructed so its delivered materials come back. A blueprint on
/// a kept room's ring is that room's and stays.
/// </summary>
public sealed class RetiredBlueprintsTests
{
    // A dropped 3x3 room's ground (ring included) at (10,10); the kept storage room's ring crosses its east column.
    private static readonly Rectangle Retired = new() { X = 10, Z = 10, Width = 5, Height = 5 };

    private static readonly LayoutPlan Plan = new()
    {
        NextRoomId = 2,
        Rooms = [new PlannedRoom { Id = 1, Role = PlannedRole.Storage, Region = RegionRules.RectRegion(new Rectangle(14, 10, 3, 3)), Door = new Cell(17, 11), DoorRot = Rotation.East }],
        RetiredGround = [RegionRules.RectRegion(Retired)],
    };

    private static ConstructionSite Blueprint(string id, Cell c) =>
        new() { Id = id, Stage = ConstructionSite.BlueprintStage, Building = new Building("Wall", c, Rotation.North, "WoodLog") };

    private static ColonyProjection Colony(params ConstructionSite[] sites) => new()
    {
        LayoutPlan = Known(Plan),
        Rooms = Known(new RoomObservation()),
        Facts = new() { CurrentConstruction = Known(new CurrentConstruction { Colony = true, Sites = [.. sites] }) },
    };

    private static DomainAction RoundTrip(DomainAction action)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            Codecs.Default.Write(writer, action);
        }
        using var json = JsonDocument.Parse(stream.ToArray());
        return Codecs.Default.Read<DomainAction>(json.RootElement);
    }

    [Fact]
    public void ABlueprintOnRetiredGroundIsCancelled()
    {
        IntentActionRules.RegisterIntentKinds();
        var colony = Colony(Blueprint("Blueprint_Build7", new Cell(11, 10)));
        var rows = HomeUpkeepReader.GroundBlueprints(colony)!;

        var (step, ok) = HomeUpkeepReader.PlannedGroundStep(colony, rows, [], null!);

        Assert.True(ok);
        Assert.Equal(GroundPhase.Blueprints, step.Phase);
        Assert.Equal("Blueprint_Build7", Assert.Single(step.Targets.Items).EntityId);
        var (prefix, actions) = HomeUpkeepReader.GroundStepMethod(new PlanId("plan-1"), step);
        Assert.Equal("cancel-Blueprint_Build7-", prefix);
        var action = RoundTrip(Assert.Single(actions));
        Assert.True(action.Deconstruction!.CancelsBlueprint);
        var intent = IntentActionRules.IntentAction("cancel", action).Intent!.Designate!;
        Assert.Equal(Op.ThingDesignation.Cancel, intent.Designation);
        Assert.Equal("Blueprint_Build7", intent.Target!.Id);
        Assert.Null(intent.Guard);
        Assert.Contains("Blueprint_Build7", HomeUpkeepReader.PlannedGroundWork(colony, rows, [], null!));
    }

    [Fact]
    public void AFrameOnRetiredGroundIsDeconstructed()
    {
        IntentActionRules.RegisterIntentKinds();
        var colony = Colony();
        var frame = new ClearanceTarget { EntityId = "Frame_Wall9", DefName = "Frame_Wall", Minimum = new Cell(11, 12), Maximum = new Cell(11, 12), Player = true, Deconstructible = true };

        var (step, ok) = HomeUpkeepReader.PlannedGroundStep(colony, [frame], [], null!);

        Assert.True(ok);
        Assert.Equal(GroundPhase.Furniture, step.Phase);
        var action = Assert.Single(HomeUpkeepReader.GroundActions(new PlanId("plan-1"), step));
        Assert.False(action.Deconstruction!.CancelsBlueprint);
        var intent = IntentActionRules.IntentAction("frame", action).Intent!.Designate!;
        Assert.Equal(Op.ThingDesignation.Deconstruct, intent.Designation);
        Assert.Equal(Op.DesignationGuard.Enclosure, intent.Guard);
    }

    [Fact]
    public void ABlueprintOnAKeptRoomsRingStays()
    {
        // (13,11) lies on the retired ground and on the storage room's west wall.
        var colony = Colony(Blueprint("Blueprint_Build8", new Cell(13, 11)));
        var rows = HomeUpkeepReader.GroundBlueprints(colony)!;

        var (_, ok) = HomeUpkeepReader.PlannedGroundStep(colony, rows, [], null!);

        Assert.False(ok);
        Assert.DoesNotContain("Blueprint_Build8", HomeUpkeepReader.PlannedGroundWork(colony, rows, [], null!));
        Assert.Single(ClearanceGroundRules.RetiredGroundDone(Plan, rows, []));
    }

    [Fact]
    public void RetiredGroundWithABlueprintLeftIsNotDone()
    {
        var colony = Colony(Blueprint("Blueprint_Build7", new Cell(11, 10)));

        Assert.Empty(ClearanceGroundRules.RetiredGroundDone(Plan, HomeUpkeepReader.GroundBlueprints(colony)!, []));
        Assert.Single(ClearanceGroundRules.RetiredGroundDone(Plan, [], []));
    }
}
