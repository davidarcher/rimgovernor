using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ProductionBenchFactsTests
{
    [Fact]
    public void CookingAndButcheringUseOneNativeBenchWithoutLosingDistinctLegality()
    {
        var input = new RimGovernor.Brain.GameFacts.Observations.ProductionBenchFacts
        {
            Bench = ProductionBenchFixtures.Observed("bench", "CombinedWorkbench", "native-token", true, "kitchen"),
            Cooking = new RimGovernor.Brain.GameFacts.Observations.CookingProduction.Supported { Usable = false, Recipes = [], Production = [] },
            Butchering = new RimGovernor.Brain.GameFacts.Observations.ButcheringProduction.Supported { Usable = true, Recipes = [], HumanButchers = [], HumanCorpseMeat = [] },
        };
        var back = OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.ProductionBenchFacts>(OrdersJson.Serialize(input));
        var cooking = new ProductionBench { Observed = back.Bench, Use = new ProductionUse.Cooking(Assert.IsType<RimGovernor.Brain.GameFacts.Observations.CookingProduction.Supported>(back.Cooking)) };
        var butchering = new ProductionBench { Observed = back.Bench, Use = new ProductionUse.Butchering(Assert.IsType<RimGovernor.Brain.GameFacts.Observations.ButcheringProduction.Supported>(back.Butchering)) };
        Assert.Same(cooking.Observed, butchering.Observed);
        Assert.Equal(("bench", "CombinedWorkbench", "native-token", "kitchen"), (cooking.Id, cooking.Definition, cooking.Token, cooking.Room));
        Assert.False(cooking.Usable);
        Assert.True(butchering.Usable);
        Assert.True(butchering.Butcher);
        Assert.False(cooking.Butcher);
        Assert.Equal(cooking.Observed.Bills, butchering.Observed.Bills);
    }

    [Fact]
    public void OrdinaryProductionReadsActualNativeUsabilityAndToken()
    {
        var bench = ProductionBenchFixtures.Ordinary("art", "ArtBench", "cas-token", false);
        Assert.False(bench.Usable);
        Assert.Equal("cas-token", bench.Token);
    }
    [Fact]
    public void AValidFoodReviewWithNoActualBenchCannotInventAMealBill()
    {
        var request = new MealTierRequest
        {
            Environment = ControlledEnvironmentTests.NoEnvironment,
            Plan = new() { DemandPerDay = 1, DeliveredPerDay = 0, GapPerDay = 1 },
            RawRunwayDays = new FoodRunway.Days(10), MinDays = 1, TargetDays = 3,
            Cooks = [new() { Pawn = new("cook"), Skill = 10 }], Previous = MealTier.Simple,
        };
        Assert.Empty(MealRules.ReviewMealTier(request, []).Recipes);
        var (_, selected) = MealRules.SelectMealBill([], 1, request, []);
        Assert.False(selected);
    }
    [Fact]
    public void FinishedGearBillsDoNotCountAsWorkButCookingTargetsStillReserveTheirBuffer()
    {
        var native = BillWire.Bill("bill") with
        {
            Recipe = "Sculpture", Finished = true,
            RepeatMode = RimGovernor.Brain.GameFacts.Operations.RepeatMode.Target, TargetCount = 7,
        };
        var cooking = new ExistingProductionBill { Observed = native };
        Assert.Same(native, cooking.Observed);
        Assert.True(ProductionRules.BillAdequate(cooking, new() { Recipe = native.Recipe, Target = 7 }));
        var gear = new GearBill { Observed = native, Role = RecipeRole.Sculpture, Spec = new() };
        var bench = new GearBench
        {
            Observed = ProductionBenchFixtures.Observed("bench", "ArtBench", "token", true),
            Bills = [gear],
            Recipes = [new() { Access = ProductionBenchFixtures.Recipe(native.Recipe), Role = RecipeRole.Sculpture, Ingredients = [], RequiredWork = [], WorkAmount = 1 }],
        };
        Assert.Empty(Assert.Single(ArtOrdersRules.ArtProductionBenches([bench])).Bills);
        var unfinished = native with { Finished = false };
        var changed = bench with { Bills = [gear with { Observed = unfinished }] };
        Assert.Same(unfinished, Assert.Single(Assert.Single(ArtOrdersRules.ArtProductionBenches([changed])).Bills).Observed);
    }
}
