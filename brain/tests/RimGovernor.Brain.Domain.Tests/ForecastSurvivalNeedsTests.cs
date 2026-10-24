using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ForecastSurvivalNeedsTests
{
    private static readonly RimGovernor.Brain.Domain.PawnId Builder = new("builder");
    private static readonly RimGovernor.Brain.Domain.PawnId Helper = new("helper");
    private static ColonyForecastRequest Frame(double foodTicks = 1500, double temperature = -20, double comfort = 0) => new()
    {
        Origin = new(42), HorizonDays = 2000.0 / GameTime.TicksPerDay,
        Demand = new DerivedDemand { Work = new ForecastWorkInputs { Tick = new(42),
            Pawns = Known(EquatableArray.From(new[] {
                PawnFixtures.Worker(Builder) with { Body = PawnFixtures.Body with { CellTemperatureC = temperature,
                    ComfortableMinC = comfort, ComfortableMaxC = 30.0, Hypothermia = 0.335 } },
                PawnFixtures.Worker(Helper) with { Body = PawnFixtures.Body with { CellTemperatureC = 20.0,
                    ComfortableMinC = 0.0, ComfortableMaxC = 30.0, Hypothermia = 0.0 } },
            })) } },
        Facts = new ColonyForecastFacts { Items = new ItemFacts { Nutrition = new Dictionary<Resource, double> { [new("TestFood")] = 1 } }, Food = new FoodSupply { Larder = new(), Consumers = [new() { Id = Builder, NutritionPerDay = 1.0 }, new() { Id = Helper, NutritionPerDay = 1.0 }],
            Stocks = [new() { Id = "food", Place = new FoodPlace.Stands(true, ""), Form = new FoodForm.Item(), Nutrition = 2 * foodTicks / GameTime.TicksPerDay,
                Rot = new FoodRot.Keeps(), Eaters = [Builder, Helper] }] } },
    };

    [Fact]
    public void WarmerTemperatureRemovesColdDeadline()
    {
        Assert.True(ForecastSurvivalNeeds.SurvivalNeedsOf(Frame()).Needs.Single(n => n.Id == "cold/builder").Window.Value.Due);
        var warm = ForecastSurvivalNeeds.SurvivalNeedsOf(Frame(temperature: 20));
        Assert.False(warm.Needs.Single(n => n.Id == "cold/builder").Window.Value.Due);
    }

    [Fact]
    public void FoodAllocationIncludesOtherEaters()
    {
        var frame = Frame();
        var supply = frame.Facts.Food;
        var faster = supply with { Consumers = [supply.Consumers[0], supply.Consumers[1] with { NutritionPerDay = 3.0 }] };
        var needs = ForecastSurvivalNeeds.SurvivalNeedsOf(frame with { Facts = frame.Facts with { Food = faster } });
        Assert.Equal(750, needs.Needs.Single(n => n.Id == "food/builder").Window.Value.Ticks, 6);
    }

    [Fact]
    public void UnknownFoodAndBodyNeverBecomeAZeroDeadline()
    {
        var frame = Frame();
        var unknown = frame with { Facts = frame.Facts with { Food = new() { Larder = new() }, FoodUnread = true },
            Demand = frame.Demand with { Work = frame.Demand.Work with { Pawns = Known(EquatableArray.From(
                frame.Demand.Work.Pawns.Value.Select(p => p with { Body = p.Body with { CellTemperatureC = double.NaN } }))) } } };
        var result = ForecastSurvivalNeeds.SurvivalNeedsOf(unknown);
        Assert.Contains("unknown_survival_food", result.Findings);
        Assert.Contains("unknown_need_window:cold/builder", result.Findings);
        Assert.All(result.Needs, n => Assert.False(n.Window.IsKnown));
    }

    [Fact]
    public void AlreadySeriousColdIsImmediateEvenInAWarmCell()
    {
        var frame = Frame(temperature: 20);
        var pawns = frame.Demand.Work.Pawns.Value.Select(p => p.Id == Builder
            ? p with { Body = p.Body with { Hypothermia = DashEstimateRules.HypothermiaSerious } } : p);
        frame = frame with { Demand = frame.Demand with { Work = frame.Demand.Work with { Pawns = Known(EquatableArray.From(pawns)) } } };
        Assert.Equal(0, ForecastSurvivalNeeds.SurvivalNeedsOf(frame).Needs.Single(n => n.Id == "cold/builder").Window.Value.Ticks);
    }

    [Fact]
    public void FrameMismatchIsRejected() =>
        Assert.Throws<DomainException>(() => ForecastSurvivalNeeds.SurvivalNeedsOf(Frame() with { Origin = new(43) }));
}
