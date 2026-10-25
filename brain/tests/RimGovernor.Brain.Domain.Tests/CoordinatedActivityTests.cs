using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class CoordinatedActivityTests
{
    private static PawnActivities Start(bool helper = true) => ActivityContinuationFixtures.Harvest("producer", helper ? "helper" : null, 2000);
    private static ActivityTaskRead Read(PawnActivities batch, string producer = "completed", string helper = "holding", long tick = 300,
        params ActivityTaskBinding[] bindings) => ConstructionFixtures.NoTasks() with
        {
            Context = new() { NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.UnclaimedCase(), Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = tick }, Bindings = EquatableArray.From(bindings),
            Pawns = EquatableArray.From(batch.Orders.Select(o => new ActivityTaskObservation(o.Pawn,
                new(o.Assignment, o.Method, o.Revision, o.Preparing ? 1 : 0, o.Pawn == "producer" ? producer : helper, "", tick)))),
        };

    [Fact]
    public void CompletedHoldReleasesBeforeReadyDeliveryRebindWithoutChangingItsObligation()
    {
        var step = new PawnActivityStep(PawnActivityKind.Deliver, "observed-output", "", "", 6, 2000).At(new(2, 2));
        var delivery = new SelectedActivityTask("delivery", "helper", [], step, new(1, 1), 100) { Resource = "Rice" };
        var idleStep = new PawnActivityStep(PawnActivityKind.Hold, "", "", "", 0, 300);
        var idle = new PawnActivityOrder("producer", "m/idle", "m", 0, 1, false, [idleStep]) { Task = "idle" };
        var waiting = new PawnActivityOrder("helper", "m/helper", "m", 0, 1, false,
            [new(PawnActivityKind.Hold, "", "", "", 0, 2000)]) { Task = "delivery", Preparing = true };
        var batch = new PawnActivities(1, [idle, waiting])
            { Tasks = [new("idle", "producer", [], idleStep, new(3, 3), 0), delivery] };
        var cleanup = CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Owed, Read(batch, bindings:
            [new ActivityTaskBinding.Bound("delivery", step with { Target = "shared-stack" })]))!;
        Assert.True(cleanup.Orders[0].Release);
        Assert.True(PawnActivities.SameOrder(waiting, cleanup.Orders[1]));
        Assert.Equal(delivery, Assert.Single(cleanup.Tasks));
        var rebound = CoordinatedActivitiesRules.ContinueCoordinatedActivity(cleanup, ActivityNeed.Owed, Read(cleanup, bindings:
            [new ActivityTaskBinding.Bound("delivery", step with { Target = "shared-stack" })]))!;
        Assert.Equal("shared-stack", Assert.Single(rebound.Orders).Steps[0].Target);
        Assert.Equal(6, rebound.Tasks[0].Step.Count);
        Assert.All(CoordinatedActivitiesRules.ContinueCoordinatedActivity(cleanup, ActivityNeed.Owed, Read(cleanup, tick: 2000))!.Orders,
            o => Assert.True(o.Release));
    }
    [Fact]
    public void NoProgressDoesNotRestartParticipantsAndObservedOutputBindsExactHelper()
    {
        var batch = Start();
        Assert.Null(CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Owed, Read(batch, "started")));
        var delivery = batch.Tasks.Single(t => t.Step.Kind == PawnActivityKind.Deliver);
        var next = CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Owed, Read(batch, bindings:
            [new ActivityTaskBinding.Bound(delivery.Id, delivery.Step with { Target = "merged-observed-stack", Count = 3 })]))!;
        Assert.True(next.Orders.Single(o => o.Pawn == "producer").Release);
        Assert.True(PawnActivities.SameOrder(batch.Orders.Single(o => o.Pawn == "helper"),
            next.Orders.Single(o => o.Pawn == "helper")));
        next = CoordinatedActivitiesRules.ContinueCoordinatedActivity(next, ActivityNeed.Owed, Read(next, bindings:
            [new ActivityTaskBinding.Bound(delivery.Id, delivery.Step with { Target = "merged-observed-stack", Count = 3 })]))!;
        Assert.Equal("merged-observed-stack", next.Orders.Single(o => o.Pawn == "helper").Steps[0].Target);
        Assert.Equal(8, next.Tasks.Single().Step.Count); // Partial binding is not eight units delivered.
        Assert.Equal(2000, next.Tasks.Single().Step.Deadline);
    }

    [Fact]
    public void EarlyCompletionInstallsBoundedPreparationUntilNextDeparture()
    {
        var batch = Start(false);
        var delivery = batch.Tasks.Single(t => t.Step.Kind == PawnActivityKind.Deliver) with { DepartTick = 500 };
        batch = batch with { Tasks = EquatableArray.From(batch.Tasks.Select(t => t.Id == delivery.Id ? delivery : t)) };
        var waiting = CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Owed, Read(batch, tick: 300))!;
        var order = Assert.Single(waiting.Orders);
        Assert.True(order.Preparing);
        Assert.Equal(delivery.Id, order.Task);
        Assert.Equal(batch.Orders[0].Revision + 1, order.Revision);
        Assert.Equal(PawnActivityKind.Approach, order.Steps[0].Kind);
        Assert.Equal(500, order.Steps[0].NotBefore);
        Assert.Equal(PawnActivityKind.Hold, order.Steps[1].Kind);
        Assert.All(order.Steps, step => Assert.Equal(2000, step.Deadline));
        Assert.Null(CoordinatedActivitiesRules.ContinueCoordinatedActivity(waiting, ActivityNeed.Owed, Read(waiting, "holding", tick: 400)));
        Assert.True(CoordinatedActivitiesRules.ContinueCoordinatedActivity(waiting, ActivityNeed.Owed, Read(waiting, "failed", tick: 400))!.Orders[0].Release);
        var resumed = CoordinatedActivitiesRules.ContinueCoordinatedActivity(waiting, ActivityNeed.Owed, Read(waiting, "holding", tick: 500,
            bindings: [new ActivityTaskBinding.Bound(delivery.Id, delivery.Step with { Target = "actual-output" })]))!;
        Assert.False(resumed.Orders[0].Preparing);
        Assert.Equal("actual-output", resumed.Orders[0].Steps[0].Target);
        Assert.Equal(order.Revision + 1, resumed.Orders[0].Revision);
    }
    [Fact]
    public void PartialDeliveryAndCarriedRecoveryRetainRemainingQuantityAndOriginalDeadline()
    {
        var task = new SelectedActivityTask("deliver", "producer", [],
            new PawnActivityStep(PawnActivityKind.Deliver, "old", "", "", 8, 2000).At(new(30, 10)), new(10, 10), 100)
            { Resource = "Rice", Source = new(10, 10) };
        var batch = new PawnActivities(1, [new("producer", "method/producer", "method", 1, 2, false,
            [task.Step with { Target = "split", Count = 3 }]) { Task = "deliver" }]) { Tasks = [task] };
        var remaining = CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Owed, Read(batch, bindings:
            [new ActivityTaskBinding.Bound("deliver", task.Step with { Target = "fresh", Count = 5 })]))!;
        Assert.Equal(5, remaining.Tasks.Single().Step.Count);
        var retry = CoordinatedActivitiesRules.ContinueCoordinatedActivity(remaining, ActivityNeed.Owed, Read(remaining, "failed", bindings:
            [new ActivityTaskBinding.Bound("deliver", task.Step with { Target = "observed-carried", Count = 5 })]))!;
        Assert.Equal("observed-carried", retry.Orders.Single().Steps[0].Target);
        Assert.Equal(2, retry.Tasks.Single().AttemptsLeft);
        Assert.Equal(2000, retry.Orders.Single().Steps[0].Deadline);
    }

    [Fact]
    public void KnownTimeoutAndTakeoverReleaseRemainingOwnersDespiteUnknownConcern()
    {
        var batch = Start();
        Assert.All(CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Unlisted, Read(batch, "started", tick: 2000))!.Orders, o => Assert.True(o.Release));
        var read = Read(batch, "started");
        read = read with { Pawns = EquatableArray.From(read.Pawns.Select(p => p.Pawn == "helper" ? p with { Execution = null } : p)) };
        var cleanup = CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Unlisted, read)!;
        Assert.Equal("producer", Assert.Single(cleanup.Orders).Pawn);
        Assert.True(cleanup.Orders[0].Release);
    }

    [Fact]
    public void FailedConsumingWorkNeverReplaysAndUnknownExecutionNeverCompletes()
    {
        var batch = Start(false);
        Assert.True(CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Owed, Read(batch, "failed"))!.Orders.Single().Release);
        Assert.Null(CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Owed, Read(batch) with { Pawns = [] }));
        Assert.All(CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Ended, Read(batch, "started"))!.Orders, o => Assert.True(o.Release));
    }

    [Fact]
    public void CompletingOneIndependentTaskKeepsOtherRunningOrderIdentical()
    {
        var work = new PawnActivityStep(PawnActivityKind.Work, "target-a", "Harvest", "Harvest", 0, 2000);
        var a = new PawnActivityOrder("producer", "m/a", "m", 0, 1, false, [work]) { Task = "a" };
        var b = new PawnActivityOrder("helper", "m/b", "m", 0, 1, false, [work with { Target = "target-b" }]) { Task = "b" };
        var batch = new PawnActivities(1, [a, b]) { Tasks = [new("a", a.Pawn, [], work, new(1, 1), 100), new("b", b.Pawn, [], b.Steps[0], new(2, 2), 100)] };
        var next = CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Owed, Read(batch, helper: "started"))!;
        Assert.True(PawnActivities.SameOrder(b, next.Orders.Single(o => o.Pawn == b.Pawn)));
        Assert.Equal("b", next.Tasks.Single().Id);
        var exhausted = batch with { Orders = [a with { Revision = long.MaxValue }, b] };
        Assert.Null(CoordinatedActivitiesRules.ContinueCoordinatedActivity(exhausted, ActivityNeed.Owed, Read(exhausted, helper: "started")));
    }
}
