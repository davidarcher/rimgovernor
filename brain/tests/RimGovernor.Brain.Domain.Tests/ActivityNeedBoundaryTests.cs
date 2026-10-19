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

    private static PawnActivities Service(PawnActivityKind kind, long depart)
    {
        var service = new SelectedActivityTask("service", "pawn", [], new(kind, "target", "", "", kind == PawnActivityKind.Eat ? 1 : 0, 5000), new(3, 4), depart);
        return new(1, [new("pawn", "method/pawn", "method", 0, 1, false,
            [new(PawnActivityKind.Approach, "", "", "", 0, 5000) { Destination = service.Location, NotBefore = depart },
             new(PawnActivityKind.Hold, "", "", "", 0, 5000)]) { Task = service.Id, Preparing = true }]) { Tasks = [service] };
    }

    [Theory]
    [InlineData(PawnActivityKind.Eat)]
    [InlineData(PawnActivityKind.Sleep)]
    [InlineData(PawnActivityKind.Recreation)]
    public void TiredParticipantWaitingToDepartForAServiceKeepsItsHold(PawnActivityKind kind)
    {
        // Releasing it would only have the next review reselect the same service, every review (#3167).
        var batch = Service(kind, 1000);
        Assert.Null(CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, Known(true), Read(batch, "holding", .08)));
    }

    [Fact]
    public void TiredParticipantWhoseServiceIsDueStartsIt()
    {
        var batch = Service(PawnActivityKind.Eat, 100);
        var task = batch.Tasks.Single();
        var next = CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, Known(true), Read(batch, "holding", .08) with { Bindings = [new(task.Id, task.Step)] })!;
        var order = Assert.Single(next.Orders);
        Assert.False(order.Release);
        Assert.Equal(PawnActivityKind.Eat, Assert.Single(order.Steps).Kind);
    }

    [Fact]
    public void TiredParticipantHarvestsTheFoodItThenEats()
    {
        // Releasing it would have the next review reselect play or sleep first, then this harvest, every review (#3598).
        var play = new SelectedActivityTask("play", "pawn", [], new(PawnActivityKind.Recreation, "pin", "", "", 0, 5000), new(1, 1), 0);
        var harvest = new SelectedActivityTask("harvest", "pawn", [], new(PawnActivityKind.Work, "crop", "Harvest", "Harvest", 0, 5000), new(2, 2), 100);
        var haul = new SelectedActivityTask("haul", "pawn", [harvest.Id],
            new(PawnActivityKind.Deliver, "crop", "", "", 8, 5000) { Destination = new(4, 4) }, new(2, 2), 150) { Resource = "Rice", Source = new(2, 2) };
        var eat = new SelectedActivityTask("eat", "pawn", [haul.Id], new(PawnActivityKind.Eat, "observed-output", "", "", 8, 5000), new(4, 4), 200)
            { Resource = "Rice", Source = new(4, 4) };
        var batch = new PawnActivities(1, [new("pawn", "method/pawn", "method", 0, 1, false, [play.Step]) { Task = play.Id }])
            { Tasks = [play, harvest, haul, eat] };
        var next = CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, Known(true), Read(batch, "completed", .08) with { Bindings = [new(harvest.Id, harvest.Step)] })!;
        var order = Assert.Single(next.Orders);
        Assert.False(order.Release);
        Assert.Equal(harvest.Id, order.Task);
    }

    private static PawnActivities WokeBeforeItsNextService(long playDepart, long sleepEnd = 4000)
    {
        var sleep = new SelectedActivityTask("sleep", "pawn", [], new(PawnActivityKind.Sleep, "bed", "", "", 0, sleepEnd), new(1, 1), 0);
        var play = new SelectedActivityTask("play", "pawn", [], new(PawnActivityKind.Recreation, "pin", "", "", 0, 9000), new(2, 2), playDepart);
        return new(1, [new("pawn", "method/pawn", "method", 0, 1, false, [sleep.Step]) { Task = sleep.Id }]) { Tasks = [sleep, play] };
    }

    [Fact]
    public void ParticipantThatWokeEarlyIsReleasedRatherThanHeldForAFarService()
    {
        // The sleep ended at the wake threshold, long before the planned play: holding it would idle it, owned and unreviewable (#3598).
        var batch = WokeBeforeItsNextService(5000);
        var next = CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, Known(true), Read(batch, "completed", .95))!;
        Assert.True(Assert.Single(next.Orders).Release);
        Assert.Empty(next.Tasks);
    }

    [Theory]
    [InlineData(500, 4000)]
    [InlineData(5000, 150)]
    public void ParticipantWhoseNextServiceIsNearOrWhoseSleepRanItsCoursePreparesForIt(long playDepart, long sleepEnd)
    {
        var batch = WokeBeforeItsNextService(playDepart, sleepEnd);
        var order = Assert.Single(CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, Known(true), Read(batch, "completed", .95))!.Orders);
        Assert.False(order.Release);
        Assert.True(order.Preparing);
        Assert.Equal("play", order.Task);
    }

    [Fact]
    public void ARunningTaskIsNeverInterruptedByAUrgentNeed()
    {
        var supply = ActivityContinuationFixtures.Cooking();
        Assert.Null(CoordinatedActivitiesRules.ContinueCoordinatedActivity(supply, Known(true), Read(supply, "started", .08)));
    }
}
