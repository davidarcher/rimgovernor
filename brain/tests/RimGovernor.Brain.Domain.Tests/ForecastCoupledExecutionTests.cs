using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
namespace RimGovernor.Brain.Domain.Tests;

public sealed class ForecastCoupledExecutionTests
{
    private static ForecastObservedIncumbent Incumbent => new(new("old-plan"), new(1,
        [new("pawn", "assignment", "method", 0, 1, false,
            [new(PawnActivityKind.Recreation, "pin", "", "", 0, 1000)])]), "retain",
        new("pawn", "assignment", "method", 1, "started"));
    private static ForecastCoupledSelection Selection(params (string Task, string Pawn)[] tasks)
    {
        var plan = new ForecastActivityPlan(tasks.Select(t => new ForecastActivityAllocation(t.Task, t.Pawn,
            0, 0, 0, 10, 0, 0, 10, 10, 0)).ToArray(), 10, 10);
        var outcome = new ForecastCoupledOutcome(plan, Known(true), new Dictionary<string, ForecastNeedsResult>(), []);
        var vector = ForecastCoupledSelection.CoupledConsequences(outcome, []);
        return new(new(tasks.Select(t => t.Task).ToArray(), plan, default, [], 0, false), outcome,
            outcome, vector, vector, [], 100, false);
    }
    [Fact]
    public void BetterReplacementReleasesExactRevisionWithoutDispatchingUnownedForecast()
    {
        var result = ForecastCoupledExecution.ReviewCoupledExecution(Selection(("eat", "pawn")), [Incumbent], 100);
        Assert.Null(result.Dispatch);
        var release = Assert.Single(Assert.Single(result.Releases).Batch.Orders);
        Assert.True(release.Release);
        Assert.Equal(1, release.ExpectedRevision);
        Assert.Equal(2, release.Revision);
        Assert.Equal("assignment", release.Assignment);
        Assert.Empty(release.Steps);
    }
    [Fact]
    public void RetentionWithFutureWorkCannotInstallSecondOwner()
    {
        var result = ForecastCoupledExecution.ReviewCoupledExecution(Selection(("retain", "pawn"), ("eat", "pawn")), [Incumbent], 100);
        Assert.Null(result.Dispatch);
        Assert.Empty(result.Releases);
    }
    [Fact]
    public void RetentionAllowsIndependentWorkWithoutRedispatchingIncumbent()
    {
        var result = ForecastCoupledExecution.ReviewCoupledExecution(Selection(("retain", "pawn"), ("eat", "other")), [Incumbent], 100);
        Assert.Empty(result.Releases);
        Assert.Equal("eat", Assert.Single(result.Dispatch!.Selection.Selected));
        Assert.Equal("other", Assert.Single(result.Dispatch.Selection.Plan!.Allocations).Pawn);
    }
    [Fact]
    public void RetainedWaitingGraphIsNotRedispatchedAndReleasedParticipantsRemainAvailable()
    {
        var old = Incumbent;
        var tombstone = new PawnActivityOrder("other", "released", "method", 1, 2, true, []);
        var incumbent = old with
        {
            Batch = old.Batch with { Orders = [.. old.Batch.Orders, tombstone] },
            Activities = ["wait", "future-sleep"],
            ReleasedOwnership = [new("other", "", "", 0, "")]
        };
        var selection = Selection(("wait", "pawn"), ("future-sleep", "pawn"), ("eat", "other"));
        selection = selection with { Selection = selection.Selection with { Selected = ["retain", "eat"] } };
        var result = ForecastCoupledExecution.ReviewCoupledExecution(selection, [incumbent], 100);
        Assert.Empty(result.Releases);
        Assert.Equal("eat", Assert.Single(result.Dispatch!.Selection.Selected));
        Assert.Equal("eat", Assert.Single(result.Dispatch.Selection.Plan!.Allocations).Activity);
    }

    [Fact]
    public void ReplacingWaitingGraphReleasesOnlyItsExactRemainingParticipant()
    {
        var old = Incumbent;
        var incumbent = old with
        {
            Batch = old.Batch with { Orders = [.. old.Batch.Orders, new("other", "released", "method", 1, 2, true, [])] },
            Activities = ["wait", "future-sleep"],
            ReleasedOwnership = [new("other", "", "", 0, "")]
        };
        var result = ForecastCoupledExecution.ReviewCoupledExecution(Selection(("eat", "pawn")), [incumbent], 100);
        Assert.Null(result.Dispatch);
        var release = Assert.Single(Assert.Single(result.Releases).Batch.Orders);
        Assert.Equal("pawn", release.Pawn);
        Assert.Equal(1, release.ExpectedRevision);
        Assert.Equal(2, release.Revision);
        Assert.True(release.Release);
        Assert.Empty(release.Steps);
    }
}