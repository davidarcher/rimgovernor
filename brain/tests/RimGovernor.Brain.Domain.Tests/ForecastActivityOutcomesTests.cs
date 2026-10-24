using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ForecastActivityOutcomesTests
{
    // Synthetic ticks and avoided losses, not calibrated exposure or hunger forecasts.
    private static ForecastPawnCapacity Pawn(string id, double until = 100) =>
        new(id, "site", Known(EquatableArray.From(new[] { new ForecastTimeWindow(0, until) })), Known(1.0));
    private static ForecastActivityOption Option(string task, string pawn, double work) =>
        new(task, pawn, Known(work), Known(0.0));
    private static ForecastActivityProblem Work(bool delivered = false) => new()
    {
        Activities = [new("deliver", "site", "site", []),
            new("build", "site", "site", delivered ? [] : ["deliver"]),
            new("harvest", "site", "site", [])],
        Pawns = [Pawn("builder"), Pawn("helper")],
        Options = [Option("deliver", "builder", 30), Option("deliver", "helper", 30),
            Option("build", "builder", 70), Option("harvest", "builder", 90), Option("harvest", "helper", 90)],
        Deadline = Known(100.0), ExpansionBudget = 4096,
    };
    private static ForecastActivityOutcome[] Needs(double shelter = 300, double food = 200) =>
        [new("shelter", "exposure", "build", Known(100.0), Known(shelter)),
            new("food", "hunger", "harvest", Known(100.0), Known(food))];

    [Fact]
    public void PerUnitObjectiveUsesSharedSchedulingAndCannotCompareUnrelatedNeeds()
    {
        var problem = Work(true) with { AlternativeLaborCost = Known(100.0) };
        ForecastActivityOutcome[] outcomes = [
            new("small", "one-demand", "build", Known(100.0), default) { ProgressUnits = Known(1.0) },
            new("large", "one-demand", "harvest", Known(100.0), default) { ProgressUnits = Known(8.0) }];
        var result = ForecastActivityOutcomes.CompareActivityOutcomes(problem, outcomes, ForecastOutcomeObjective.LowestLaborPerUnit);
        Assert.Equal(["large"], result.Selected);
        Assert.False(result.NetBenefit.IsKnown);
        var dangerous = problem with { Options = problem.Options.Select(o => o.Activity == "harvest"
            ? o with { RecoveryLaborTicks = Known(50.0) } : o).ToArray() };
        Assert.Equal(["small"], ForecastActivityOutcomes.CompareActivityOutcomes(dangerous, outcomes, ForecastOutcomeObjective.LowestLaborPerUnit).Selected);
        var unknown = ForecastActivityOutcomes.CompareActivityOutcomes(problem, [outcomes[0] with { ProgressUnits = default }], ForecastOutcomeObjective.LowestLaborPerUnit);
        Assert.Empty(unknown.Selected);
        Assert.Contains("unknown_outcome_estimate:small", unknown.Findings);
        Assert.Empty(ForecastActivityOutcomes.CompareActivityOutcomes(problem with { AlternativeLaborCost = default }, outcomes, ForecastOutcomeObjective.LowestLaborPerUnit).Selected);
        Assert.Throws<DomainException>(() => ForecastActivityOutcomes.CompareActivityOutcomes(problem, [outcomes[0], outcomes[1] with { Need = "other-demand" }], ForecastOutcomeObjective.LowestLaborPerUnit));
    }

    [Fact]
    public void ExposureAndHungerReverseTheChoiceUnderTheSamePawnBudget()
    {
        // A solo builder can complete delivery + building while the helper harvests.
        // Protect the builder's first 30 ticks: now delivery requires the helper.
        var problem = Work() with { Pawns = [new("builder", "site",
            Known(EquatableArray.From(new[] { new ForecastTimeWindow(30, 100) { StartLocation = Known("site") } })), Known(1.0)), Pawn("helper")] };
        var cold = ForecastActivityOutcomes.CompareActivityOutcomes(problem, Needs());
        Assert.Equal(["shelter"], cold.Selected);
        Assert.Equal("helper", cold.Plan!.Allocations.Single(a => a.Activity == "deliver").Pawn);
        Assert.Equal("builder", cold.Plan.Allocations.Single(a => a.Activity == "build").Pawn);
        var hungry = ForecastActivityOutcomes.CompareActivityOutcomes(problem, Needs(200, 300));
        Assert.Equal(["food"], hungry.Selected);
        Assert.False(cold.SearchTruncated);
        Assert.False(hungry.SearchTruncated);
    }

    [Fact]
    public void GeneratesBothOutcomesAndUsesSoloPreparationToFreeTheHelper()
    {
        var result = ForecastActivityOutcomes.CompareActivityOutcomes(Work(), Needs());
        Assert.Equal(["food", "shelter"], result.Selected);
        Assert.Equal("builder", result.Plan!.Allocations.Single(a => a.Activity == "deliver").Pawn);
        Assert.Equal("helper", result.Plan.Allocations.Single(a => a.Activity == "harvest").Pawn);
        Assert.Equal(310, result.NetBenefit.Value);
        foreach (var pawn in result.Plan.Allocations.GroupBy(a => a.Pawn))
        {
            var rows = pawn.OrderBy(a => a.Depart).ToArray();
            for (var i = 1; i < rows.Length; i++) Assert.True(rows[i - 1].End <= rows[i].Depart);
        }
    }

    [Fact]
    public void DeliveredMaterialsReleaseLaborAndDoNotScheduleRedundantPreparation()
    {
        var result = ForecastActivityOutcomes.CompareActivityOutcomes(Work(true), Needs());
        Assert.Equal(2, result.Selected.Length);
        Assert.DoesNotContain(result.Plan!.Allocations, a => a.Activity == "deliver");
        Assert.Equal(340, result.NetBenefit.Value);
    }

    [Fact]
    public void IndividualDeadlineIsCheckedEvenWhenTheHorizonAllowsWork()
    {
        var needs = Needs();
        needs[0] = needs[0] with { Deadline = Known(60.0) };
        var result = ForecastActivityOutcomes.CompareActivityOutcomes(Work(true), needs);
        Assert.Equal(["food"], result.Selected);
        Assert.Contains("outcome_deadline", result.Findings);
    }

    [Fact]
    public void HighCostAndUnknownReliefRetainPostponement()
    {
        var expensive = ForecastActivityOutcomes.CompareActivityOutcomes(Work(), Needs(10, 10));
        Assert.Empty(expensive.Selected);
        Assert.Null(expensive.Plan);
        var needs = Needs();
        needs[0] = needs[0] with { AvoidedLaborLoss = Unknown<double>() };
        var unknown = ForecastActivityOutcomes.CompareActivityOutcomes(Work(), needs);
        Assert.Equal(["food"], unknown.Selected);
        Assert.Contains("unknown_outcome_estimate:shelter", unknown.Findings);
    }

    [Fact]
    public void ExplicitAlternativesCannotDoubleCountRelief()
    {
        var needs = Needs().Select(n => n with { ChoiceIdentity = n.Need }).ToArray();
        var result = ForecastActivityOutcomes.CompareActivityOutcomes(Work(true), [needs[0], needs[0] with { Id = "other-shelter" }]);
        Assert.Single(result.Selected);
        Assert.Equal(230, result.NetBenefit.Value);
    }

    [Fact]
    public void RecoveryCostAndUnroutedWalksAreNotFree()
    {
        var problem = Work(true);
        var danger = problem with { Options = problem.Options.Select(o => o.Activity == "build"
            ? o with { RecoveryLaborTicks = Known(400.0) } : o).ToArray() };
        Assert.Equal(["food"], ForecastActivityOutcomes.CompareActivityOutcomes(danger, Needs()).Selected);
        var distant = problem with { Activities = problem.Activities.Select(a => a.Id == "build"
            ? a with { Location = "remote" } : a).ToArray() };
        var result = ForecastActivityOutcomes.CompareActivityOutcomes(distant, Needs());
        Assert.Equal(["food"], result.Selected);
        Assert.Contains(result.Findings, f => f.StartsWith("unrouted:", StringComparison.Ordinal));
    }

    [Fact]
    public void SharedPrerequisiteExecutesOnceForDistinctOutcomes()
    {
        var problem = Work() with { Activities = [new("deliver", "site", "site", []),
            new("build", "site", "site", ["deliver"]), new("harvest", "site", "site", ["deliver"])],
            Options = [Option("deliver", "builder", 10), Option("build", "builder", 70), Option("harvest", "helper", 90)] };
        var result = ForecastActivityOutcomes.CompareActivityOutcomes(problem, Needs());
        Assert.Equal(2, result.Selected.Length);
        Assert.Single(result.Plan!.Allocations, a => a.Activity == "deliver");
        Assert.Equal(330, result.NetBenefit.Value);
    }

    [Fact]
    public void UnknownCapacityCannotBeUsedToCompleteTheMoreValuableOutcome()
    {
        var problem = Work(true) with { Pawns = [Pawn("builder") with { Windows = Unknown<EquatableArray<ForecastTimeWindow>>() }, Pawn("helper")] };
        var result = ForecastActivityOutcomes.CompareActivityOutcomes(problem, Needs());
        Assert.Equal(["food"], result.Selected);
        Assert.Contains(result.Findings, f => f.StartsWith("unknown_capacity_or_cost:build", StringComparison.Ordinal));
    }

    [Fact]
    public void OneBudgetBoundsCombinationGenerationAndSchedulingDeterministically()
    {
        var problem = Work() with { ExpansionBudget = 12 };
        var first = ForecastActivityOutcomes.CompareActivityOutcomes(problem, Needs());
        var second = ForecastActivityOutcomes.CompareActivityOutcomes(problem, Needs().Reverse().ToArray());
        Assert.True(first.SearchTruncated);
        Assert.Equal(12, first.Expansions);
        Assert.Equal(first.Selected, second.Selected);
        Assert.Equal(first.NetBenefit, second.NetBenefit);
        Assert.Contains("search_truncated", first.Findings);
    }
}
