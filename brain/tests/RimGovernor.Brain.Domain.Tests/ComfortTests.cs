using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The comfort census is one total facts record (#3771): every member is set, a recreation source's room a named
/// <see cref="ComfortHost"/> and the joy boredom matrix always read. On the wire it round-trips through
/// <c>OrdersJsonContext</c> exactly.
/// </summary>
public sealed class ComfortTests
{
    /// <summary>A census with no colonists, surfaces, seats, sources or joy kinds: the base every test comfort census starts from.</summary>
    internal static readonly ComfortFacts NoComfort = new() { People = [], Surfaces = [], Dining = [], Recreation = [], Joy = new([], []) };

    /// <summary>The empty census with no catalog rows: the base every test comfort observation starts from.</summary>
    internal static readonly ComfortObservation NoComfortObservation = new()
    {
        Census = NoComfort, RecreationFoothold = "", WatchBuildings = [], Furniture = new(), JoyMethods = [],
    };

    [Fact]
    public void MissingTotalCensusFailsTheDecoder()
    {
        var snapshot = ColonyFactsCoreTests.Snapshot() with { Upkeep = UpkeepFactsTests.NoUpkeep with { Comfort = null! } };
        Assert.Throws<BridgeContractException>(() => ColonyFramesReads.ColonyComfort(snapshot, null));
    }

    [Fact]
    public void PartialProjectionsMustReadTheCensusBeforePlanning()
    {
        Assert.Equal("comfort census", Assert.Throws<RequiredFactException>(() => new ReviewFacts().RequireComfort()).Fact);
        Assert.Equal("basic comfort census", Assert.Throws<RequiredFactException>(() => new ReviewFacts().RequireBasicComfort()).Fact);
    }

    [Fact]
    public void StationsSupplyComfortRegardlessOfTheirRoomsRole()
    {
        PawnId pawn = new("pawn");
        var basic = NoComfortObservation with { Census = NoComfort with
        {
            People = [pawn],
            Surfaces = [new("other-room", [])],
            Dining = [new("chair", "other-room", [pawn], [pawn])],
            Recreation = [new("horseshoes", "gaming", new ComfortHost.Outdoors(), [pawn], [pawn])],
            Joy = new(["gaming"], [new(pawn, [false])]),
        } };
        Assert.True(BasicComfortRules.ReviewBasicComfort(basic).Recovered());
        Assert.True(ComfortRules.ReviewComfort(basic, default, new(0)).Recovered());
        Assert.Single(basic.Census.Dining);
        Assert.Single(basic.Census.Recreation);
    }

    [Fact]
    public void EveryRoutineReadIncludesRoomsAndRetainsActualFacilities()
    {
        PawnId pawn = new("pawn");
        var basic = NoComfortObservation with { Census = NoComfort with
        {
            People = [pawn],
            Dining = [new("chair", "other-room", [pawn], [])],
            Recreation = [new("horseshoes", "gaming", new ComfortHost.Outdoors(), [pawn], [])],
        } };
        var projection = ColonyFactsCoreTests.Reading() with
        {
            Facts = ColonyFactsCoreTests.Reading().Facts with
            {
                Comfort = basic,
                BasicComfort = basic
            }
        };
        var frame = new ReviewFrame { Rooms = new() { Context = ColonyFactsCoreTests.Snapshot().Context!, Rooms = [] } };

        var read = FrameReads.WithFrameRooms(projection, frame, new DiningFurniture());

        Assert.Same(basic, read.Facts.RequireBasicComfort());
        Assert.Single(read.Facts.RequireComfort().Census.Dining);
        Assert.Single(read.Facts.RequireComfort().Census.Recreation);
        Assert.Equal(frame.Rooms!.Rooms, read.Facts.Upkeep.Rooms.Rooms);
    }

    [Fact]
    public void RoomJoinsPreserveTheNativeSleepingTemperature()
    {
        var initial = ColonyFactsCoreTests.Reading();
        var measured = new RimGovernor.Brain.GameFacts.Observations.SleepingTemperature.Indoor(-25, -10);
        initial = initial with { Facts = initial.Facts with { BasicComfort = NoComfortObservation, Census = initial.Snapshot with { SleepingTemperature = measured } } };
        var frame = new ReviewFrame { Rooms = new() { Context = initial.Snapshot.Context, Rooms = [] } };
        var joined = FrameReads.WithFrameRooms(initial, frame, new DiningFurniture());
        Assert.Same(initial.Snapshot, joined.Snapshot);
        Assert.Same(measured, joined.Facts.Census.SleepingTemperature);
    }

    [Fact]
    public void MissingFrameRoomsCannotCertifyHostedComfort()
    {
        var projection = ColonyFactsCoreTests.Reading() with
        {
            Facts = ColonyFactsCoreTests.Reading().Facts with
            {
                Comfort = NoComfortObservation,
                BasicComfort = NoComfortObservation
            }
        };
        Assert.Throws<ObservationContractException>(() => FrameReads.WithFrameRooms(projection, new ReviewFrame(), new DiningFurniture()));
    }
    [Theory]
    [InlineData("comfort")]
    [InlineData("people")]
    [InlineData("surfaces")]
    [InlineData("dining")]
    [InlineData("recreation")]
    [InlineData("joy")]
    public void RequiredWireCensusMembersCannotBeOmitted(string member)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(OrdersJson.Serialize(ColonyFactsCoreTests.Snapshot()))!.AsObject();
        var upkeep = json["upkeep"]!.AsObject();
        var parent = member == "comfort" ? upkeep : upkeep["comfort"]!.AsObject();
        Assert.True(parent.Remove(member));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => OrdersJson.Deserialize<ColonyFactsSnapshot>(json.ToJsonString()));
    }
    [Fact]
    public void JsonRoundTripIsExact()
    {
        var census = NoComfort with
        {
            People = [new PawnId("Thing_Human1")],
            Surfaces = [new ComfortSurface("7", [new(3, 4)])],
            Dining = [new DiningSeat("Thing_Chair3", "7", [new PawnId("Thing_Human1")], [])],
            Recreation =
            [
                new RecreationSource("Thing_Horseshoes4", "Gaming_Dexterity", new ComfortHost.Outdoors(), [new PawnId("Thing_Human1")], [new PawnId("Thing_Human1")]),
                new RecreationSource("Thing_Chess5", "Gaming_Cerebral", new ComfortHost.Indoors("7"), [], []),
            ],
            Joy = new(["Gaming_Cerebral", "Gaming_Dexterity"], [new JoyTolerance(new PawnId("Thing_Human1"), [false, true])]),
        };

        var json = OrdersJson.Serialize(census);

        Assert.Equal(
            """{"people":["Thing_Human1"],"surfaces":[{"room":"7","adjacent":[{"x":3,"z":4}]}],"dining":[{"id":"Thing_Chair3","room":"7","accessibleTo":["Thing_Human1"],"users":[]}],"recreation":[{"id":"Thing_Horseshoes4","kind":"Gaming_Dexterity","host":{"type":"outdoors"},"accessibleTo":["Thing_Human1"],"users":["Thing_Human1"]},{"id":"Thing_Chess5","kind":"Gaming_Cerebral","host":{"type":"indoors","room":"7"},"accessibleTo":[],"users":[]}],"joy":{"kinds":["Gaming_Cerebral","Gaming_Dexterity"],"pawns":[{"pawn":"Thing_Human1","bored":[false,true]}]}}""",
            json);
        Assert.Equal(census, OrdersJson.Deserialize<ComfortFacts>(json));
    }
}
