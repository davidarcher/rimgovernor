using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The comfort census is one total facts record (#3771): every member is set, a recreation source's room a named
/// <see cref="ComfortHost"/> and the joy boredom matrix always read. On the wire it round-trips through
/// <c>WireJsonContext</c> exactly.
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
        Assert.Equal("comfort census", Assert.Throws<RequiredFactException>(() => new RoundsFacts().RequireComfort()).Fact);
        Assert.Equal("basic comfort census", Assert.Throws<RequiredFactException>(() => new RoundsFacts().RequireBasicComfort()).Fact);
    }

    [Fact]
    public void BasicComfortRetainsFacilitiesTheHostingRoomFilterExcludes()
    {
        PawnId pawn = new("pawn");
        var basic = NoComfortObservation with { Census = NoComfort with
        {
            People = [pawn],
            Surfaces = [new("other-room", [])],
            Dining = [new("chair", "other-room", [pawn], [])],
            Recreation = [new("horseshoes", "gaming", new ComfortHost.Outdoors(), [pawn], [])],
            Joy = new(["gaming"], [new(pawn, [false])]),
        } };
        var hosted = ComfortRules.HostedComfort(basic, RoomObservation.NoRooms);

        Assert.True(BasicComfortRules.ReviewBasicComfort(basic).Recovered());
        Assert.False(ComfortRules.ReviewComfort(hosted, default, new(0)).Recovered());
        Assert.Single(basic.Census.Dining);
        Assert.Single(basic.Census.Recreation);
        Assert.Empty(hosted.Census.Dining);
        Assert.Empty(hosted.Census.Recreation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnedAndDetailedReadsBothFilterHostedFacilities(bool detailedRooms)
    {
        PawnId pawn = new("pawn");
        var basic = NoComfortObservation with { Census = NoComfort with
        {
            People = [pawn],
            Dining = [new("chair", "other-room", [pawn], [])],
            Recreation = [new("horseshoes", "gaming", new ComfortHost.Outdoors(), [pawn], [])],
        } };
        var projection = ColonyFactsCoreTests.Projection() with { Facts = new() { Comfort = basic, BasicComfort = basic } };
        var frame = new RoundsFrame { Rooms = new() { Context = ColonyFactsCoreTests.Snapshot().Context!, Rooms = [] } };

        var read = RoundsReads.WithFrameRooms(projection, frame, new DiningFurniture(), detailedRooms);

        Assert.Same(basic, read.Facts.RequireBasicComfort());
        Assert.Empty(read.Facts.RequireComfort().Census.Dining);
        Assert.Empty(read.Facts.RequireComfort().Census.Recreation);
        Assert.Equal(detailedRooms, read.Rooms.IsKnown);
    }

    [Fact]
    public void MissingFrameRoomsCannotCertifyHostedComfort()
    {
        var projection = ColonyFactsCoreTests.Projection() with { Facts = new() { Comfort = NoComfortObservation, BasicComfort = NoComfortObservation } };
        Assert.Throws<ObservationContractException>(() => RoundsReads.WithFrameRooms(projection, new RoundsFrame(), new DiningFurniture(), false));
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
        var json = System.Text.Json.Nodes.JsonNode.Parse(WireJson.Serialize(ColonyFactsCoreTests.Snapshot()))!.AsObject();
        var upkeep = json["upkeep"]!.AsObject();
        var parent = member == "comfort" ? upkeep : upkeep["comfort"]!.AsObject();
        Assert.True(parent.Remove(member));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => WireJson.Deserialize<ColonyFactsSnapshot>(json.ToJsonString()));
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

        var json = WireJson.Serialize(census);

        Assert.Equal(
            """{"people":["Thing_Human1"],"surfaces":[{"room":"7","adjacent":[{"x":3,"z":4}]}],"dining":[{"id":"Thing_Chair3","room":"7","accessibleTo":["Thing_Human1"],"users":[]}],"recreation":[{"id":"Thing_Horseshoes4","kind":"Gaming_Dexterity","host":{"type":"outdoors"},"accessibleTo":["Thing_Human1"],"users":["Thing_Human1"]},{"id":"Thing_Chess5","kind":"Gaming_Cerebral","host":{"type":"indoors","room":"7"},"accessibleTo":[],"users":[]}],"joy":{"kinds":["Gaming_Cerebral","Gaming_Dexterity"],"pawns":[{"pawn":"Thing_Human1","bored":[false,true]}]}}""",
            json);
        Assert.Equal(census, WireJson.Deserialize<ComfortFacts>(json));
    }
}
