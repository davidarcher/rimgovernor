using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using C = RimGovernor.Contracts.Wire.Common;
using Defs = RimGovernor.Contracts.Wire.Defs;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The colony facts core is total (#3764): every core member of the read and of its projection is <c>required</c>,
/// planning is a named state (<see cref="Planning.Requested"/> or <see cref="Planning.NotRequested"/>) and so is the
/// indoor sleeping range (<see cref="SleepingTemperature.Indoor"/> or <see cref="SleepingTemperature.NoIndoorBed"/>).
/// A planner that needs planning facts from a read that did not request them raises <c>RequiredFactException("planning")</c>.
/// </summary>
public sealed class ColonyFactsCoreTests
{
    /// <summary>A colony facts read with every section present and empty: no planning, no indoor bed, nothing pending.</summary>
    internal static ColonyFactsSnapshot Snapshot(C.ObservationContext? context = null) => new()
    {
        Context = context ?? new C.ObservationContext { Identity = new C.Identity { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 0 },
        ResourceThings = [],
        WorkResources = [],
        ColonistCount = 0,
        WorkerCount = 0,
        MapSize = new MapSize { Width = 0, Height = 0 },
        Biome = "TemperateForest",
        PendingFoodNutrition = 0,
        PendingHunts = 0,
        BedCapacity = 0,
        IndoorSleepingCapacity = 0,
        SleepingTemperature = new SleepingTemperature.NoIndoorBed(),
        OutdoorTemperatureC = 0,
        FoodSupply = new(),
        Forecast = new(),
        Upkeep = UpkeepFactsTests.NoUpkeep,
        Development = new(),
        Planning = new Planning.NotRequested(),
        Recovery = new(),
        Waste = new(),
        PlayerTechLevel = Defs.TechLevel.Neolithic,
        Threat = new() { Observed = QuietThreat },
        EventLoot = new(),
        FoodChannels = new(),
        DeepResources = DeepResourcesTests.NoDeepResources,
        FermentingBarrels = 0,
        Policies = PoliciesTests.NoPolicies,
        Biotech = null,
        Anomaly = null,
        Royalty = RoyaltySectionTests.NoRoyalty,
        DeliveryLedger = DeliveryLedgerTests.NoDeliveries,
        HuntCensus = new() { Hunters = [] },
        Acquisition = [],
    };

    /// <summary>A projection with its total core set: the given identity, an empty map, no workers, no deliveries and nothing pending.</summary>
    internal static ColonyProjection Projection(Identity? identity = null) => new()
    {
        Identity = identity ?? new Identity(),
        Bounds = default,
        PlayerTechLevel = Defs.TechLevel.Neolithic,
        Workers = 0,
        Threat = QuietThreat,
        DeliveryLedger = DeliveryLedgerTests.NoDeliveries,
        PendingFoodNutrition = 0,
        PendingHunts = 0,
        DeepResources = DeepResourcesTests.NoDeepResources,
        Policies = PoliciesTests.NoPolicies,
        Books = [],
        Foods = [],
        FoodEaters = [],
        RoyaltySection = RoyaltySectionTests.NoRoyalty,
    };

    /// <summary>A threat read of a colony with no wealth, no raid points, no hostile faction and no raid on the map.</summary>
    internal static ThreatFacts QuietThreat { get; } = new()
    {
        WealthItems = 0,
        WealthBuildings = 0,
        WealthPawns = 0,
        WealthTotal = 0,
        RaidPoints = 0,
        Storyteller = "Cassandra",
        DaysPassed = 0,
        HostileFactions = [],
        Raids = [],
    };

    [Fact]
    public void ReadWithoutPlanningFailsPlannerThatNeedsIt()
    {
        var facts = Snapshot();
        Assert.IsType<Planning.NotRequested>(facts.Planning);
        var refused = Assert.Throws<RequiredFactException>(() => PlanningWindowReads.RequirePlanning(facts));
        Assert.Equal("planning", refused.Fact);
    }

    [Fact]
    public void PlanningReadHandsPlannerItsFacts()
    {
        var planning = new PlanningFacts { Gear = GearFixtures.NoSnapshot, Crops = [new EdibleCrop { DefName = "Plant_Rice" }] };
        var facts = Snapshot() with { Planning = new Planning.Requested(planning) };
        Assert.Same(planning, PlanningWindowReads.RequirePlanning(facts));
    }

    [Fact]
    public void HardenedReadRoundTripsExactly()
    {
        foreach (var facts in new[]
        {
            Snapshot(),
            Snapshot() with
            {
                Planning = new Planning.Requested(new PlanningFacts { Gear = GearFixtures.NoSnapshot, Crops = [new EdibleCrop { DefName = "Plant_Rice" }] }),
                SleepingTemperature = new SleepingTemperature.Indoor(12.5, 21),
                PendingFoodNutrition = 3.5,
                PendingHunts = 2,
                MapSize = new MapSize { Width = 250, Height = 200 },
                WorkResources = [new WorkResourcePawn("pawn", "", 0, "", "", "", 0) { LocalHour = 6.5 }],
            },
        })
        {
            var json = WireJson.Serialize(facts);
            var back = WireJson.Deserialize<ColonyFactsSnapshot>(json);
            Assert.Equal(json, WireJson.Serialize(back));
            Assert.Equal(facts.Planning.GetType(), back.Planning.GetType());
            Assert.Equal(facts.SleepingTemperature, back.SleepingTemperature);
        }
    }

    [Fact]
    public void ReadMissingItsPlanningStateDoesNotDecode()
    {
        var json = WireJson.Serialize(Snapshot());
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        var planning = node.Select(p => p.Key).Single(k => k.Equals("planning", StringComparison.OrdinalIgnoreCase));
        node.Remove(planning);
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => WireJson.Deserialize<ColonyFactsSnapshot>(node.ToJsonString()));
    }
}
