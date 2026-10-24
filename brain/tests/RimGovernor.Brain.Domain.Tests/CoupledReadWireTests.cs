using RimGovernor.Brain.Domain;
using RimGovernor.Contracts.Wire;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>#3782: every named state of the coupled read survives the wire as itself.</summary>
public sealed class CoupledReadWireTests
{
    [Fact]
    public void ANeedTargetsStockRoundTripsAsStackedOrNotStacked()
    {
        NeedStock[] stocks = [new NeedStock.Stacked(4, new FoodFreshness.Rots(900)), new NeedStock.Stacked(2, new FoodFreshness.Never()), new NeedStock.NotStacked()];
        foreach (var stock in stocks)
        {
            var target = NeedsFixtures.Service("pin", "joy", 0d, .01) with { Stock = stock, MaxPawns = 3 };
            Assert.Equal(target, WireJson.Deserialize<NeedTarget>(WireJson.Serialize(target)));
        }
    }

    [Fact]
    public void EveryCoupledNamedStateRoundTrips()
    {
        var conversion = CoupledFixtures.Conversion("rice", "pawn", "bench", "CookMeal", "Rice", 4, "Meal", 1,
            new(1, 1), new(2, 1), new(3, 1), 0, 1, 20, 5, 4, 4);
        var read = CoupledFixtures.Empty with
        {
            Routes = [new("pawn", new(1, 1), new(2, 1), new RouteTicks.Reachable(12)) { FromStart = false },
                new("pawn", new(1, 1), new(9, 9), RouteTicks.None) { FromStart = true }],
            FoodSources = [new("rice", "Rice", new(1, 1), 4, new FoodFreshness.Rots(900)), new("salt", "Salt", new(1, 1), 4, new FoodFreshness.Never())],
            FoodConversions = [conversion, conversion with { Pawn = "a", Cooking = new FoodCooking.Incapable(), Delivery = new FoodDelivery.CannotCarry() },
                conversion with { Pawn = "b", Delivery = new FoodDelivery.NoReturnPath() }],
            FoodUses = [new("pawn", "Meal", new(3, 1), true, .8, 10, 1, new FoodFreshness.Never()) { Thoughts = [], Ingredient = "Rice" },
                new("a", "Rice", new(3, 1), false, .1, 10, 4, new FoodFreshness.Rots(900)) { Ingredient = "", Thoughts = [] },
                new("b", "Rice", new(3, 1), true, .1, 10, 4, new FoodFreshness.Rots(900))
                    { Ingredient = "", Thoughts = [NeedsFixtures.Memory("AteRawFood", -7, 60000, false)] }],
            Support = CoupledFixtures.NoSupport with
            {
                Workers = [new("care", "pawn", "patient", "TendPatient", "Tend", new(1, 1), 0, 20)
                        { Medicine = new("medicine", "MedicineHerbal", 1, new(1, 1), 3), Treatment = CoupledFixtures.Tend(.5, new InfectionCheck.Pending(40)) },
                    new("care", "a", "patient", "TendPatient", "Tend", new(1, 1), 0, 20) { Medicine = null, Treatment = CoupledFixtures.Tend() },
                    new("surgery", "b", "other", "DoBill", "Doctor", new(1, 1), 0, 20)
                        { Medicine = null, Treatment = CoupledFixtures.Surgery(new("bill", "operation", []), quality: .9) }],
            },
        };
        var back = WireJson.Deserialize<CoupledForecastRead>(WireJson.Serialize(read));
        Assert.Equal(read.Routes, back.Routes);
        Assert.Equal(read.FoodSources, back.FoodSources);
        Assert.Equal(read.FoodConversions, back.FoodConversions);
        Assert.Equal(read.FoodUses, back.FoodUses);
        Assert.Equal(read.Support.Workers, back.Support.Workers);
    }

    /// <summary>The forecast read carries its coupled read and combined food supply as plain required members, with no
    /// section wrapper: a forecast without its coupled read is a contract error.</summary>
    [Fact]
    public void TheForecastReadRequiresItsCoupledRead()
    {
        var forecast = new ForecastFacts { Needs = NeedsFixtures.Empty, Coupled = CoupledFixtures.Empty, CombinedFoodSupply = new() { Larder = new() }, Patients = [new() { PawnId = "p", MoodTarget = .5 }] };
        var json = WireJson.Serialize(forecast);
        var back = WireJson.Deserialize<ForecastFacts>(json);
        Assert.Equal(forecast.Patients, back.Patients);
        Assert.Equal(forecast.CombinedFoodSupply, back.CombinedFoodSupply);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        node.Remove("coupled");
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => WireJson.Deserialize<ForecastFacts>(node.ToJsonString()));
    }
}
