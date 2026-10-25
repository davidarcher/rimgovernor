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
/// the complete gear and crop censuses are always present, and the
/// indoor sleeping range (<see cref="SleepingTemperature.Indoor"/> or <see cref="SleepingTemperature.NoIndoorBed"/>).
/// </summary>
public sealed class ColonyFactsCoreTests
{
    /// <summary>A colony facts read with every section present and empty: no indoor bed and nothing pending.</summary>
    internal static ColonyFactsSnapshot Snapshot(C.ObservationContext? context = null) => new()
    {
        Gear = GearFixtures.NoSnapshot,
        Crops = [],
        FoodClimate = new FoodClimate { GrowingYear = [], GrowingDays = 0, GrowingDaysRemaining = 0, SowingNow = false, GrowingDaysUntil = 60, Season = "Winter", DayOfYear = 0, NonGrowingDays = 60 },
        ControlledEnvironment = ControlledEnvironmentTests.NoWireEnvironment,
        Resources = [],
        Context = context ?? new C.ObservationContext { NativeGeneration = new RimGovernor.Contracts.Wire.Common.ObservedGeneration.UnclaimedCase(), Identity = new C.Identity { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 0 },
        ResourceThings = [],
        FoodRunway = new RimGovernor.Contracts.Wire.Observations.FoodRunway.NoDemand(),
        WorkResources = [],
        JoinerLetters = [],
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
        FoodSupply = new() { Larder = new() },
        Forecast = new() { Needs = NeedsFixtures.Empty, Coupled = CoupledFixtures.Empty, CombinedFoodSupply = new() { Larder = new() }, Patients = [] },
        Upkeep = UpkeepFactsTests.NoUpkeep,
        Development = new() { Outcome = new() { ShortCircuit = new ObservedShortCircuit.NoneSeen() } },
        Recovery = new() { Restrictions = [], Buildings = [] },
        Waste = new() { Items = [] },
        Blight = new() { Plants = [] },
        PlayerTechLevel = Defs.TechLevel.Neolithic,
        Threat = new() { Observed = QuietThreat },
        EventLoot = new(),
        FoodChannels = new() { FishableWater = null, Gatherable = [], EggLayer = [], PasteDispenser = [], Forage = [], Grazing = [], Slaughter = [], PastureTerrain = [] },
        DeepResources = DeepResourcesTests.NoDeepResources,
        FermentingBarrels = 0,
        Policies = PoliciesTests.NoPolicies,
        Biotech = BiotechSectionTests.NoBiotech,
        Anomaly = AnomalySectionTests.NoAnomaly,
        Royalty = RoyaltySectionTests.NoRoyalty,
        DeliveryLedger = DeliveryLedgerTests.NoDeliveries,
        HuntCensus = new() { Hunters = [] },
        Acquisition = [],
        Cooking = [],
        Butchering = [],
    };

    /// <summary>A projection with its total core set: the given identity, an empty map, no workers, no deliveries and nothing pending.</summary>
    internal static ColonyProjection Projection(Identity? identity = null) => new()
    {
        Rooms = RoomTestFacts.NoRooms,
        Identity = identity ?? new Identity(),
        Facts = new() { CurrentConstruction = CurrentConstruction.Empty, Gear = ColonyModelFixtures.NoGear() },
        Bounds = default,
        PlayerTechLevel = Defs.TechLevel.Neolithic,
        Workers = 0,
        Threat = QuietThreat,
        DeliveryLedger = DeliveryLedgerTests.NoDeliveries,
        PendingFoodNutrition = 0,
        ProductionBenches = [],
        CookingBenches = [],
        ButcheringBenches = [],
        FoodSupply = new FoodSupply { Larder = new() },
        CombinedFoodSupply = new FoodSupply { Larder = new() },
        PendingHunts = 0,
        DeepResources = DeepResourcesTests.NoDeepResources,
        Policies = PoliciesTests.NoPolicies,
        Books = [],
        Foods = [],
        FoodEaters = [],
        RoyaltySection = RoyaltySectionTests.NoRoyalty,
        BiotechSection = BiotechSectionTests.NoBiotech,
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
    public void EveryColonyReplyRequiresGearEvenWithoutAPlanningRequest()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(WireJson.Serialize(Snapshot()))!.AsObject();
        Assert.True(json.Remove("gear"));
        Assert.Throws<System.Text.Json.JsonException>(() => WireJson.Deserialize<ColonyFactsSnapshot>(json.ToJsonString()));
    }

    [Fact]
    public void HardenedReadRoundTripsExactly()
    {
        foreach (var facts in new[]
        {
            Snapshot(),
            Snapshot() with
            {
                Crops = [new EdibleCrop { DefName = "Plant_Rice", NutritionDemandPerDay = 2.5, DietAllowed = true }],
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
            Assert.Equal(facts.SleepingTemperature, back.SleepingTemperature);
        }
    }

    [Theory]
    [InlineData("foodClimate")]
    [InlineData("controlledEnvironment")]
    public void TotalCensusCannotBeOmittedFromJson(string member)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(WireJson.Serialize(Snapshot()))!.AsObject();
        Assert.True(node.Remove(member));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => WireJson.Deserialize<ColonyFactsSnapshot>(node.ToJsonString()));
    }

    [Fact]
    public void MissingClimateFailsTheDecoder()
    {
        var read = Snapshot() with { FoodClimate = null! };
        Assert.Throws<BridgeContractException>(() => ColonyDecodeReads.DecodeColony(read, new Identity(), default!, null));
    }

    [Fact]
    public void MissingEnvironmentFailsTheDecoderEvenWithoutPlanning()
    {
        var read = Snapshot() with { ControlledEnvironment = null! };
        Assert.Throws<BridgeContractException>(() => ColonyDecodeReads.DecodeColony(read, new Identity(), default!, null));
    }

    [Fact]
    public void InvalidClimateCannotBecomeAnUnknownCalendar()
    {
        var climate = Snapshot().FoodClimate with { GrowingDaysRemaining = double.NaN };
        Assert.Throws<BridgeContractException>(() => ColonyProductionReads.ColonyCalendar(climate));
    }

    [Fact]
    public void MissingZoneCensusFailsInsteadOfCertifyingEmptyFields()
    {
        Assert.Throws<BridgeContractException>(() => ZonesReads.ApplyZones(Projection(), null));
    }

    [Fact]
    public void ZoneSnapshotFillsBothTotalCropArrays()
    {
        var context = Snapshot().Context with { NativeGeneration = new C.ObservedGeneration.AtCase(7) };
        var expected = DecodeReads.ContextIdentity(context);
        var read = new ZonesSnapshot
        {
            Context = context,
            Zones = [ZoneFixtures.Growing("field", "Plant_Rice", usableCells: 12, edible: true)],
        };
        var projection = ZonesReads.ApplyZones(Projection(expected) with
        {
            Definitions =
            [
                new PlanningDefinition
                {
                    Name = "Plant_Rice", Available = true,
                    Plant = new PlanningPlant { GrowDays = 6, MinGrowthTemperature = 0, MinOptimalGrowthTemperature = 10, MaxOptimalGrowthTemperature = 42, MaxGrowthTemperature = 58 },
                    Harvest = new PlanningHarvest { HarvestedThingDef = "RawRice", HarvestYield = 6, HarvestNutrition = .3, Edible = true },
                    Diet = new PlanningDiet { NutritionDemandPerDay = 1.6, DietAllowed = true },
                },
            ],
        }, read);

        Assert.Same(read, projection.Zones!.Value);
        Assert.Single(projection.FieldCrops);
        Assert.Equal(12L, Assert.Single(projection.FieldCapacityCrops).GrowingCells);
    }

    [Fact]
    public void ReadMissingItsCropCensusDoesNotDecode()
    {
        var json = WireJson.Serialize(Snapshot());
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        Assert.True(node.Remove("crops"));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => WireJson.Deserialize<ColonyFactsSnapshot>(node.ToJsonString()));
    }
}
