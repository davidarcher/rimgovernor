using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts;
using RimGovernor.Brain.GameOrders;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class WorkUpkeepTotalTests
{
    [Theory]
    [InlineData("upkeep")]
    [InlineData("lighting")]
    [InlineData("flooring")]
    [InlineData("routes")]
    public void MissingTotalSectionRefusesDirectDecoder(string section)
    {
        var snapshot = ColonyFactsCoreTests.Snapshot();
        snapshot = section switch
        {
            "upkeep" => snapshot with { Upkeep = null! },
            "lighting" => snapshot with { Upkeep = snapshot.Upkeep with { Lighting = null! } },
            "flooring" => snapshot with { Upkeep = snapshot.Upkeep with { Flooring = null! } },
            "routes" => snapshot with { Upkeep = snapshot.Upkeep with { Routes = null! } },
            _ => throw new ArgumentException(section),
        };
        Assert.Contains("upkeep", Assert.Throws<BridgeContractException>(() => ColonyPlanningReads.Compose(snapshot, new() { Context = snapshot.Context }, default!, null)).Message);
    }

    [Theory]
    [InlineData("lighting")]
    [InlineData("flooring")]
    [InlineData("routes")]
    public void MissingTotalSectionRefusesWireDecoder(string section)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(OrdersJson.Serialize(ColonyFactsCoreTests.Snapshot()))!;
        Assert.True(node["upkeep"]!.AsObject().Remove(section));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => OrdersJson.Deserialize<RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot>(node.ToJsonString()));
    }

    [Fact]
    public void PlannerCannotReadAbsentUpkeepOrDemandAsEmpty()
    {
        var upkeep = new UpkeepObservation();
        Assert.Equal("lighting census", Assert.Throws<RequiredFactException>(() => upkeep.Lighting).Fact);
        Assert.Equal("flooring census", Assert.Throws<RequiredFactException>(() => upkeep.Flooring).Fact);
        Assert.Equal("routes census", Assert.Throws<RequiredFactException>(() => upkeep.Routes).Fact);
        Assert.Equal("resource demand", Assert.Throws<RequiredFactException>(() => new StationFacts().Stock).Fact);
        Assert.Equal(0, LayoutClusterRules.BuildersOf(new([])));
    }

    [Theory]
    [InlineData(false, 0, false, ThroughputIngredients.NoRecipe)]
    [InlineData(true, 0, false, ThroughputIngredients.Missing)]
    [InlineData(true, 2, false, ThroughputIngredients.Stocked)]
    [InlineData(true, 0, true, ThroughputIngredients.Stocked)]
    public void ThroughputNamesMissingRecipeAndIngredientStock(bool offers, long stock, bool takesNone, ThroughputIngredients expected)
    {
        var ingredient = new Resource("observed-ingredient");
        var recipe = new GearRecipe
        {
            Access = ProductionBenchFixtures.Recipe("observed-recipe"), RequiredWork = [], WorkAmount = 1,
            Ingredients = takesNone ? [] : [[new Amount(ingredient, 1)]],
        };
        var input = new DispatchInputs
        {
            Benches = offers ? [new GearBench { Observed = ProductionBenchFixtures.Observed("observed-bench", "observed-kind", "observed-token", true), Bills = [], Recipes = [recipe] }] : [],
            Stock = new Dictionary<Resource, long> { [ingredient] = stock },
        };
        var order = new OrderSpec { Recipe = recipe.Definition, BenchKind = "observed-kind" };
        Assert.Equal(expected, WorkRules.ObserveOrder(order, offers ? 1 : 0, new DispatchSizing.Sized(0, 1, 1), input).Ingredients);
    }
    [Theory]
    [InlineData(ThroughputIngredients.Missing, UnmetReason.Ingredients, false)]
    [InlineData(ThroughputIngredients.Stocked, UnmetReason.Haul, false)]
    [InlineData(ThroughputIngredients.NoRecipe, UnmetReason.None, true)]
    public void IngredientStatesControlThroughputCalibration(ThroughputIngredients ingredients, UnmetReason expected, bool widen)
    {
        var start = new ThroughputObservation { Tick = 1, Target = 100, Carrying = 1, Eligible = 2, Capacity = 10, Active = true, Ingredients = ingredients,
            Output = new ProductionOutput.Observed("load", 0), Basis = new(new(), [], []) };
        var (window, _) = WorkRules.CalibrateThroughput(new(), start);
        var (_, result) = WorkRules.CalibrateThroughput(window, start with { Tick = 1 + WorkRules.ThroughputWindowTicks });
        Assert.True(result.Closed);
        Assert.True(result.IsShort);
        Assert.Equal(expected, result.Reason);
        Assert.Equal(widen, result.Widen);
    }
}
