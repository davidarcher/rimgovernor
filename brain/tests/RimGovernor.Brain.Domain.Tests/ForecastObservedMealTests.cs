using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ForecastObservedMealTests
{
    private const double Day = GameTime.TicksPerDay;
    private static ForecastCoupledSelection Select(int count, double expiry)
    {
        var facts = ForecastCoupledSelectionTests.Review(level: .2, fall: 1.5 / Day);
        var pawn = facts.ForecastObserved.Needs!.Pawns[0] with { Targets =
            [NeedsFixtures.Food("meal-stack", "Meal", count, 0d, 10d, .9) with
                { Location = new(1, 1), StockCount = Known((double)count), FreshTicks = Known(expiry) }] };
        facts = facts with
        {
            Resources = Known<EquatableArray<RimGovernor.Brain.Policy.Amount>>([new(new("Meal"), count)]),
            ForecastObserved = facts.ForecastObserved with
            {
                Needs = facts.ForecastObserved.Needs with { Pawns = [pawn] },
                Coupled = facts.ForecastObserved.Coupled! with { Food = CoupledFixtures.NoFood, FoodUses = [],
                    Routes = [new("pawn", new(1, 1), new(1, 1), new RouteTicks.Reachable(0d)) { FromStart = true }] },
            },
        };
        facts = WorkRules.ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([]));
        var candidates = ForecastCoupledCandidates.GenerateCoupledActivities(facts, Day)!;
        Assert.Contains(candidates.Context.NeedTasks, n => n.Target == "meal-stack");
        return ForecastCoupledSelection.CompareActivityOutcomes(ForecastCoupled.CoupledForecastRequestOf(new("world", 100), 1, facts, 
            new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, [], new()), candidates);
    }

    [Fact]
    public void OrdinarySelectorUsesTwoObservedMealsAtDifferentTimesWithoutProducingMoreFood()
    {
        var result = Select(2, Day + 1);
        Assert.Equal(Known(true), result.Selected.Feasible);
        Assert.Equal(2, result.Selected.Plan.Allocations.Length);
        var meals = result.Selected.Plan.Allocations.OrderBy(a => a.Start).ToArray();
        Assert.True(meals[1].Start > meals[0].End + 10000);
        Assert.All(meals, meal => Assert.StartsWith("need/pawn/meal-stack/", meal.Activity));
        var food = result.Selected.Needs["pawn"].Needs.Single(n => n.Kind == "food");
        Assert.Equal(0, food.ZeroTicks.Value);
        Assert.True(food.FinalLevel.Value > 0);
    }

    [Theory]
    [InlineData(1, Day + 1)]
    [InlineData(2, 1000)]
    public void InsufficientOrExpiredObservedStockCannotFinanceRepeatedRecovery(int count, double expiry)
    {
        var result = Select(count, expiry);
        Assert.Equal(Known(true), result.Selected.Feasible);
        Assert.True(result.Selected.Plan.Allocations.Length <= count);
        Assert.True(result.Selected.Needs["pawn"].Needs.Single(n => n.Kind == "food").ZeroTicks.Value > 0);
        Assert.All(result.Selected.Plan.Allocations, meal => Assert.True(meal.End <= expiry));
    }
}