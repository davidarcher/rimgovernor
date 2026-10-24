using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ProductiveDemandTests
{
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(false, false, true)]
    public void UsefulBatchRemainderIsAllowedButSaturatedEatingCannotJustifyLearningInvestment(bool learning, bool saturated, bool feasible)
    {
        var conversion = ForecastPortfolioChoiceTests.Conversion("rice", "Rice", "SimpleMeal", 100) with
        { AvailableInput = 8, ProductCount = 2, Cooking = new FoodCooking.Cook(100, 0, learning ? ProductiveLearningTests.Work() : null) };
        var facts = ForecastPortfolioChoiceTests.Observed([conversion], [new("rice", "Rice", new(1, 1), 8, new FoodFreshness.Rots(2000d))]);
        var needs = facts.ForecastObserved.Needs!;
        facts = facts with { ForecastObserved = facts.ForecastObserved with
        {
            Needs = needs with { Pawns = EquatableArray.From(needs.Pawns.Select(p => p with
            { Needs = EquatableArray.From(p.Needs.Select(n => n.Kind == "food" ? n with { Level = saturated ? 1d : .4, FallPerTick = 0d } : n)) })) }
        } };
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts, 700)!;
        var batch = generated.Alternatives.Single(c => c.Id.StartsWith("food/", StringComparison.Ordinal) && c.ActivityCompletions.Count == 2);
        var eating = generated.Context.NeedTasks.First(n => n.FoodReady == batch.ActivityCompletions[0]);
        var eat = generated.Alternatives.Single(c => c.ActivityCompletions.Contains(eating.Task));
        var request = ForecastCoupled.CoupledForecastRequestOf(new(100), 700d / GameTime.TicksPerDay,
            facts with { CoupledAlternatives = generated },  new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, []);
        var alternatives = CoupledForecastKit.Forecast(request, [batch, eat]).Proposed.Coupled!.Alternatives;
        Assert.Equal(feasible, alternatives.Any(a => a.Feasible == Known(true)));
        if (feasible)
            Assert.Contains(alternatives.Where(a => a.Feasible == Known(true)), a => ForecastActivities.StockChangesOf(generated.Context.Activities, a.Plan).Where(c => c.Resource.Value == "Meal").Sum(c => c.Units) == 3);
        else
            Assert.Contains(alternatives.SelectMany(a => a.Findings), f => f.Reason == "productive_output_without_useful_demand");
        if (saturated)
        {
            var choice = ForecastCoupledSelection.CompareActivityOutcomes(request, generated);
            Assert.Empty(choice.Selected.Plan.Allocations);
        }
    }
}
