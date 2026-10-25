using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Policy.Methods.Generation;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using RimGovernor.Brain.GameOrders.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class FreezerCreditTests
{
    private const double Day = GameTime.TicksPerDay;
    private static FoodStock Meat(string id = "meat", long count = 40, double rate = 1) => new()
    {
        Id = id, DefName = "Meat", Count = count, Rot = new FoodRot.Rots(2 * GameTime.TicksPerDay), RotRate = rate,
        TemperatureC = rate == 0 ? -5 : 20, IsHumanMeat = false, IsHumanlike = false, RawMeat = true, Vegetable = false,
        RawClass = FoodIngredientClass.Meat, Holder = new(""), Eaters = [], Nutrition = count * .05, UnitMass = .03,
        Place = new FoodPlace.Stands(false, ""), Form = new FoodForm.Item(), Forbidden = false,
    };
    private static PlannedRoom Room(int id = 1, int width = 1) => new()
        { Id = id, Role = PlannedRole.Freezer, Region = RegionRules.RectRegion(new(id * 10, 1, width, 1)) };
    private static ForecastShelterPlan Shelter(int id = 1, double heat = -1, double target = -5, double outside = 20) => new(
        new(ForecastShelterSource.Planned, id.ToString()), [new(id * 10, 1)], [], ShelterOutcomes.Estimated(outside, outside, .001),
        [new("cooler" + id, new(id * 10, 1), true, heat, target, CoupledThermalTests.Powered(Day)) { HotSideTemperature = outside }], []);
    private static ForecastCoupledContext Context(params FoodStock[] stocks) => PlannedRoomCreditTests.Context(PlannedRoomCredits.Shipped) with
    {
        PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.Freezer },
        LayoutRooms = new Dictionary<int, PlannedRoom> { [1] = Room() }, FreezerStock = stocks,
        Shelters = [Shelter()], RotRates = new Dictionary<int, double> { [-5] = 0, [0] = 0, [5] = .5, [6] = .6, [10] = 1, [20] = 1 },
        MaterialValues = new Dictionary<Resource, ConstructionMaterialValue>
            { [new("Meat")] = new(MaterialValueBasis.Market, .01), [new("WoodLog")] = new(MaterialValueBasis.Labor, .001) },
    };
    private static ForecastInvestmentContribution[] Credits(ForecastCoupledContext context, ForecastCoupledOutcome? outcome = null) =>
        [.. ForecastValue.Of(outcome ?? PlannedRoomCreditTests.Outcome(context), context, Day).InvestmentContributions
            .Where(c => c.Key.StartsWith("freezer/", StringComparison.Ordinal))];

    [Fact]
    public void FortyMeatAtTwentyAgainstMinusFiveEarnsItsValueOverObservedDaysToRot()
    {
        var context = Context(Meat());
        var temperature = Assert.IsType<ForecastShelterOutcome.Projected>(ForecastShelterPolicy.ForecastShelter(context.Shelters[0], new([], 0, 0), Day));
        Assert.Equal(-5, ForecastShelterPolicy.SteadyTemperature(context.Shelters[0]));
        Assert.Equal(-5, temperature.TemperatureAtHorizon, 8);
        var row = Assert.Single(Credits(context));
        Assert.Equal("freezer/1/meat", row.Key);
        Assert.Equal(7, row.Units); // Three days of own owed work, seven served days.
        Assert.Equal(-40 * .01 / 2 * 7, row.Value, 10);
        Assert.Contains(new Resource("Meat"), ForecastValue.ChargedMaterials(context));
    }

    [Fact]
    public void FrozenStockAndAnUnpoweredCoolerEarnNothingWhileMissingRequiredFactsThrow()
    {
        Assert.Empty(Credits(Context(Meat(rate: 0))));
        var context = Context(Meat());
        Assert.Empty(Credits(context with { Shelters = [context.Shelters[0] with { Sources = [] }] }));
        Assert.Empty(Credits(context with { Shelters = [context.Shelters[0] with
            { Sources = [context.Shelters[0].Sources[0] with { Supply = new HeatSupply.Unconnected() }] }] }));
        Assert.Throws<RequiredFactException>(() => Credits(context with { Shelters = [] }));
        Assert.Throws<RequiredFactException>(() => Credits(context with { RotRates = new Dictionary<int, double>() }));
        Assert.Throws<RequiredFactException>(() => Credits(context with { Shelters = [context.Shelters[0] with { Thermal = new ForecastShelterThermal.Unestimated() }] }));
        Assert.Throws<RequiredFactException>(() => Credits(context with { MaterialValues = new Dictionary<Resource, ConstructionMaterialValue>() }));
    }

    [Fact]
    public void WholeDegreeSamplesInterpolateAndSlowRotUsesTheNormalizedFormula()
    {
        Assert.Equal(.55, ForecastFreezer.RotRate(Context().RotRates, 5.5), 12);
        var context = Context(Meat(rate: .5)) with { Shelters = [Shelter(target: 5)] };
        Assert.Empty(Credits(context)); // Current and future rates are equal.
        context = context with { Shelters = [Shelter()] };
        Assert.Equal(-40 * .01 / 2 * 7, Assert.Single(Credits(context)).Value, 10);
    }

    [Fact]
    public void EachStorageCellTakesTheHighestCreditStackAndEachStackIsCreditedOnce()
    {
        var context = Context(Meat("small", 20), Meat("large", 40)) with
        {
            PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.Freezer, [2] = PlannedRole.Freezer },
            LayoutRooms = new Dictionary<int, PlannedRoom> { [1] = Room(), [2] = Room(2) }, Shelters = [Shelter(), Shelter(2)],
        };
        var outcome = PlannedRoomCreditTests.Outcome(context) with { ServedRooms = [new(2, "", 3 * Day), new(1, "", 3 * Day)] };
        Assert.Equal(["freezer/2/small", "freezer/1/large"], Credits(context, outcome).Select(c => c.Key));
    }

    [Fact]
    public void CoolerConduitAndGeneratorOweWorkAndMaterialsWithoutSchedulableRoots()
    {
        var context = Context(Meat());
        var cooler = new PlannedBuildSite("cooler", 1, OpKind.Build, "Cooler", "", Rotation.North, [new(9, 1)])
            { Material = [new(new("WoodLog"), 2)] };
        context = context with { RoomPrerequisites = new Dictionary<int, EquatableArray<PlannedBuildSite>>
            { [1] = [cooler, cooler with { Id = "conduit" }, cooler with { Id = "generator" }] },
            OwedWork = context.OwedWork.Concat(new Dictionary<string, double> { ["cooler"] = Day, ["conduit"] = Day, ["generator"] = Day })
                .ToDictionary(p => p.Key, p => p.Value) };
        var outcome = PlannedRoomCreditTests.Outcome(context);
        Assert.Equal(6 * Day, Assert.Single(outcome.ServedRooms).ServedAt);
        Assert.Equal(4, Assert.Single(Credits(context, outcome)).Units);
        var ledger = ForecastValue.Of(outcome, context, Day);
        Assert.Equal(6, ledger.InvestmentContributions.Where(c => c.Key == ForecastValue.MaterialKey(new("WoodLog"))).Sum(c => c.Units));
        Assert.DoesNotContain(context.Activities.Activities, a => a.Id is "cooler" or "conduit" or "generator");
        var blocked = context with { OwedWork = context.OwedWork.Where(p => p.Key != "generator").ToDictionary(p => p.Key, p => p.Value) };
        Assert.Empty(Credits(blocked)); // An unestimable required site cannot serve the room.
    }

    [Fact]
    public void FreezersBypassTheReadAndGeometryRoomCapsAndNativeStockReferencesReachTheLedger()
    {
        var meat = Meat();
        var facts = PlannedShelterTests.UnplacedHeatedRoom();
        var room = facts.PlannedShelters[0] with { Role = PlannedRole.Freezer };
        facts = facts with { PlannedShelters = [room], CombinedFoodSupply = facts.CombinedFoodSupply with { Stocks = [meat] } };
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts)!;
        Assert.Same(meat, Assert.Single(generated.Context.FreezerStock));
        Assert.True(generated.Context.Priced(room.Id));
        var freezers = Enumerable.Range(1, 6).Select(i => Room(i * 3, 12)).ToArray();
        var request = PlannedRules.PlannedThermalRequestOf(freezers, [], [], "Cooler", -5)!;
        Assert.Equal(6, request.Rooms.Count);
        Assert.Equal(6, request.Coolers.Count);
        var geometries = freezers.Select(r => new ForecastShelterGeometry(new(ForecastShelterSource.Planned, r.Id.ToString()),
            EquatableArray.From(r.Region.Cells()), r.Region.Bounds(), r.Door, false) { Unbounded = true });
        var shelters = CoupledShelterCandidates.Generate(geometries, RoomTestFacts.NoRooms, null, [],
            new Dictionary<(string, string), Policy.Methods.ActivityTaskCandidate>(), [], [], 0, 100);
        Assert.Equal(6, shelters.Plans.Length);
        Assert.False(shelters.Truncated);
    }

    [Theory]
    [InlineData(-1, -5, 20)]
    [InlineData(-.01, -5, 60)]
    [InlineData(.1, 100, 50)]
    [InlineData(-1, -5, 150)]
    [InlineData(.1, 200, 130)]
    public void SourceEfficiencyAndSteadyTemperatureAgreeWithTheProjectedTrajectory(double heat, double target, double outside)
    {
        var room = Shelter(heat: heat, target: target, outside: outside) with
            { Sources = [Shelter(heat: heat, target: target, outside: outside).Sources[0] with { Supply = CoupledThermalTests.Powered(1e7) }] };
        var projected = Assert.IsType<ForecastShelterOutcome.Projected>(ForecastShelterPolicy.ForecastShelter(room, new([], 0, 0), 1e6));
        var t = ForecastShelterPolicy.SteadyTemperature(room);
        Assert.Equal(t, projected.TemperatureAtHorizon, 8);
        if (t != target)
        {
            var efficiency = heat < 0 ? Math.Max(0, 1 - Math.Max(outside - t, outside - 40) / 130) : Math.Clamp((120 - t) / 100, 0, 1);
            Assert.Equal(0, .001 * (outside - t) + heat * efficiency, 10);
        }
    }

    [Fact]
    public void AClosedRoomWithNoSourceSettlesToOutdoors()
    {
        var room = Shelter() with { Sources = [], Thermal = ShelterOutcomes.Estimated(-5, 20, .001) };
        var projected = Assert.IsType<ForecastShelterOutcome.Projected>(ForecastShelterPolicy.ForecastShelter(room, new([], 0, 0), Day));
        Assert.Equal(20, ForecastShelterPolicy.SteadyTemperature(room));
        Assert.Equal(20, projected.TemperatureAtHorizon, 8);
    }

    [Fact]
    public void PlannedCoolerRequestsAndNativeBuildEstimatesSurviveTheProductionJsonBoundary()
    {
        var request = PlannedRules.PlannedThermalRequestOf([Room()], [], [], "Cooler", -5)!;
        var copy = OrdersJson.Deserialize<ConstructionActivityRequest>(OrdersJson.Serialize(new ConstructionActivityRequest(
            ConstructionFixtures.Context().Identity, []) { Thermal = request }));
        Assert.Equal(request, copy.Thermal);
        var source = Shelter().Sources[0] with { Complete = false, Definition = "Cooler", BuildCell = new(9, 1), PowerWatts = 200,
            BuildEstimate = ConstructionFixtures.Answer("cooler1") };
        var read = ConstructionFixtures.NoConstruction() with { Thermal = CoupledThermalTests.NoThermal with { Sources = [source] } };
        var answer = OrdersJson.Deserialize<ConstructionActivityRead>(OrdersJson.Serialize(read));
        Assert.Equal(source, Assert.Single(answer.Thermal!.Sources));
    }

    [Fact]
    public void MissingStockRotRateIsRejectedByTheNativeFoodContract()
    {
        var census = new FoodSupplyFacts { Consumers = [], Stocks = [Meat()], Larder = new() { RawMeatNutrition = 0, CookDemandNutrition = 0, Corpses = [] } };
        var json = System.Text.Json.Nodes.JsonNode.Parse(OrdersJson.Serialize(census))!;
        Assert.True(json["stocks"]![0]!.AsObject().Remove("rotRate"));
        Assert.Throws<System.Text.Json.JsonException>(() => OrdersJson.Deserialize<FoodSupplyFacts>(json.ToJsonString()));
    }

    [Fact]
    public void ADisconnectedEarlyFreezerIncludesItsPlannedGeneratorAndConduitAndUsesTheSameSiteGeneration()
    {
        var room = Room();
        var source = Shelter().Sources[0] with { Target = "planned/freezer1/cooler", Complete = false, Definition = "Cooler",
            BuildCell = new(9, 1), PowerWatts = 200, Supply = new HeatSupply.Unconnected(), BuildEstimate = ConstructionFixtures.Answer("planned/freezer1/cooler") };
        var facts = ForecastCoupledTests.ConstructionReview();
        facts = facts with
        {
            LayoutPlan = new() { Rooms = [room], Sites = [new() { Node = "solar", Kind = NodeKind.Solar,
                Footprint = RegionRules.RectRegion(new(20, 20, 5, 5)) }],
                Conduits = [new(ConduitRef.ForRoom(1), ConduitRef.ForSite("solar"), [new(10, 1), new(11, 1)])] },
            Definitions = [.. new[] { "Cooler", PowerRules.WindTurbineDefinition, LayoutUtilityRules.SolarDefinition,
                PowerRules.GeothermalDefinition, LayoutPowerDeriveRules.PowerConduitDefinition }.Select(def => new PlanningDefinition
                { Name = def, Available = def != PowerRules.WindTurbineDefinition, FixedConstruction = new() { Costs = [], WorkToBuild = 100 } })],
            EssentialInputs = new() { Power = new() { Conduits = [new(10, 1)] } }, CurrentConstruction = CurrentConstruction.Empty,
        };
        var prerequisites = FreezerPrerequisites.Of(facts, CoupledThermalTests.NoThermal with { Sources = [source] });
        var sites = prerequisites[1];
        Assert.Equal(3, sites.Count);
        var secondSource = source with { Target = "planned/freezer2/cooler", Cell = new(20, 1), BuildCell = new(19, 1) };
        var twoRooms = facts with { LayoutPlan = facts.LayoutPlan! with { Rooms = [room, Room(2)] } };
        var oneGenerator = FreezerPrerequisites.Of(twoRooms, CoupledThermalTests.NoThermal with { Sources = [source, secondSource] });
        Assert.True(oneGenerator.ContainsKey(1));
        Assert.False(oneGenerator.ContainsKey(2));
        var twoGenerators = twoRooms with { LayoutPlan = twoRooms.LayoutPlan! with { Sites = [.. twoRooms.LayoutPlan.Sites,
            new() { Node = "solar2", Kind = NodeKind.Solar, Footprint = RegionRules.RectRegion(new(30, 20, 5, 5)) }] } };
        var allocated = FreezerPrerequisites.Of(twoGenerators, CoupledThermalTests.NoThermal with { Sources = [source, secondSource] });
        Assert.Equal(2, allocated.Count);
        Assert.NotEqual(allocated[1].Single(s => s.Definition == LayoutUtilityRules.SolarDefinition).Id,
            allocated[2].Single(s => s.Definition == LayoutUtilityRules.SolarDefinition).Id);
        Assert.Contains(sites, s => s.Definition == LayoutUtilityRules.SolarDefinition);
        Assert.Single(sites, s => s.Definition == LayoutPowerDeriveRules.PowerConduitDefinition);
        Assert.Same(source.BuildEstimate, sites.Single(s => s.Definition == "Cooler").Answer);
        var answered = EquatableArray.From(sites.Select(s => s with { Answer = ConstructionFixtures.Answer(s.Id) with
            { Options = [new Row(s.Id, s.Definition, s.Positions[0], "pawn", "", "", s.Positions[0], 0, 0, "Finish", 0, null, 100)] } }));
        var generated = PlannedRoomCandidates.Generate(answered, PlannedRules.PlannedRoomAlternatives(answered).Alternatives,
            [], CoupledThermalTests.NoThermal, facts.WorkView!, new HashSet<string> { "pawn" }, Day, (long)Day, schedulable: false);
        Assert.Equal(3, generated.Sites.Length);
        Assert.Empty(generated.Roots);
        var context = Context(Meat()) with { Shelters = [Shelter() with { Sources = [source] }], RoomPrerequisites = prerequisites,
            OwedWork = Context().OwedWork.Concat(sites.Select(s => new KeyValuePair<string, double>(s.Id, Day))).ToDictionary(p => p.Key, p => p.Value) };
        Assert.Equal(4, Assert.Single(Credits(context)).Units);
        Assert.Empty(FreezerPrerequisites.Of(facts with { LayoutPlan = facts.LayoutPlan! with { Sites = [] } },
            CoupledThermalTests.NoThermal with { Sources = [source] }));
    }
}
