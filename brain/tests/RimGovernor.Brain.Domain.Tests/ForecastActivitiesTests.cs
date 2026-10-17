using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ForecastActivitiesTests
{
    private static ForecastPawnCapacity Pawn(string id, double rate = 1, double until = 5000) => new(id, "home", Known(EquatableArray.From(new[] { new ForecastTimeWindow(0, until) })), Known(rate));
    private static ForecastActivityOption Option(string task, string pawn, double ticks, double injury = 0) => new(task, pawn, Known(ticks), Known(injury));
    private static ForecastActivityRoute Route(string pawn, string from, string to, double ticks) => new(pawn, from, to, Known(ticks));
    // Synthetic estimates, not game constants: the helper has a 100-tick detour.
    private static ForecastActivityProblem Hunt(double distance = 200, double helperRate = 1,
        double harvestTime = 100, double injury = 0, double helperCapacity = 100) => new()
    {
        Activities = [new("produce", "field", "field", []), new("retrieve", "field", "home", ["produce"])],
        Pawns = [Pawn("specialist", 3), Pawn("generalist", helperRate)],
        Options = [Option("produce", "specialist", harvestTime, injury), Option("retrieve", "specialist", distance),
            Option("retrieve", "generalist", distance * (2 * Math.Ceiling(100 / helperCapacity) - 1))],
        Routes = [Route("specialist", "home", "field", distance), Route("generalist", "home", "field", distance + 100)],
        Deadline = Known(5000.0), AlternativeLaborCost = Known(4000.0),
    };
    private static ForecastActivityAllocation Assigned(ForecastActivityComparison result, string task) => Assert.Single(result.Selected!.Allocations, a => a.Activity == task);

    [Fact]
    public void DistanceChangesWhetherSendingAHelperPaysWithoutASquadTemplate()
    {
        var near = ForecastActivities.CompareActivityPlans(Hunt(20));
        var far = ForecastActivities.CompareActivityPlans(Hunt());
        Assert.Equal("specialist", Assigned(near, "retrieve").Pawn);
        Assert.Equal("generalist", Assigned(far, "retrieve").Pawn);
        Assert.Equal(420, near.Selected!.LaborCost);
        Assert.Equal(1400, far.Selected!.LaborCost);
        Assert.True(Assigned(far, "retrieve").Depart < Assigned(far, "produce").End);
        Assert.Equal(Assigned(far, "produce").End, Assigned(far, "retrieve").Start);
        Assert.Contains(far.Alternatives, p => p.LaborCost == 1500);
    }

    [Fact]
    public void CandidateGenerationMattersEvenWithIdenticalWeights()
    {
        var full = Hunt() with { Deadline = Known(500.0) };
        var solo = full with { Options = full.Options.Where(o => o.Pawn == "specialist").ToArray() };
        Assert.Equal(1500, ForecastActivities.CompareActivityPlans(solo).Selected!.LaborCost);
        Assert.Equal(1400, ForecastActivities.CompareActivityPlans(full).Selected!.LaborCost);
        // Preparatory travel overlaps the prerequisite; waiting at home preserves baseline work.
        full = Hunt(harvestTime: 300) with { Deadline = Known(700.0) };
        var result = ForecastActivities.CompareActivityPlans(full);
        Assert.Equal("generalist", Assigned(result, "retrieve").Pawn);
        Assert.Equal(200, Assigned(result, "retrieve").Depart);
        Assert.Equal(700, result.Selected!.CompletedAt);
    }

    [Fact]
    public void CarryingCapacitySkillAndDisplacedObligationsChangeChoices()
    {
        Assert.Equal("generalist", Assigned(ForecastActivities.CompareActivityPlans(Hunt()), "retrieve").Pawn);
        Assert.Equal("specialist", Assigned(ForecastActivities.CompareActivityPlans(Hunt(helperCapacity: 50)), "retrieve").Pawn);
        Assert.Equal("specialist", Assigned(ForecastActivities.CompareActivityPlans(Hunt(helperRate: 4)), "retrieve").Pawn);
        Assert.Equal("specialist", Assigned(ForecastActivities.CompareActivityPlans(Hunt(harvestTime: 0) with { Deadline = Known(450.0) }), "retrieve").Pawn);
        var fast = Hunt() with { Deadline = Known(550.0) };
        var slow = Hunt(harvestTime: 300) with { Deadline = Known(550.0) };
        Assert.NotNull(ForecastActivities.CompareActivityPlans(fast).Selected);
        Assert.Null(ForecastActivities.CompareActivityPlans(slow).Selected);
        var protectedDuty = Hunt();
        protectedDuty = protectedDuty with { Pawns = [Pawn("specialist", 3), Pawn("generalist", until: 400)] };
        Assert.Equal("specialist", Assigned(ForecastActivities.CompareActivityPlans(protectedDuty), "retrieve").Pawn);
    }

    [Fact]
    public void WaitingIsChargedAndJustInTimeTravelPreservesDivisibleBaselineWork()
    {
        var result = ForecastActivities.CompareActivityPlans(Hunt(harvestTime: 300));
        var row = Assigned(result, "retrieve");
        Assert.Equal(200, row.Depart);
        Assert.Equal(0, row.WaitTicks);
        Assert.Equal(500, row.DisplacedLaborTicks);
        Assert.Contains(result.Alternatives, p => p.Allocations.Any(a => a.Pawn == "generalist" && a.WaitTicks == 200 && a.DisplacedLaborTicks == 700));
        Assert.Equal(result.Selected!.LaborCost, result.Selected.Allocations.Sum(a => a.DisplacedLaborTicks + a.RecoveryLaborTicks));
    }

    [Fact]
    public void DangerAndWholeOutcomeBudgetRetainAlternativeIncludingPostponement()
    {
        Assert.NotNull(ForecastActivities.CompareActivityPlans(Hunt()).Selected);
        var danger = ForecastActivities.CompareActivityPlans(Hunt(injury: 3000));
        Assert.Null(danger.Selected);
        Assert.Equal("retain_alternative", danger.Decision);
        Assert.All(danger.Alternatives, p => Assert.Equal(3000, p.Allocations.Sum(a => a.RecoveryLaborTicks)));
        Assert.Null(ForecastActivities.CompareActivityPlans(Hunt() with { AlternativeLaborCost = Known(1400.0) }).Selected);
    }

    [Fact]
    public void MaterialDeliveryAndBuildingUseTheSameDependencySearch()
    {
        var problem = new ForecastActivityProblem
        {
            Activities = [new("deliver", "home", "site", []), new("build", "site", "site", ["deliver"])],
            Pawns = [Pawn("builder", 3), Pawn("carrier")],
            Options = [Option("deliver", "builder", 200), Option("deliver", "carrier", 200), Option("build", "builder", 100)],
            Routes = [Route("builder", "home", "site", 100)],
            Deadline = Known(300.0), AlternativeLaborCost = Known(1000.0),
        };
        var result = ForecastActivities.CompareActivityPlans(problem);
        Assert.Equal("carrier", Assigned(result, "deliver").Pawn);
        Assert.Equal(100, Assigned(result, "build").Depart);
        Assert.Equal(200, Assigned(result, "build").Start);
        Assert.Equal(800, result.Selected!.LaborCost);
        Assert.Contains(result.Alternatives, p => p.LaborCost == 900 && p.Allocations.All(a => a.Pawn == "builder"));
        Assert.Equal("builder", Assigned(ForecastActivities.CompareActivityPlans(problem with { Pawns = [Pawn("builder", 3), Pawn("carrier", 4)] }), "deliver").Pawn);
    }

    [Fact]
    public void SharedPawnCannotOverlapIndependentWorkAndDependenciesRunOnce()
    {
        var problem = new ForecastActivityProblem
        {
            Activities = [new("a", "home", "home", []), new("b", "home", "home", []), new("join", "home", "home", ["a", "b"])],
            Pawns = [Pawn("worker")],
            Options = [Option("a", "worker", 100), Option("b", "worker", 100), Option("join", "worker", 50)],
            Deadline = Known(250.0), AlternativeLaborCost = Known(300.0),
        };
        var result = ForecastActivities.CompareActivityPlans(problem);
        Assert.Equal(250, result.Selected!.CompletedAt);
        Assert.Equal(3, result.Selected.Allocations.Length);
        Assert.All(result.Alternatives, plan =>
        {
            var rows = plan.Allocations.OrderBy(a => a.Depart).ToArray();
            Assert.True(rows.Zip(rows.Skip(1)).All(pair => pair.First.End <= pair.Second.Depart));
        });
        Assert.Null(ForecastActivities.CompareActivityPlans(problem with { Deadline = Known(249.0) }).Selected);
    }

    [Fact]
    public void UnknownsNeverBecomeFreeCapacityRoutesOrRecovery()
    {
        var p = Hunt();
        var unknown = p with { Options = p.Options.Select(o => o with { RecoveryLaborTicks = Unknown<double>() }).ToArray() };
        Assert.Empty(ForecastActivities.CompareActivityPlans(unknown).Alternatives);
        Assert.Contains(ForecastActivities.CompareActivityPlans(unknown).Findings, f => f.StartsWith("unknown_capacity_or_cost:", StringComparison.Ordinal));
        Assert.Empty(ForecastActivities.CompareActivityPlans(p with { Routes = [] }).Alternatives);
        Assert.Equal("unknown_deadline", ForecastActivities.CompareActivityPlans(p with { Deadline = Unknown<double>() }).Decision);
        Assert.Equal("unknown_alternative_cost", ForecastActivities.CompareActivityPlans(p with { AlternativeLaborCost = Unknown<double>() }).Decision);
        Assert.Empty(ForecastActivities.CompareActivityPlans(p with { Pawns = p.Pawns.Select(a => a with { Windows = Unknown<EquatableArray<ForecastTimeWindow>>() }).ToArray() }).Alternatives);
    }

    [Fact]
    public void BoundedSearchCountsUnknownAttemptsAndIsStableUnderInputPermutation()
    {
        var p = Hunt() with { ExpansionBudget = 5 };
        var first = ForecastActivities.CompareActivityPlans(p);
        var reversed = ForecastActivities.CompareActivityPlans(p with { Activities = p.Activities.Reverse().ToArray(), Pawns = p.Pawns.Reverse().ToArray(), Options = p.Options.Reverse().ToArray(), Routes = p.Routes.Reverse().ToArray() });
        Assert.True(first.SearchTruncated);
        Assert.Equal(5, first.Expansions);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(first), System.Text.Json.JsonSerializer.Serialize(reversed));
        var unknown = p with { ExpansionBudget = 1, Options = p.Options.Select(o => o with { WorkTicks = Unknown<double>() }).ToArray(), Activities = p.Activities.Select(a => a with { Prerequisites = [] }).ToArray() };
        Assert.True(ForecastActivities.CompareActivityPlans(unknown).SearchTruncated);
        Assert.Equal(1, ForecastActivities.CompareActivityPlans(unknown).Expansions);
    }

    [Fact]
    public void InvalidGraphsAndEstimatesAreRejected()
    {
        var p = Hunt();
        Assert.Throws<DomainException>(() => ForecastActivities.CompareActivityPlans(p with { Activities = [new("produce", "field", "field", ["retrieve"]), p.Activities[1]] }));
        Assert.Throws<DomainException>(() => ForecastActivities.CompareActivityPlans(p with { Deadline = Known(double.NaN) }));
        Assert.Throws<DomainException>(() => ForecastActivities.CompareActivityPlans(p with { Options = [.. p.Options, p.Options[0]] }));
        Assert.Throws<DomainException>(() => ForecastActivities.CompareActivityPlans(p with { ExpansionBudget = 0 }));
    }
}
