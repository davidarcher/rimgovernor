using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>A running task is never interrupted. At the boundary between tasks of a coupled batch, a participant
/// whose rest or food is at or below its threshold is not handed the next task: its order is released so the next
/// review compares the need service against the remaining work.</summary>
public sealed class ActivityNeedBoundaryTests
{
    private static ActivityTaskRead Read(PawnActivities batch, string state, double rest) => new()
    {
        Known = true, Context = new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 200 },
        Pawns = EquatableArray.From(batch.Orders.Select(o => new ActivityTaskObservation(o.Pawn,
            new(o.Assignment, o.Method, o.Revision, 0, state, "", null, 200)))),
        Needs = new() { Tick = 200, Pawns = [new() { Pawn = "pawn", State = "eligible",
            Needs = [new("rest", Known(rest), Known(.00002), Known(.3)), new("food", Known(.9), Known(.00003), Known(.3))] }] }
    };

    [Fact]
    public void TiredCookFinishingSupplyIsReleasedInsteadOfStartingTheMeal()
    {
        var supply = ActivityContinuationFixtures.Cooking();
        var cook = supply.Tasks.Single(t => t.Step.Kind == PawnActivityKind.Recipe);
        var next = CoordinatedActivitiesRules.ContinueCoordinatedActivity(supply, Known(true), Read(supply, "completed", .08) with { Bindings = [new(cook.Id, cook.Step)] })!;
        Assert.True(Assert.Single(next.Orders).Release);
        Assert.Empty(next.Tasks);
    }

    [Fact]
    public void RestedCookFinishingSupplyStartsTheMealAsBefore()
    {
        var supply = ActivityContinuationFixtures.Cooking();
        var cook = supply.Tasks.Single(t => t.Step.Kind == PawnActivityKind.Recipe);
        var next = CoordinatedActivitiesRules.ContinueCoordinatedActivity(supply, Known(true), Read(supply, "completed", .9) with
        { Bindings = [new(cook.Id, cook.Step)] })!;
        Assert.False(Assert.Single(next.Orders).Release);
        Assert.Equal(cook.Id, next.Orders[0].Task);
    }

    [Fact]
    public void TiredParticipantWaitingToDepartIsReleasedWhileTheRunningTaskIsUntouched()
    {
        var batch = ActivityContinuationFixtures.Harvest("producer", "helper", 2000);
        var read = new ActivityTaskRead
        {
            Known = true, Context = new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 300 },
            Pawns = EquatableArray.From(batch.Orders.Select(o => new ActivityTaskObservation(o.Pawn,
                new(o.Assignment, o.Method, o.Revision, o.Preparing ? 1 : 0, o.Pawn == "producer" ? "started" : "holding", "", null, 300)))),
            Needs = new() { Tick = 300, Pawns = [new() { Pawn = "helper", State = "eligible", Needs = [new("rest", Known(.05), Known(.00002), Known(.3))] }] }
        };
        var next = CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, Known(true), read)!;
        Assert.False(next.Orders.Single(o => o.Pawn == "producer").Release);
        Assert.True(next.Orders.Single(o => o.Pawn == "helper").Release);
    }

    [Fact]
    public void ARunningTaskIsNeverInterruptedByAUrgentNeed()
    {
        var supply = ActivityContinuationFixtures.Cooking();
        Assert.Null(CoordinatedActivitiesRules.ContinueCoordinatedActivity(supply, Known(true), Read(supply, "started", .08)));
    }
}
