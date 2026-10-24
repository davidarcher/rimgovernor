using RimGovernor.Contracts.Wire;
using Xunit;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The quest worker, shuttle and workload reads are total (#3863): every member <c>required</c>, a colonist that cannot
/// negotiate is <see cref="O.QuestNegotiation.Incapable"/>, a harvest names no recipe and a recipe without a speed stat
/// names none, and each round-trips through <c>WireJsonContext</c> exactly.
/// </summary>
public sealed class QuestWorkerWireTests
{
    [Fact]
    public void WorkerJsonRoundTripIsExact()
    {
        var worker = new O.QuestWorker
        {
            PawnId = new PawnId("Thing_Human1"),
            HealthyAdult = true,
            CanFight = false,
            Rates = [new O.QuestWorkRate { Stat = "ConstructionSpeed", Rate = 1.5 }],
            CarryCapacity = 35,
            CarriedMass = 4.5,
            FactionLeader = true,
            Negotiation = new O.QuestNegotiation.Able(1.2, 8),
        };

        var json = WireJson.Serialize(worker);

        Assert.Equal(
            """{"pawnId":"Thing_Human1","healthyAdult":true,"canFight":false,"rates":[{"stat":"ConstructionSpeed","rate":1.5}],"carryCapacity":35,"carriedMass":4.5,"factionLeader":true,"negotiation":{"type":"able","ability":1.2,"social":8}}""",
            json);
        Assert.Equal(worker, WireJson.Deserialize<O.QuestWorker>(json));
        var mute = worker with { Negotiation = new O.QuestNegotiation.Incapable() };
        Assert.Equal(mute, WireJson.Deserialize<O.QuestWorker>(WireJson.Serialize(mute)));
    }

    [Fact]
    public void ShuttleJsonRoundTripIsExact()
    {
        var shuttle = new O.QuestShuttleState
        {
            AutoloadAvailable = true,
            Autoload = false,
            Loading = true,
            AllRequiredLoaded = false,
            ManualLaunchAvailable = false,
            LoadedPawnIds = [new PawnId("Thing_Human1")],
            PendingPawnIds = [new PawnId("Thing_Human2")],
        };

        var json = WireJson.Serialize(shuttle);

        Assert.Equal(
            """{"autoloadAvailable":true,"autoload":false,"loading":true,"allRequiredLoaded":false,"manualLaunchAvailable":false,"loadedPawnIds":["Thing_Human1"],"pendingPawnIds":["Thing_Human2"]}""",
            json);
        Assert.Equal(shuttle, WireJson.Deserialize<O.QuestShuttleState>(json));
    }

    [Fact]
    public void WorkloadJsonRoundTripIsExact()
    {
        var harvest = new O.QuestWorkload { Recipe = null, Stat = "PlantWorkSpeed", Work = 800, RateFactor = 1 };

        var json = WireJson.Serialize(harvest);

        Assert.Equal("""{"recipe":null,"stat":"PlantWorkSpeed","work":800,"rateFactor":1}""", json);
        Assert.Equal(harvest, WireJson.Deserialize<O.QuestWorkload>(json));
    }
}
