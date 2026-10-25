using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The quest worker, shuttle and workload reads are total (#3863): every member <c>required</c>, a colonist that cannot
/// negotiate is <see cref="RimGovernor.Brain.GameFacts.Observations.QuestNegotiation.Incapable"/>, a harvest names no recipe and a recipe without a speed stat
/// names none, and each round-trips through <c>OrdersJsonContext</c> exactly.
/// </summary>
public sealed class QuestWorkerWireTests
{
    [Fact]
    public void WorkerJsonRoundTripIsExact()
    {
        var worker = new RimGovernor.Brain.GameFacts.Observations.QuestWorker
        {
            PawnId = new PawnId("Thing_Human1"),
            HealthyAdult = true,
            CanFight = false,
            Rates = [new RimGovernor.Brain.GameFacts.Observations.QuestWorkRate { Stat = "ConstructionSpeed", Rate = 1.5 }],
            CarryCapacity = 35,
            CarriedMass = 4.5,
            FactionLeader = true,
            Negotiation = new RimGovernor.Brain.GameFacts.Observations.QuestNegotiation.Able(1.2, 8),
        };

        var json = OrdersJson.Serialize(worker);

        Assert.Equal(
            """{"pawnId":"Thing_Human1","healthyAdult":true,"canFight":false,"rates":[{"stat":"ConstructionSpeed","rate":1.5}],"carryCapacity":35,"carriedMass":4.5,"factionLeader":true,"negotiation":{"type":"able","ability":1.2,"social":8}}""",
            json);
        Assert.Equal(worker, OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.QuestWorker>(json));
        var mute = worker with { Negotiation = new RimGovernor.Brain.GameFacts.Observations.QuestNegotiation.Incapable() };
        Assert.Equal(mute, OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.QuestWorker>(OrdersJson.Serialize(mute)));
    }

    [Fact]
    public void ShuttleJsonRoundTripIsExact()
    {
        var shuttle = new RimGovernor.Brain.GameFacts.Observations.QuestShuttleState
        {
            AutoloadAvailable = true,
            Autoload = false,
            Loading = true,
            AllRequiredLoaded = false,
            ManualLaunchAvailable = false,
            LoadedPawnIds = [new PawnId("Thing_Human1")],
            PendingPawnIds = [new PawnId("Thing_Human2")],
        };

        var json = OrdersJson.Serialize(shuttle);

        Assert.Equal(
            """{"autoloadAvailable":true,"autoload":false,"loading":true,"allRequiredLoaded":false,"manualLaunchAvailable":false,"loadedPawnIds":["Thing_Human1"],"pendingPawnIds":["Thing_Human2"]}""",
            json);
        Assert.Equal(shuttle, OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.QuestShuttleState>(json));
    }

    [Fact]
    public void WorkloadJsonRoundTripIsExact()
    {
        var harvest = new RimGovernor.Brain.GameFacts.Observations.QuestWorkload { Recipe = null, Stat = "PlantWorkSpeed", Work = 800, RateFactor = 1 };

        var json = OrdersJson.Serialize(harvest);

        Assert.Equal("""{"recipe":null,"stat":"PlantWorkSpeed","work":800,"rateFactor":1}""", json);
        Assert.Equal(harvest, OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.QuestWorkload>(json));
    }
}
