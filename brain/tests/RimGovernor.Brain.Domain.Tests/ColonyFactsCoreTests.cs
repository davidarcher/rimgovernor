using RimGovernor.Brain.Facts;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The colony facts core is total (#3764): every core member of the read and of its projection is <c>required</c>,
/// the complete gear and crop censuses are always present, and the
/// indoor sleeping range (<see cref="SleepingTemperature.Indoor"/> or <see cref="SleepingTemperature.NoIndoorBed"/>).
/// </summary>
public sealed class ColonyFactsCoreTests
{
    /// <summary>A colony facts read with every section present and empty: no indoor bed and nothing pending.</summary>
    internal static ColonyFactsSnapshot Snapshot(RimGovernor.Brain.GameFacts.Common.ObservationContext? context = null) => new()
    {
        Gear = GearFixtures.NoSnapshot,
        Crops = [],
        FoodClimate = new FoodClimate { GrowingYear = [], GrowingDays = 0, GrowingDaysRemaining = 0, SowingNow = false, GrowingDaysUntil = 60, Season = "Winter", DayOfYear = 0, NonGrowingDays = 60 },
        ControlledEnvironment = ControlledEnvironmentTests.NoEnvironment,
        Resources = [],
        Context = context ?? new RimGovernor.Brain.GameFacts.Common.ObservationContext { NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.UnclaimedCase(), Identity = new RimGovernor.Brain.GameFacts.Common.Identity { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 0 },
        ResourceThings = [],
        FoodRunway = new RimGovernor.Brain.GameFacts.Observations.FoodRunway.NoDemand(),
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
        FoodSupply = new() { Consumers = [], Stocks = [], Larder = new() { RawMeatNutrition = 0, CookDemandNutrition = 0, Corpses = [] } },
        Forecast = new() { Needs = NeedsFixtures.Empty, Coupled = CoupledFixtures.Empty, CombinedFoodSupply = new() { Consumers = [], Stocks = [], Larder = new() { RawMeatNutrition = 0, CookDemandNutrition = 0, Corpses = [] } }, Patients = [] },
        Upkeep = UpkeepFactsTests.NoUpkeep,
        Development = new() { Outcome = new() { Networks = [], Geysers = [], ShortCircuit = new ObservedShortCircuit.NoneSeen() } },
        Recovery = new() { Restrictions = [], Buildings = [] },
        Waste = new() { Items = [] },
        Blight = new() { Plants = [] },
        PlayerTechLevel = RimGovernor.Brain.GameFacts.Defs.TechLevel.Neolithic,
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
        ProductionBenches = [],
    };

    /// <summary>A canonical reading of an empty frame with the reviewed inputs its tests use.</summary>
    internal static ColonyReading Reading(ObservationIdentity? identity = null)
    {
        var snapshot = Snapshot(identity?.Context);
        return new()
        {
            Tables = new(default, default, default, []) { Zones = new() { Context = snapshot.Context, Zones = [] } },
            Identity = identity ?? new ObservationIdentity { Context = snapshot.Context },
            Facts = new()
            {
                Census = snapshot,
                Research = ResearchTestFacts.Read(),
                MedicalReserve = new() { Census = snapshot },
                CurrentConstruction = CurrentConstruction.Empty,
                Gear = ColonyModelFixtures.NoGear(),
                Upkeep = new() { Rooms = RoomTestFacts.NoRooms, Routes = new() { PawnIds = [], Facilities = [], Traffic = [], TrafficSamples = 0 } },
                MapBounds = default,
                Books = [], Foods = [], FoodEaters = [],
                FoodSupply = new FoodSupply { Larder = new() { RawMeatNutrition = 0, CookDemandNutrition = 0, Corpses = [] } },
                CombinedFoodSupply = new FoodSupply { Larder = new() { RawMeatNutrition = 0, CookDemandNutrition = 0, Corpses = [] } },
            },
        };
    }
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
        ProgressScore = 0,
        NoTreeConnection = false,
        ThreatScale = 1,
        Biome = "TemperateForest",
        Generator = [],
        GeneratorStartDays = 0,
        BiomeIncidents = [],
        HostileFactions = [],
        Raids = [],
        Disease = new() { Storyteller = "Cassandra", Biome = "TemperateForest", RandomDiseases = false, IntervalFactor = 1, DaysSinceSettle = 0, Candidates = 0, FreeColonists = 0, Incidents = [] },
        Trade = new() { Danger = StoryDangerLevel.None, PopulationIntent = 0, AllyFraction = 0, FullAllyFraction = 0, DaysSinceSettle = 0, CommsConsole = false, TradeBeacon = false, BeaconSilver = 0, Negotiator = false, NegotiatorPriceImprovement = 0, PriceFactorLoss = 0, Incidents = [] },
    };

    [Fact]
    public void EveryColonyReplyRequiresGearEvenWithoutAPlanningRequest()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(OrdersJson.Serialize(Snapshot()))!.AsObject();
        Assert.True(json.Remove("gear"));
        Assert.Throws<System.Text.Json.JsonException>(() => OrdersJson.Deserialize<ColonyFactsSnapshot>(json.ToJsonString()));
    }

    [Fact]
    public void ReviewedCalendarRetainsTheNativeCensusInstance()
    {
        var reading = Reading();
        Assert.Same(reading.Snapshot.FoodClimate, reading.Facts.Calendar);
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
            var json = OrdersJson.Serialize(facts);
            var back = OrdersJson.Deserialize<ColonyFactsSnapshot>(json);
            Assert.Equal(json, OrdersJson.Serialize(back));
            Assert.Equal(facts.SleepingTemperature, back.SleepingTemperature);
        }
    }

    [Theory]
    [InlineData("foodClimate")]
    [InlineData("controlledEnvironment")]
    public void TotalCensusCannotBeOmittedFromJson(string member)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(OrdersJson.Serialize(Snapshot()))!.AsObject();
        Assert.True(node.Remove(member));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => OrdersJson.Deserialize<ColonyFactsSnapshot>(node.ToJsonString()));
    }

    [Fact]
    public void MissingClimateFailsComposition()
    {
        var read = Snapshot() with { FoodClimate = null! };
        Assert.Throws<BridgeContractException>(() => ColonyPlanningReads.Compose(read, new ObservationIdentity { Context = Snapshot().Context }, default!, null));
    }

    [Fact]
    public void MissingEnvironmentFailsCompositionEvenWithoutPlanning()
    {
        var read = Snapshot() with { ControlledEnvironment = null! };
        Assert.Throws<BridgeContractException>(() => ColonyPlanningReads.Compose(read, new ObservationIdentity { Context = Snapshot().Context }, default!, null));
    }

    [Fact]
    public void InvalidClimateCannotBecomeAnUnknownCalendar()
    {
        var climate = Snapshot().FoodClimate with { GrowingDaysRemaining = double.NaN };
        Assert.False(climate.Valid());
        var snapshot = Snapshot() with { FoodClimate = climate };
        Assert.Throws<BridgeContractException>(() => ColonyPlanningReads.Compose(snapshot, new ObservationIdentity { Context = snapshot.Context }, Reading().Tables, null));
    }

    [Fact]
    public void CropSelectionReadsTheCurrentNativeCensusAndDerivedDarkness()
    {
        var census = Snapshot() with
        {
            Biome = "native-biome",
            FoodClimate = Snapshot().FoodClimate with { SowingNow = true, GrowingDaysRemaining = 17 },
        };
        var facts = Reading().Facts with { Census = census, OutdoorsDark = false };
        var climate = facts.CropClimate;
        Assert.Same(census, climate.Census);
        Assert.True(climate.SowingOutdoors());
        Assert.Equal(17, climate.DaysRemaining);
        Assert.Equal(census.Biome, climate.Biome);
        Assert.Same(census.FoodClimate, new FieldRequest { Climate = climate }.Calendar);
        Assert.False((facts with { OutdoorsDark = true }).CropClimate.SowingOutdoors());
        Assert.False((facts with { Census = census with { FoodClimate = census.FoodClimate with { SowingNow = false } } }).CropClimate.SowingOutdoors());
        Assert.Throws<RequiredFactException>(() => new FieldRequest().Climate);
        Assert.Throws<RequiredFactException>(() => new ResourceFieldRequest().Climate);
    }
    [Fact]
    public void MissingZoneCensusFailsInsteadOfCertifyingEmptyFields()
    {
        Assert.Throws<BridgeContractException>(() => ColonyZonesReads.ApplyZones(Reading(), null));
    }

    [Fact]
    public void ZoneSnapshotFillsBothTotalCropArrays()
    {
        var context = Snapshot().Context with { NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.AtCase(7) };
        var expected = new ObservationIdentity { Context = context };
        var read = new ZonesSnapshot
        {
            Context = context,
            Zones = [ZoneFixtures.Growing("field", "Plant_Rice", usableCells: 12, edible: true)],
        };
        var initial = Reading(expected);
        var reading = ColonyZonesReads.ApplyZones(initial with
        {
            Facts = initial.Facts with
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
            },
        }, read);

        Assert.Same(read, Assert.IsType<Held<ZonesSnapshot>>(reading.Zones).Value);
        Assert.Single(reading.Facts.FieldCrops);
        Assert.Equal(12L, Assert.Single(reading.Facts.FieldCapacityCrops).GrowingCells);
    }

    [Fact]
    public void ReadMissingItsCropCensusDoesNotDecode()
    {
        var json = OrdersJson.Serialize(Snapshot());
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        Assert.True(node.Remove("crops"));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => OrdersJson.Deserialize<ColonyFactsSnapshot>(node.ToJsonString()));
    }
}
