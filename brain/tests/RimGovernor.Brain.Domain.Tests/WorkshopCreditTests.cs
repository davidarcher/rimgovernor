using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Policy.Methods;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class WorkshopCreditTests
{
    private const double Day = GameTime.TicksPerDay, ParkaValue = .2;
    private const string Bench = "HandTailoringBench", Recipe = "Make_Parka";
    private static readonly Resource Parka = new("Apparel_Parka");
    private static readonly Cell Position = new(1, 1);
    private static BenchSpeedModel Speed => new(1, .1, [new BenchWorkOutdoorsPart(.8), new BenchTemperaturePart(9, 35, .7)]);
    // A standing tailoring bench lists the recipe (GearOrdersRules.GearBenchDef), so the planned room's credit is its speed gain.
    private static OrderSpec Order => new() { Recipe = Recipe, Mode = BillMode.StockTarget, Target = 100, Product = Parka, Class = ResourceClass.Material, BenchKind = Bench };
    private static ForecastCoupledContext Context(bool standing = true, bool bills = true) => ForecastValueTests.Context(ForecastValueTests.Pawn()) with
    {
        PlannedRoomRoles = new Dictionary<int, PlannedRole> { [1] = PlannedRole.Workshop },
        LayoutRooms = new Dictionary<int, PlannedRoom> { [1] = new() { Id = 1, Role = PlannedRole.Workshop,
            Production = bills ? [new(Bench, Recipe, 1)] : [] } },
        Closures = [new(1, "", [Position], [], [], PlannedRoomPredictionTests.Closed("1", "", "Workshop", 10, 1, 1, 0, false) with
            { WorkBenches = [new(Bench, Position, 0, 0, new BenchPower.Unneeded(), standing ? new BenchPlacement.Standing(.56) : new BenchPlacement.Planned())] })],
        WorkBenchSpeeds = new Dictionary<string, BenchSpeedModel> { [Bench] = Speed },
        ProductionOrders = new() { Status = WorkRules.LedgerViewReconciled, Orders = bills ? [Order] : [] },
        ProductionRecipes = new Dictionary<string, RecipeFlow> { [Recipe] = new() { Products = [new(Parka, 1)] } },
        MaterialValues = new Dictionary<Resource, ConstructionMaterialValue> { [Parka] = new(MaterialValueBasis.Market, ParkaValue) },
        Shelters = [new(new(ForecastShelterSource.Planned, "1"), [Position], [], ShelterOutcomes.Estimated(0, 0, .001),
            [new("heater", Position, true, .35, 20, CoupledThermalTests.Powered(10 * Day))], []) { PlannedRoom = 1 }],
        InvestmentHorizonDays = 10,
    };
    private static ForecastCoupledOutcome Outcome(ForecastCoupledContext context, double stock = 0, bool completed = false, double drawn = 0) =>
        new(new([], 0, 0), true, new Dictionary<string, ForecastNeedsResult>(), [])
        {
            ServedRooms = [new(1, "", 3 * Day)],
            Terminal = new(new() { Origin = new(0), HorizonDays = 1, Coupled = context }, new Dictionary<string, ForecastCommitment>(),
                new Dictionary<Resource, double> { [Parka] = stock }, completed ? new Dictionary<string, double> { ["make"] = Day } : new Dictionary<string, double>(), null,
                new Dictionary<Resource, double> { [Parka] = drawn }),
        };
    private static ForecastInvestmentContribution[] Credits(ForecastCoupledContext context, ForecastCoupledOutcome? outcome = null) =>
        [.. ForecastValue.Of(outcome ?? Outcome(context), context, Day).InvestmentContributions.Where(r => r.Key.StartsWith("workshop/", StringComparison.Ordinal))];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AUsefulParkaAtZeroDegreesEarnsTheNativeSpeedGainTimesDailyBillOutputTimesItsValue(bool standing)
    {
        var context = Context(standing);
        var row = Assert.Single(Credits(context));
        Assert.Equal("workshop/1/HandTailoringBench/Make_Parka", row.Key);
        Assert.Equal(7, row.Units);
        Assert.Equal(-(1 - .56) * 1 * ParkaValue * 7, row.Value, 12);
        Assert.True(context.Priced(1));
        Assert.Contains(Parka, ForecastValue.ChargedMaterials(context));
    }

    [Fact]
    public void NoUsefulBillsOrFullStockEarnsNothing()
    {
        Assert.Empty(Credits(Context(bills: false)));
        var context = Context();
        Assert.Empty(Credits(context, Outcome(context, stock: 100)));
        Assert.Empty(Credits(context with { ProductionOrders = new() { Status = WorkRules.LedgerViewInactive } }));
    }

    [Fact]
    public void SelectedConsumeRefillCyclesRemainUsefulAtFullTerminalStockWithoutInventingRecurringOutput()
    {
        var context = Context();
        double Value(int cycles)
        {
            var walk = new ColonyForecastRules.InventoryWalk();
            var effects = Enumerable.Range(0, cycles).SelectMany(i => new[]
                { new ColonyForecastRules.InventoryChange(Parka, .1 + i * .2, -1d), new ColonyForecastRules.InventoryChange(Parka, .2 + i * .2, 1d) }).ToArray();
            walk.Run(new([new(Parka, 100)], new Dictionary<string, ForecastCommitment>(), 1), effects, []);
            Assert.Equal(100, walk.Terminal[Parka]);
            Assert.Equal(cycles, walk.Drawn[Parka]);
            return Assert.Single(Credits(context, Outcome(context, stock: walk.Terminal[Parka], drawn: walk.Drawn[Parka]))).Value;
        }
        Assert.Equal(-(1 - .56) * ParkaValue, Value(1), 12);
        Assert.Equal(3 * Value(1), Value(3), 12);
        var moved = new ColonyForecastRules.InventoryWalk();
        moved.Run(new([new(Parka, 100)], new Dictionary<string, ForecastCommitment>(), 1),
            [new(Parka, .1, -1) { Drawn = 0 }, new(Parka, .5, 1), new(Parka, 2, -20)], []);
        Assert.Empty(moved.Drawn);
        Assert.Empty(Credits(context, Outcome(context, stock: moved.Terminal[Parka])));
    }

    [Fact]
    public void ACountTargetCountsRecipeIterationsAndValuesEveryUnitInItsNativeBatch()
    {
        var context = Context() with
        {
            ProductionOrders = new() { Status = WorkRules.LedgerViewReconciled, Orders = [Order with { Mode = BillMode.GearBatch, Target = 2 }] },
            LayoutRooms = new Dictionary<int, PlannedRoom> { [1] = new() { Id = 1, Role = PlannedRole.Workshop,
                Production = [new(Bench, Recipe, 2)] } },
            ProductionRecipes = new Dictionary<string, RecipeFlow> { [Recipe] = new() { Products = [new(Parka, 15)] } },
        };
        Assert.Equal(-(1 - .56) * 2 * 15 * ParkaValue, Assert.Single(Credits(context)).Value, 12);
        var facts = FirstRoomPlanTests.Facts(BedMergePriceTests.Temperate, "mild",
            [new() { Recipe = "MakeKnife", Mode = BillMode.GearBatch, Target = 2, BenchKind = "CraftingSpot" }]);
        var input = facts.GraphInput();
        input = input with { Recipes = new Dictionary<string, RecipeFlow>(input.Recipes)
            { ["MakeKnife"] = input.Recipes["MakeKnife"] with { Products = [new(Parka, 15)] } } };
        var production = Assert.Single(StationGraph.BuildStationGraph(input).Nodes.SelectMany(n => n.Production), p => p.Recipe == "MakeKnife");
        Assert.Equal(2, production.RunsPerDay);
        Assert.Equal(15, Assert.Single(input.Recipes[production.Recipe].Products).Count);
    }

    [Fact]
    public void ACompletedFiniteBatchEarnsNothingAndAnUnfinishedBatchCannotRepeatForEveryServedDay()
    {
        var context = Context() with
        {
            ProductionOrders = new() { Status = WorkRules.LedgerViewReconciled, Orders = [Order with { Mode = BillMode.GearBatch, Target = 1 }] },
            Activities = new() { Deadline = Day, Activities = [new("make", "tailor", "tailor", []) { Outputs = [new(Parka, 1)] }] },
            Bindings = new Dictionary<(string, string), ActivityTaskCandidate>
                { [("make", "pawn")] = new("make", "pawn", [], PawnActivityKind.Recipe, "tailor", "", "", 0, 60000, Position) { Recipe = new("bill", Recipe, []) } },
        };
        Assert.Empty(Credits(context, Outcome(context, completed: true)));
        Assert.Equal(-(1 - .56) * ParkaValue, Assert.Single(Credits(context)).Value, 12);
    }

    [Fact]
    public void TheSharedHeatBalanceKeepsAnUnheatedWorkshopAtOutdoorTemperature()
    {
        var context = Context();
        context = context with { Shelters = [context.Shelters[0] with { Sources = [] }] };
        Assert.Equal(-(.7 - .56) * ParkaValue * 7, Assert.Single(Credits(context)).Value, 12);
        Assert.Throws<RequiredFactException>(() => Credits(context with { Shelters = [] }));
        Assert.Throws<RequiredFactException>(() => Credits(context with { WorkBenchSpeeds = new Dictionary<string, BenchSpeedModel>() }));
        Assert.Throws<RequiredFactException>(() => Credits(context with { ProductionOrders = null }));
    }

    [Fact]
    public void TheFreshPlanCarriesItsChosenBillLoadThroughGraphClusteringTilingAndSave()
    {
        var order = new OrderSpec { Recipe = "MakeKnife", Mode = BillMode.GearBatch, Target = 1 };
        var facts = FirstRoomPlanTests.Facts(BedMergePriceTests.Temperate, "mild", [order]);
        var spot = facts.HostAlternatives().Alternatives.Single(a => a.Id == "host:MakeKnife/CraftingSpot");
        var node = Assert.Single(StationGraph.BuildStationGraph(facts.GraphInput(spot)).Nodes, n => n.Def == "CraftingSpot");
        Assert.Equal(1, Assert.Single(node.Production).RunsPerDay);
        var plan = FirstRoomPlanTests.DeriveHosted(facts, spot.Id);
        var room = Assert.Single(plan.Rooms, r => r.Production.Any(p => p.Recipe == order.Recipe));
        var back = RimGovernor.Brain.Store.Codec.StoreJson.Deserialize<LayoutPlan>(RimGovernor.Brain.Store.Codec.StoreJson.Serialize(plan));
        Assert.Equal(room.Production, back.Rooms.Single(r => r.Id == room.Id).Production);
        var ct = TestContext.Current.CancellationToken;
        using var journal = RimGovernor.Brain.Store.Store.Open(Path.Combine(Path.GetTempPath(), "rg-workshop-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var world = new GenerationSnapshot(new ColonyId("colony"), new MapId(1), new LoadId("load"), new PlanId("review"), new PlanRevision(1), default);
        journal.RecordLayoutPlan(world, new Tick(100), plan, ct);
        var saved = new Dictionary<string, string>(journal.GovernorStateBlobs(ct));
        var old = System.Text.Json.Nodes.JsonNode.Parse(saved[RimGovernor.Brain.Store.Store.GovernorLayoutPlanKey])!;
        foreach (var savedRoom in old["record"]!["Rooms"]!.AsArray()) savedRoom!.AsObject().Remove("Production");
        saved[RimGovernor.Brain.Store.Store.GovernorLayoutPlanKey] = old.ToJsonString();
        journal.RebuildFamilies(saved, ct);
        Assert.Null(journal.LayoutPlan(world, new Tick(100), ct));
        var review = ForecastCoupledTests.ConstructionReview() with { PlannedShelters = [room], WorkBenchSpeeds = new Dictionary<string, BenchSpeedModel> { [Bench] = Speed }, ProductionOrders = facts.Ledger, ProductionRecipes = facts.Recipes };
        var context = ForecastCoupledCandidates.GenerateCoupledActivities(review)!.Context;
        Assert.Same(room, context.LayoutRooms[room.Id]);
        Assert.Same(review.WorkBenchSpeeds, context.WorkBenchSpeeds);
        Assert.Same(facts.Ledger, context.ProductionOrders);
        Assert.Same(facts.Recipes, context.ProductionRecipes);
        var chosen = Assert.Single(room.Production);
        var priced = Context(standing: false) with
        {
            LayoutRooms = new Dictionary<int, PlannedRoom> { [1] = room with { Id = 1 } },
            ProductionOrders = facts.Ledger,
            ProductionRecipes = facts.Recipes,
            WorkBenchSpeeds = new Dictionary<string, BenchSpeedModel> { [chosen.Bench] = Speed },
            MaterialValues = facts.Recipes[chosen.Recipe].Products.ToDictionary(p => p.Resource, _ => new ConstructionMaterialValue(MaterialValueBasis.Market, ParkaValue)),
        };
        priced = priced with { Closures = [priced.Closures[0] with { Prediction = priced.Closures[0].Prediction with
            { WorkBenches = [new(chosen.Bench, Position, 0, 0, new BenchPower.Unneeded(), new BenchPlacement.Planned())] } }] };
        Assert.True(Assert.Single(Credits(priced)).Value < 0);
    }

    [Fact]
    public void StockTargetsPreserveTheDemandOwnersTypedRefillFactsInTheLedgerView()
    {
        var view = WorkRules.BuildLedgerView(new Dictionary<string, OrderSpec> { ["order"] = Order with { BenchKind = Bench } }, []);
        var order = Assert.Single(view.Orders);
        Assert.Equal((Parka, ResourceClass.Material, Bench), (order.Product, order.Class, order.BenchKind));
    }
}
