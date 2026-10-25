using System.Text.Json;
using System.Text.Json.Nodes;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire;
using Xunit;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class CanonicalFoodFactsTests
{
    private static FoodConsumer Eater() => new() { Id = new("pawn"), NutritionPerDay = 1, HumanMeatAcceptable = true };
    private static FoodLarder Larder() => new() { RawMeatNutrition = 0, CookDemandNutrition = 0, Corpses = [] };
    private static FoodStock Meal(string id, string def = "MealSimple", bool forbidden = false, bool human = false) => new()
    {
        Id = id, DefName = def, Count = 10, Nutrition = 9, UnitMass = .3,
        IsHumanMeat = human, RawMeat = false, IsHumanlike = false, Vegetable = false, RawClass = FoodIngredientClass.None,
        Holder = new PawnId(""), Eaters = [new("pawn")], Rot = new FoodRot.Keeps(),
        Place = new FoodPlace.Stands(true, "dining"), TemperatureC = 20, Form = new FoodForm.Item(), Forbidden = forbidden,
    };

    [Fact]
    public void ForecastViewSharesNativeRowsAndKeepsReserveSelectionInPolicy()
    {
        var meal = Meal("meal");
        var reserve = Meal("reserve", "MealSurvivalPack", forbidden: true);
        var barred = Meal("barred", forbidden: true);
        var human = Meal("human", forbidden: true, human: true);
        var eater = Eater();
        var larder = Larder();
        var census = new O.FoodSupplyFacts { Consumers = [eater], Stocks = [meal, reserve, barred, human], Larder = larder };
        var view = FoodForecastRules.FromCensus(census);

        Assert.Same(eater, Assert.Single(view.Consumers));
        Assert.Same(larder, view.Larder);
        Assert.Same(meal, view.Stocks[0]);
        Assert.Same(reserve, view.Stocks[1]);
        Assert.Same(human, view.Stocks[2]);
        Assert.Same(barred, Assert.Single(view.Barred));
        Assert.Equal(["reserve"], view.Reserved);
        Assert.Equal(9, Assert.IsType<FoodRunway.Days>(FoodForecastRules.ForecastFood(view, null).RunwayDays).Value);

        var protectedView = view with { Reserved = new HashSet<string> { "meal", "reserve" } };
        Assert.Equal(0, Assert.IsType<FoodRunway.Days>(FoodForecastRules.ForecastFood(protectedView, null).RunwayDays).Value);
        Assert.False(meal.Forbidden);
        Assert.Equal(["reserve"], view.Reserved);
    }

    [Fact]
    public void NativeFoodFactsRoundTripAndOmittedPhysicalFactsFail()
    {
        var stock = Meal("meal");
        var census = new O.FoodSupplyFacts { Consumers = [Eater()], Stocks = [stock], Larder = Larder() };
        var json = WireJson.Serialize(census);
        var actual = WireJson.Deserialize<O.FoodSupplyFacts>(json);
        Assert.Equal(stock.Id, Assert.Single(actual.Stocks).Id);
        Assert.IsType<FoodPlace.Stands>(actual.Stocks[0].Place);
        Assert.IsType<FoodRot.Keeps>(actual.Stocks[0].Rot);
        Assert.IsType<FoodForm.Item>(actual.Stocks[0].Form);

        var document = JsonNode.Parse(json)!.AsObject();
        Assert.True(document["stocks"]![0]!.AsObject().Remove("forbidden"));
        Assert.Throws<JsonException>(() => WireJson.Deserialize<O.FoodSupplyFacts>(document.ToJsonString()));
    }
}