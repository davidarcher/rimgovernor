using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// Task 6.4 (RG-HABITAT-008): a short power network compares configuring, routing, generating, storing, sheltering and passive
// thermal provision against deferring, each priced by its consumers' interruption over the horizon.
public sealed class PowerUtilityTests
{
    private const string Wood = "WoodFiredGenerator";
    private const string Solar = "SolarGenerator";
    private static readonly string Conduit = Wire.Name(PowerMethod.Connect);
    private static readonly Tick Now = new(600_000);

    private static readonly PowerPlanning Base = new()
    {
        Battery = new PowerBattery(600, 0.5),
        Sources = new Dictionary<string, PowerSourceProfile> { [Wood] = PowerRules.ConstantPowerProfile, [Solar] = PowerRules.SolarPowerProfile },
        Light = new LightTerrains(new Dictionary<string, bool> { ["Soil"] = true }),
        DaylightHours = 12,
        Now = Now,
    };

    private static GeneratorOption WoodOption(long stock = 500, bool renewable = true) => new()
    {
        Definition = Wood, Available = true, Profile = PowerRules.ConstantPowerProfile, NominalW = 1000,
        Fuel = new Resource("WoodLog"), FuelStock = stock, FuelRenewable = renewable, FuelPerDay = 20,
    };

    private static GeneratorOption SolarOption() => new() { Definition = Solar, Available = true, Profile = PowerRules.SolarPowerProfile, NominalW = 1700 };

    private static ProvisionOption Piece(string name, double cost, double lead) => new(name, false, lead, cost);

    private static PowerValuation Valuation(IEnumerable<(string Id, UtilityService Service, double Loss)> consumers, params ProvisionOption[] pieces) => new()
    {
        Consumers = consumers.ToDictionary(c => c.Id, c => new ConsumerValue(c.Service, new InterruptionLoss.PerDay(c.Loss)), StringComparer.Ordinal),
        Pieces = pieces.ToDictionary(p => p.Kind, p => p, StringComparer.Ordinal),
    };

    private static PowerSite Site(string id, string def, Cell cell, double watts, string network = "net") =>
        PowerBuildingFixtures.Trader(id, def, cell, watts, watts > 0 ? watts : 0, true, network);

    private static SiteCell[] Row(int from, int to, int z = 2, string? roof = null) =>
        [.. Enumerable.Range(from, to - from + 1).Select(x => SiteCellFixtures.At(new Cell(x, z), roof: roof))];

    private static double Days(PricedUtility p) => Assert.IsType<UtilityPrice.Days>(p.Price).Value;

    private static PowerProposal Select(PowerTopology v, IReadOnlyList<SiteCell> cells, PowerPlanning planning) =>
        PowerRules.SelectPowerMethod(v, new Bounds(20, 20), cells, [], planning);

    // (a) A freezer holding 2000 nutrition of perishable stock and an idle art bench overdraw a 500 W generator: switching the
    // idle bench off loses nothing, so it beats building a generator.
    [Fact]
    public void SwitchingAnIdleBenchOffBeatsANewGeneratorWhenAFreezerIsSpoiling()
    {
        var stock = new FoodStock
        {
            Id = "potatoes", DefName = "RawPotatoes", Nutrition = 2000, Count = 40_000, Rot = new FoodRot.Rots(2L * GameTime.TicksPerDay),
            Place = new FoodPlace.Stands(true, "freezer-room"), TemperatureC = -5, RotRate = 0, Form = new FoodForm.Item(),
            IsHumanMeat = false, RawMeat = false, IsHumanlike = false, Vegetable = true, RawClass = FoodIngredientClass.None,
            Holder = new PawnId(string.Empty), Eaters = [], UnitMass = 0.03, Forbidden = false,
        };
        var spoiling = ForecastFreezer.SpoilageAvoided(stock, 1, 0, new Dictionary<Resource, ConstructionMaterialValue>
        {
            [new Resource("RawPotatoes")] = new(MaterialValueBasis.Labor, 0.001),
        });
        Assert.True(spoiling > 0);
        var v = new PowerTopology { Buildings = [Site("gen", Wood, new(2, 2), 500), Site("freezer", "Cooler", new(4, 2), -400), Site("art", "TableSculpting", new(6, 2), -300)] };
        var planning = Base with
        {
            Generators = [WoodOption()],
            Valuation = Valuation([("freezer", UtilityService.Refrigeration, spoiling), ("art", UtilityService.Workshop, 0)], Piece(Wood, 5, 1)),
        };

        var proposal = Select(v, Row(0, 8), planning);

        Assert.Equal(PowerMethod.Configure, proposal.Method);
        Assert.Equal(new PowerConfiguration(["art"], []), proposal.Configuration);
        var generate = Assert.Single(proposal.Decision!.Alternatives, a => a.Alternative.Kind == UtilityChoiceKind.Generate);
        Assert.True(Days(generate) > Days(proposal.Decision.Chosen));
    }

    // (b) Solar beats a wood generator whose finite stock lasts a day where open sky stands; under a rock roof solar is excluded
    // and wood wins. No generator ranking remains.
    [Fact]
    public void SolarWinsUnderOpenSkyAndWoodWinsInACave()
    {
        var v = new PowerTopology { Buildings = [Site("stove", "ElectricStove", new(2, 2), -300)] };
        var planning = Base with
        {
            Generators = [WoodOption(stock: 20, renewable: false), SolarOption()],
            Valuation = Valuation([("stove", UtilityService.Workshop, 10)], Piece(Wood, 3, 1), Piece(Solar, 6, 1)),
        };

        var open = Select(v, Row(0, 6), planning);
        Assert.Equal((PowerMethod.Generate, Solar), (open.Method, open.Definition));

        var cave = Select(v, Row(0, 6, roof: "RoofRockThick"), planning);
        Assert.Equal((PowerMethod.Generate, Wood), (cave.Method, cave.Definition));
        Assert.Equal(new UtilityStanding.Excluded("no_open_sky"), cave.Decision!.Alternatives.Single(a => a.Alternative.Subject == Solar).Alternative.Standing);

        var policy = typeof(PowerRules).Assembly;
        Assert.Null(policy.GetType("RimGovernor.Brain.Policy.GeneratorRanking"));
        Assert.Null(policy.GetType("RimGovernor.Brain.Policy.RankedGenerator"));
        Assert.Null(typeof(PowerRules).GetMethod("RankGenerators"));
        Assert.Null(typeof(PowerRules).GetMethod("GeneratorYield"));
        Assert.Null(typeof(GeneratorOption).GetProperty("BuildValue"));
    }

    // (c) A rain-exposed bank: roofing it clears the short-circuit outage and beats a second battery while the rate is read;
    // with the rate unread every alternative that leaves the exposure is unpriced, naming it, and the network defers.
    [Fact]
    public void RoofingAnExposedBankBeatsASecondBatteryOnlyWhileTheRateIsRead()
    {
        UtilityAlternative[] alternatives =
        [
            new(UtilityChoiceKind.Shelter, "battery", new UtilityStanding.Assessed(new UtilityEffect { CostDays = 2, LeadDays = 0.5, ClearsExposure = true })),
            new(UtilityChoiceKind.Store, PowerRules.BatteryDefinition, new UtilityStanding.Assessed(new UtilityEffect { StorageWD = 600, CostDays = 4, LeadDays = 1 })),
        ];
        var n = new UtilityNetwork
        {
            Network = "net", Producers = [new PowerProducer { NominalW = 1000, Profile = PowerRules.ConstantPowerProfile }], StorageWD = 600,
            Consumers = [new("freezer", 400, UtilityService.Refrigeration, new InterruptionLoss.PerDay(20))],
            ChargeEfficiency = 0.5, Exposure = new UtilityExposure.Expected(0.1, 1),
        };

        var read = UtilityChoiceRules.Choose(n, alternatives);
        Assert.Equal(UtilityChoiceKind.Shelter, read.Kind);
        Assert.True(Days(read.Alternatives.Single(a => a.Alternative.Kind == UtilityChoiceKind.Store)) > Days(read.Chosen));

        var unread = UtilityChoiceRules.Choose(n with { Exposure = new UtilityExposure.Unassessed("rain short-circuit rate") }, alternatives);
        Assert.Equal(UtilityChoiceKind.Defer, unread.Kind);
        Assert.Equal("rain short-circuit rate", unread.Reason);
        var battery = unread.Alternatives.Single(a => a.Alternative.Kind == UtilityChoiceKind.Store);
        Assert.Equal(new UtilityPrice.Unpriced("rain short-circuit rate"), battery.Price);
    }

    // (d) A passive cooler whose steady temperature holds the freezer band frees the powered cooler's draw on a short network,
    // beating a generator and switching a bench off; out of band it is excluded.
    [Fact]
    public void APassiveCoolerInBandBeatsThePoweredOneWhenPowerIsShort()
    {
        var v = new PowerTopology { Buildings = [Site("gen", Wood, new(2, 2), 500), Site("cooler", "Cooler", new(4, 2), -400), Site("bench", "TableSculpting", new(6, 2), -300)] };
        var planning = Base with
        {
            Generators = [WoodOption()],
            Valuation = Valuation([("cooler", UtilityService.Refrigeration, 20), ("bench", UtilityService.Workshop, 5)], Piece(Wood, 10, 1)) with
            {
                Passive = [new PassiveProvision("cooler", new PassiveSteady.Read(-2), -10, 0, 1, 0.5) { Definition = "PassiveCooler" }],
            },
        };

        var passive = Select(v, Row(0, 8), planning);
        Assert.Equal((PowerMethod.Passive, "PassiveCooler"), (passive.Method, passive.Definition));

        var warm = Select(v, Row(0, 8), planning with { Valuation = planning.Valuation with { Passive = [new PassiveProvision("cooler", new PassiveSteady.Read(8), -10, 0, 1, 0.5) { Definition = "PassiveCooler" }] } });
        Assert.NotEqual(PowerMethod.Passive, warm.Method);
        Assert.Equal(new UtilityStanding.Excluded("passive_out_of_band"), warm.Decision!.Alternatives.Single(a => a.Alternative.Kind == UtilityChoiceKind.Passive).Alternative.Standing);
    }

    // (e) A dated chain step's draw with a priced loss is prepared with the generator's lead and joins the demand, so the network
    // generates before it stands; with its loss unassessed the prospective comparison defers and the network needs nothing.
    [Fact]
    public void ADatedChainDrawPreparesGenerationWithLeadAndDefersWhenItsLossIsUnassessed()
    {
        var v = new PowerTopology { Buildings = [Site("gen", Wood, new(2, 2), 500), Site("bench", "TableSculpting", new(4, 2), -300)] };
        var change = new ExpectedChange("ElectricTailoringBench", ExpectedOwner.Power, 400, new ExpectedTiming.Dated(Now.Value + (GameTime.TicksPerDay / 2)),
            new ExpectedStay.Permanent(), new ExpectedLoss.PerDay(3));
        var planning = Base with
        {
            Generators = [WoodOption()],
            Valuation = Valuation([("bench", UtilityService.Workshop, 5)], Piece(Wood, 5, 1)),
            Expected = [change],
        };

        var prepared = Select(v, Row(0, 6), planning);
        Assert.Equal((PowerMethod.Generate, Wood), (prepared.Method, prepared.Definition));
        Assert.Contains(prepared.Decision!.Alternatives, a => a.Alternative.Kind == UtilityChoiceKind.Defer && Days(a) > Days(prepared.Decision.Chosen));

        var unassessed = change with { Loss = new ExpectedLoss.Unassessed("unpowered ElectricTailoringBench loss") };
        var choice = ProspectiveCapacityRules.Choose(unassessed with { Units = 1 }, Now, [Piece(Wood, 5, 1)]);
        Assert.Equal(new ProspectiveChoice.Defer("unpowered ElectricTailoringBench loss", 0), choice);
        Assert.Equal(PowerMethod.NoMethod, Select(v, Row(0, 6), planning with { Expected = [unassessed] }).Method);
    }

    // (f) No reserve of days or storage margin: a solar network short only at night stores when the night's priced interruption
    // outweighs the battery, and defers when the consumer loses nothing unpowered.
    [Fact]
    public void StorageIsSizedByThePricedInterruption()
    {
        Assert.Null(typeof(PowerPlanning).GetProperty("ReserveMinDays"));
        Assert.Null(typeof(PowerPlanning).GetProperty("StorageMargin"));
        Assert.Null(typeof(PowerBudgetInput).GetProperty("StorageMargin"));
        Assert.Null(typeof(PowerRules).GetField("DefaultStorageMargin"));
        Assert.Null(typeof(PowerRules).GetField("GeothermalReachCells"));

        var v = new PowerTopology { Buildings = [Site("sun", Solar, new(2, 2), 1000), Site("stove", "ElectricStove", new(4, 2), -300)] };
        var planning = Base with
        {
            Generators = [WoodOption()], BatteryAvailable = true,
            Valuation = Valuation([("stove", UtilityService.Workshop, 10)], Piece(Wood, 10, 1), Piece(PowerRules.BatteryDefinition, 2, 0.5)),
        };

        Assert.Equal(PowerMethod.Store, Select(v, Row(0, 6), planning).Method);

        var idle = Select(v, Row(0, 6), planning with
        {
            Valuation = planning.Valuation with { Consumers = new Dictionary<string, ConsumerValue> { ["stove"] = new(UtilityService.Workshop, new InterruptionLoss.PerDay(0)) } },
        });
        Assert.Equal(PowerMethod.Defer, idle.Method);
        Assert.Equal(UtilityChoiceKind.Defer, idle.Decision!.Kind);
    }

    // (g) A conduit to a network with surplus beats a new generator when its cost and build delay are lower.
    [Fact]
    public void RoutingToASurplusNetworkBeatsANewGenerator()
    {
        var v = new PowerTopology
        {
            Buildings = [Site("small", Wood, new(1, 2), 100, "a"), Site("stove", "ElectricStove", new(2, 2), -400, "a"), Site("big", Wood, new(6, 2), 1000, "b")],
            Networks =
            [
                new PowerNetworkFact { Id = "a", GenerationW = 100, ConsumptionW = 400, StoredWD = 0 },
                new PowerNetworkFact { Id = "b", GenerationW = 1000, ConsumptionW = 0, StoredWD = 0 },
            ],
        };
        var planning = Base with
        {
            Generators = [WoodOption()],
            Valuation = Valuation([("stove", UtilityService.Workshop, 10)], Piece(Wood, 5, 1), Piece(Conduit, 0.1, 0.05)),
        };

        var proposal = Select(v, Row(0, 8), planning);

        Assert.Equal(PowerMethod.Connect, proposal.Method);
        Assert.Equal(UtilityChoiceKind.Route, proposal.Decision!.Kind);
        Assert.Equal([new Cell(5, 2), new Cell(4, 2), new Cell(3, 2), new Cell(2, 2)], proposal.Cells);
        Assert.True(Days(proposal.Decision.Alternatives.Single(a => a.Alternative.Kind == UtilityChoiceKind.Generate)) > Days(proposal.Decision.Chosen));

        var dear = Select(v, Row(0, 8), planning with
        {
            Valuation = planning.Valuation with { Pieces = new Dictionary<string, ProvisionOption> { [Wood] = Piece(Wood, 5, 1), [Conduit] = Piece(Conduit, 10, 1) } },
        });
        Assert.Equal(PowerMethod.Generate, dear.Method);
    }

    // (f) A short winter network whose consumers are a bench with a bill, a vitals monitor beside a patient queued for surgery
    // and a bedroom heater builds rather than defers: each loss is its service's own valuation (the recipe flow's run value,
    // the care bed's surgery loss, the housing valuation's flat mood gain), and a lamp no model prices stays a named lower bound.
    [Fact]
    public void AShortWinterNetworkOfABenchAPatientsMonitorAndABedroomHeaterBuilds()
    {
        const double Day = GameTime.TicksPerDay;
        // The bench: one forever parka bill, a quarter day's work a run worth one capability-day; it runs nothing unpowered.
        var parka = new Resource("Apparel_Parka");
        var bench = ProductionBenchFixtures.Observed("bench", "ElectricTailoringBench", "t", true) with { Bills = [BillWire.Bill("parka") with { Recipe = "Make_Parka" }] };
        var speeds = new Dictionary<string, BenchSpeedModel> { ["ElectricTailoringBench"] = new(1, 0, []) { RequiresPower = true } };
        var recipes = new Dictionary<string, RecipeFlow> { ["Make_Parka"] = new RecipeFlow { Products = [new Amount(parka, 1)], Work = Day / 4 } };
        var values = new Dictionary<Resource, ConstructionMaterialValue> { [parka] = new(MaterialValueBasis.Labor, 1) };
        var workshop = PowerValuationRules.WorkshopLoss(bench, speeds, recipes, values, new Dictionary<Resource, long>(), double.NaN, true);
        // Four runs a day, each its product's value less the quarter day of labor it takes.
        Assert.Equal(new InterruptionLoss.PerDay(4 * (ForecastWorkshop.RunValueOf(recipes["Make_Parka"], values, double.NaN) - 0.25)), workshop);

        // The monitor: a patient in the medical bed two cells away, queued for a surgery a 0.9 doctor performs; half its failures die.
        var bed = new SupportCareBed("medbed", new(2, 2), 1)
        {
            Medical = true, Prisoners = false, Slaves = false, Humanlike = true, Occupants = ["patient"], UsableNow = true, Movement = new CareMovement.Walk(30),
            InfectionChanceFactor = 1, Temperature = CoupledFixtures.Comfortable, HealPerDay = 0, SurgerySuccessFactor = new CareSurgeryFactor.Read(1),
        };
        var support = CoupledFixtures.NoSupport with
        {
            Patients = [CoupledFixtures.CarePatient("care", "patient", BedOccupant.Colonist, new CarePosture.Lying(), CareConduct.Calm, bed)],
            Demands = [CoupledFixtures.Demand("op", "surgery", "patient", new(2, 2), 0, 0) with
                { SurgeryOutcomes = [new("success", 0, 0, 0), new("death", 0.5, 0, 0)], SurgeryEnvironment = true, SurgeryQualityMax = 0.98 }],
            Workers = [new("op", "doctor", "patient", "DoBill", "DoBillsMedicalHumanOperation", new(2, 2), 0d, 100d)
                { Medicine = null, Treatment = CoupledFixtures.Surgery(new("bill", "operation", []), quality: 0.9) }],
        };
        var monitor = new FacilityLink { Def = "VitalsMonitor", MaxDistance = 8, SurgerySuccessChanceOffset = 0.05 };
        var value = new ForecastValuePolicy();
        var care = PowerValuationRules.CareLoss(new(2, 4), monitor, support, [], value, true);
        // 0.9 x 1 succeeds 0.9 powered, 0.9 x 0.95 unpowered: the bed's own surgery loss at each factor.
        var request = new CareCapacityRequest { Support = support, Value = value };
        var surgery = support.Demands.ToArray();
        var lost = CareCapacityRules.SurgeryLossOf(request, surgery, bed with { SurgerySuccessFactor = new CareSurgeryFactor.Read(0.95) }).Loss
            - CareCapacityRules.SurgeryLossOf(request, surgery, bed).Loss;
        // The queued surgery is done once: its loss is charged once, not every day of the horizon.
        Assert.Equal(new InterruptionLoss.PerDay(0) { OnceDays = lost }, care);
        // The monitor standing unpowered now: the bed's read factor already lacks the offset (CompFacility applies it only powered), so
        // the same loss is read from a 0.95 factor, powered 1 against unpowered 0.95, not 0.95 against 0.9.
        var dark = support with { Patients = [support.Patients[0] with { Beds = [bed with { SurgerySuccessFactor = new CareSurgeryFactor.Read(0.95) }] }] };
        Assert.Equal(care, PowerValuationRules.CareLoss(new(2, 4), monitor, dark, [], value, false));
        // No surgery queued on the patient: the facility's surgery term is nothing.
        Assert.Equal(new InterruptionLoss.PerDay(0), PowerValuationRules.CareLoss(new(2, 4), monitor, support with { Demands = [] }, [], value, true));
        Assert.Equal((((1 - 0.855) * 0.5) - ((1 - 0.9) * 0.5)) * value.DeathTerminalValue, lost, 9);

        // The heater: its sleeper sleeps at 20 now and at the -10 outdoors the room settles at without it, in slept_in_cold.
        var sleeper = ForecastValueTests.Pawn() with
        {
            OwnedBed = "bed", SleepComfortableMin = 10, SleepComfortableMax = 30,
            ThoughtStages = [new("SleptInCold", "slept_in_cold", 0, -4, NeedsFixtures.Lasting(Day, false))],
        };
        var gains = new BedroomGainFacts
        {
            Pawns = new Dictionary<string, NeedsForecastPawn> { ["pawn"] = sleeper },
            Breaks = new() { Threshold = 0.35, Recovery = new MoodRecovery.Measured(.25), Tiers = [new(1, 2), new(1, 3), new(1, 5)] },
            WellbeingWeight = 1,
        };
        var sleep = PowerValuationRules.SleepLoss([sleeper], 20, new PassiveSteady.Read(-10), gains);
        Assert.Equal(new InterruptionLoss.PerDay(-gains.MoodGain("pawn", -4)!.Value), sleep);

        var v = new PowerTopology
        {
            Buildings = [Site("gen", Wood, new(2, 2), 500), Site("bench", "ElectricTailoringBench", new(4, 2), -300), Site("monitor", "VitalsMonitor", new(6, 2), -80),
                Site("heater", "Heater", new(8, 2), -175), Site("lamp", "StandingLamp", new(10, 2), -30)],
        };
        var planning = Base with
        {
            Generators = [WoodOption()],
            Valuation = new PowerValuation
            {
                Consumers = new Dictionary<string, ConsumerValue>(StringComparer.Ordinal)
                {
                    ["bench"] = new(UtilityService.Workshop, workshop), ["monitor"] = new(UtilityService.Care, care), ["heater"] = new(UtilityService.Sleep, sleep),
                    ["lamp"] = new(UtilityService.Unmodelled, new InterruptionLoss.Unassessed(PowerRules.ConsumerServiceFact + "StandingLamp")),
                },
                Pieces = new Dictionary<string, ProvisionOption>(StringComparer.Ordinal) { [Wood] = Piece(Wood, 1, 1) },
            },
        };

        var built = Select(v, Row(0, 12), planning);
        Assert.Equal((PowerMethod.Generate, Wood), (built.Method, built.Definition));
        Assert.All(new[] { workshop, sleep }, loss => Assert.True(Assert.IsType<InterruptionLoss.PerDay>(loss).CapabilityDays > 0));
        // Switching the heater off, the cheapest consumer to lose, still costs its sleeper's mood over the horizon; deferring costs most.
        var decision = built.Decision!;
        Assert.Equal(sleep, new InterruptionLoss.PerDay(Days(decision.Alternatives.Single(a => a.Alternative.Kind == UtilityChoiceKind.Configure)) / ColonyModelPolicy.DefaultHorizonDays));
        Assert.True(Days(decision.Alternatives.Single(a => a.Alternative.Kind == UtilityChoiceKind.Defer)) > Days(decision.Chosen));
        // The surgery meets the network as it stands while the generator is built: its loss once, at the base unserved share.
        var share = UtilityChoiceRules.UnservedShare([new PowerProducer { NominalW = 500, Profile = PowerRules.ConstantPowerProfile }], 0, 0, 585, 0.5, false);
        var deferred = Days(decision.Alternatives.Single(a => a.Alternative.Kind == UtilityChoiceKind.Defer));
        Assert.Equal(share * ((3 + 0.04) * ColonyModelPolicy.DefaultHorizonDays + lost), deferred, 9);
    }

    // (g) The passive provision is filled from the colony's own thermal read: a summer bedroom held at 21 by a wall cooler, with
    // a passive cooler available, gets the passive cooler's steady temperature where the cooler's cold side stands (the room's
    // balance without the powered cooler, with the burner's push added), its sleeper's band and its piece plus a horizon of fuel;
    // on the short network it beats the generator.
    [Fact]
    public void APassiveCoolerIsFilledFromTheThermalReadAndBeatsAGenerator()
    {
        var wood = new Resource("WoodLog");
        var interior = Enumerable.Range(1, 3).SelectMany(x => Enumerable.Range(1, 3).Select(z => new Cell(x, z))).ToArray();
        var ring = Enumerable.Range(0, 5).SelectMany(x => Enumerable.Range(0, 5).Select(z => new Cell(x, z))).Except(interior).ToArray();
        var cooler = PowerBuildingFixtures.Trader("cooler", "Cooler", new(2, 4), -200, 0, true);
        var thermal = CoupledThermalTests.NoThermal with
        {
            Outdoors = 35,
            Cells = EquatableArray.From(interior.Select(c => new ThermalCell(c, 21, "thin", false, false, "", false, false, 0, false))
                .Concat(ring.Select(c => new ThermalCell(c, 35, "none", true, false, "", false, false, 0, false)))),
            // The powered cooler pushes its cold into the cell behind it, built in the wall.
            Sources = [new("cooler", new(2, 3), true, -0.35, 21, CoupledThermalTests.Powered(60000)) { BuildCell = new(2, 4), HotSideTemperature = 35, PowerWatts = 200 }],
        };
        var passive = new TemperatureDefinition
        {
            Name = "PassiveCooler", HeatPerSecond = -11, PowerW = 0, MinimumC = 17, MaximumC = 100, Wall = false, Controlled = false, BuildValue = 0,
            Fuels = [wood], FuelItemsPerDay = 10,
        };
        var powered = new TemperatureDefinition
        {
            Name = "Cooler", HeatPerSecond = -21, PowerW = 200, MinimumC = -270, MaximumC = 100, Wall = true, Controlled = true, BuildValue = 0, Fuels = [],
        };
        var sleeper = ForecastValueTests.Pawn() with { OwnedBed = "bed", SleepComfortableMin = 10, SleepComfortableMax = 26 };
        var facts = ForecastCoupledTests.ConstructionReview();
        facts = facts with
        {
            Census = facts.Census with { OutdoorTemperatureC = 35 },
            Definitions = [new PlanningDefinition { Name = "PassiveCooler", Available = true, FixedConstruction = new() { Costs = [new Amount(wood, 50)], WorkToBuild = 1500 } }],
            Stations = facts.Stations with { Temperature = [powered, passive] },
            EssentialInputs = new() { Power = new() { Buildings = [cooler] } },
            Upkeep = facts.Upkeep with { Rooms = RoomFixtures.Census((RoomFixtures.State("bedroom", RoomRole.Bedroom, beds: ["bed"]), interior)) },
            ForecastObserved = facts.ForecastObserved with
            {
                Needs = facts.ForecastObserved.Needs! with { Pawns = [sleeper] },
                Coupled = facts.ForecastObserved.Coupled! with { Thermal = thermal },
                WorkPawns = new WorkPawnsRead(EquatableArray.From<WorkPawn>([PawnFixtures.Worker("pawn") with { Work = PawnFixtures.WorkTable(true, [new() { Observed = new() { DefName = "Construction", Priority = 1, Disabled = false } }]) }])),
            },
        };
        Assert.Equal(1, LayoutClusterRules.BuildersOf(facts.ForecastObserved.WorkPawns));
        var values = new Dictionary<Resource, ConstructionMaterialValue> { [wood] = new(MaterialValueBasis.Labor, 0.001) };

        var valuation = PowerValuationRules.Valuation(facts, [], values);

        var provision = Assert.Single(valuation.Passive);
        Assert.Equal(("cooler", "PassiveCooler", 10d, 26d), (provision.Control, provision.Definition, provision.BandMin, provision.BandMax));
        // Its push far outweighs the room's exchange with the 35 outdoors, so the room settles where the burner stops pushing.
        Assert.Equal(17, Assert.IsType<PassiveSteady.Read>(provision.Steady).TemperatureC, 6);
        var piece = ColonyPlanningRules.HostPiece(facts, "PassiveCooler")!.Value;
        Assert.Equal(LayoutClusterRules.PiecePrice(piece, values) + (10 * 0.001 * ColonyModelPolicy.DefaultHorizonDays), provision.CostDays, 9);
        // The cooler itself is the bedroom's sleep control; without it the lone-cooled room settles at the outdoors.
        Assert.Equal(UtilityService.Sleep, valuation.Consumers["cooler"].Service);

        var v = new PowerTopology { Buildings = [Site("gen", Wood, new(10, 2), 500), cooler, Site("bench", "TableSculpting", new(12, 2), -400)] };
        var planning = Base with
        {
            Generators = [WoodOption()],
            Valuation = Valuation([("cooler", UtilityService.Sleep, 20), ("bench", UtilityService.Workshop, 5)], Piece(Wood, 10, 1)) with { Passive = valuation.Passive },
        };
        var chosen = Select(v, Row(0, 14), planning);
        Assert.Equal((PowerMethod.Passive, "PassiveCooler", new Cell(2, 4)), (chosen.Method, chosen.Definition, chosen.Center));
    }
}
