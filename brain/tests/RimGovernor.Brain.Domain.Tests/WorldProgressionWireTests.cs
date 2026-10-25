using RimGovernor.Contracts.Wire;
using Xunit;
using C = RimGovernor.Contracts.Wire.Common;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

// #3866: the caravan, world map and assembly rows are plain required records; a caravan's course is Moving or
// Stopped and a home route is Reachable with its ticks or Unreachable. Each round-trips exactly through the wire JSON.
public sealed class WorldProgressionWireTests
{
    [Fact]
    public void ExpeditionTripsRetainNativeCaravansAndOnlyTheHomeAssembly()
    {
        var away = new O.CaravanAssembly { MapId = 2, Pawns = [new PawnId("away")] };
        var trips = RimGovernor.Brain.Bridge.QuestReads.FrameExpeditionTrips(Snapshot with { Assemblies = [Snapshot.Assemblies[0], away] }, new(0));
        Assert.Equal(3, trips.Count);
        var caravan = Assert.IsType<RimGovernor.Brain.Policy.ExpeditionTrip.OnWorld>(trips[0]);
        Assert.Same(Snapshot.Caravans[0], caravan.Observed);
        Assert.Same(Snapshot.Caravans[0].Course, caravan.Course);
        Assert.Same(Snapshot.Caravans[0].HomeRoutes[0], caravan.HomeRoutes[0]);
        Assert.Equal(new PawnId("Human1"), Assert.Single(caravan.PawnIds));
        Assert.Equal(91, (caravan with { Observed = caravan.Observed with { Tile = 91 } }).Tile);
        var forming = Assert.IsType<RimGovernor.Brain.Policy.ExpeditionTrip.Forming>(trips[2]);
        Assert.Same(Snapshot.Assemblies[0], forming.Observed);
        Assert.Equal(new PawnId("Human3"), Assert.Single(forming.PawnIds));
    }

    private static readonly C.ObservationContext Context = new() { NativeGeneration = new RimGovernor.Contracts.Wire.Common.ObservedGeneration.UnclaimedCase(), Identity = new C.Identity { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 10 };

    private static O.CaravanState Caravan(string id, O.CaravanCourse course) => new()
    {
        Caravan = new O.EntityRef { Id = id, DefName = "Caravan", Label = "Caravan " + id, Place = new O.EntityPlace.Unplaced() },
        Tile = 42,
        Course = course,
        MassUsage = 84.5,
        MassCapacity = 210,
        FoodDays = 6.25,
        FoodRotDays = 600,
        Pawns = [PawnFixtures.Row("Human1")],
        HomeRoutes =
        [
            new O.WorldRoute { Destination = 7, Route = new O.PackRoute.Reachable(90000) },
            new O.WorldRoute { Destination = 9, Route = new O.PackRoute.Unreachable() },
        ],
        Inventory = [new O.Quantity { DefName = "Silver", Units = 120 }],
    };

    private static readonly O.WorldProgressionSnapshot Snapshot = new()
    {
        Context = Context,
        Maps = [new O.WorldMap { Id = 0, Tile = 7, Home = true, Pawns = [PawnFixtures.Row("Human2")], QuestWorkers = [] }],
        Caravans = [Caravan("Caravan_1", new O.CaravanCourse.Moving(7)), Caravan("Caravan_2", new O.CaravanCourse.Stopped())],
        Assemblies = [new O.CaravanAssembly { MapId = 0, Pawns = [new PawnId("Human3")] }],
        Quests = [],
        Sites = [],
    };

    private static void RoundTrips<T>(T payload)
    {
        var json = WireJson.Serialize(payload);
        var back = WireJson.Deserialize<T>(json);

        Assert.Equal(json, WireJson.Serialize(back));
        Assert.Equal(payload, back);
    }

    [Fact]
    public void TheWorldCensusWithEveryCourseAndRouteStateRoundTripsExactly()
    {
        RoundTrips(Snapshot);
        var json = WireJson.Serialize(Snapshot);
        Assert.Contains("\"type\":\"moving\"", json);
        Assert.Contains("\"type\":\"stopped\"", json);
        Assert.Contains("\"type\":\"reachable\"", json);
        Assert.Contains("\"type\":\"unreachable\"", json);
    }

    [Theory]
    [InlineData("context")]
    [InlineData("maps")]
    [InlineData("caravans")]
    [InlineData("assemblies")]
    [InlineData("quests")]
    [InlineData("sites")]
    public void MissingWorldCensusSectionsCannotBecomeEmptyCollections(string member)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(WireJson.Serialize(Snapshot))!.AsObject();
        Assert.True(node.Remove(member));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => WireJson.Deserialize<O.WorldProgressionSnapshot>(node.ToJsonString()));
    }
    [Fact]
    public void ThePeaceTalksRangeRoundTripsExactly() =>
        RoundTrips(new O.QuestPeaceTalks { WorstGoodwillLoss = 40, BestGoodwillGain = 25, IdeologyActive = true });
}
