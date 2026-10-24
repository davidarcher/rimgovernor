using RimGovernor.Contracts.Wire;
using Xunit;
using C = RimGovernor.Contracts.Wire.Common;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

// #3866: the caravan, world map and assembly rows are plain required records; a caravan's course is Moving or
// Stopped and a home route is Reachable with its ticks or Unreachable. Each round-trips exactly through the wire JSON.
public sealed class WorldProgressionWireTests
{
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

    [Fact]
    public void ThePeaceTalksRangeRoundTripsExactly() =>
        RoundTrips(new O.QuestPeaceTalks { WorstGoodwillLoss = 40, BestGoodwillGain = 25, IdeologyActive = true });
}
