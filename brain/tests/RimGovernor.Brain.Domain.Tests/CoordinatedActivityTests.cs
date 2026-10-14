using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class CoordinatedActivityTests
{
    private static FoodActivityOption Food(string pawn, double approach, double work, double delivery, int count = 8, bool harvest = true) =>
        new(pawn, "crop", "Rice", new(10, 10), new(30, 10), "Harvest", count, approach + delivery, work)
        { ApproachTicks = Known(approach), DeliveryTicks = Known(delivery), NutritionPerUnit = Known(1.0), HarvestAllowed = Known(harvest) };
    private static PawnActivities Start(params FoodActivityOption[] options) => StartFoodActivity(new()
        { Known = true, Context = new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 100 }, Options = EquatableArray.From(options) }, 1, "method", 2000, out _)!;
    private static ActivityTaskRead Read(PawnActivities batch, string producer = "completed", string helper = "holding", long tick = 300,
        params ActivityTaskBinding[] bindings) => new()
        {
            Known = true, Context = new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = tick }, Bindings = EquatableArray.From(bindings),
            Pawns = EquatableArray.From(batch.Orders.Select(o => new ActivityTaskObservation(o.Pawn,
                new(o.Assignment, o.Method, o.Revision, o.Preparing ? 1 : 0, o.Pawn == "producer" ? producer : helper, "", null, tick)))),
        };

    [Fact]
    public void JointGenerationProposesHaulOnlyHelperAndSelfServiceAndCapacityAlternatives()
    {
        var producer = Food("producer", 0, 20, 400);
        var helper = Food("helper", 5, 500, 10, harvest: false);
        var delegated = Start(producer, helper);
        Assert.Equal("producer", delegated.Tasks.Single(t => t.Step.Kind == PawnActivityKind.Work).Pawn);
        Assert.Equal("helper", delegated.Tasks.Single(t => t.Step.Kind == PawnActivityKind.Deliver).Pawn);
        Assert.True(delegated.Orders.Single(o => o.Pawn == "helper").Preparing);
        Assert.Single(Start(producer, helper with { ApproachTicks = Known(800.0) }).Orders);
        // Adding a tiny-capacity helper cannot reduce the self-service outcome to one item.
        var smallHelper = Start(producer, helper with { Count = 1, DeliveryTicks = Known(100.0) });
        Assert.Equal(8, smallHelper.Tasks.Single(t => t.Step.Kind == PawnActivityKind.Deliver).Step.Count);
    }

    [Fact]
    public void UnknownRoutesCannotAcquireAndTruncationIsVisible()
    {
        var read = new FoodActivityRead { Known = true, Context = new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 100 }, Options = [Food("producer", 0, 20, 10) with { ApproachTicks = default }] };
        Assert.Null(StartFoodActivity(read, 1, "m", 2000, out var comparison));
        Assert.Contains(comparison!.Findings, f => f.Contains("unknown_route", StringComparison.Ordinal));
        StartFoodActivity(read with { Truncated = true }, 1, "m", 2000, out comparison);
        Assert.Contains("generation_truncated", comparison!.Findings);
    }

    [Fact]
    public void HarvestCompletionKeepsFoodReconsiderationPendingWhileHelperApproaches()
    {
        var batch = Start(Food("producer", 0, 20, 400), Food("helper", 5, 500, 10, harvest: false));
        var approaching = Read(batch, helper: "started");
        Assert.NotNull(FoodReconsideration(batch, approaching));
        Assert.Null(ReconsiderObservedFood(batch, Known(true), approaching,
            new() { Known = true, Context = new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 300 } }, out _));
        Assert.NotNull(FoodReconsideration(batch, Read(batch, helper: "holding")));
    }

    [Fact]
    public void NoProgressDoesNotRestartParticipantsAndObservedOutputBindsExactHelper()
    {
        var batch = Start(Food("producer", 0, 20, 400), Food("helper", 5, 500, 10, harvest: false));
        Assert.Null(ContinueCoordinatedActivity(batch, Known(true), Read(batch, "started")));
        var delivery = batch.Tasks.Single(t => t.Step.Kind == PawnActivityKind.Deliver);
        var next = ContinueCoordinatedActivity(batch, Known(true), Read(batch, bindings:
            [new(delivery.Id, delivery.Step with { Target = "merged-observed-stack", Count = 3 })]))!;
        Assert.True(next.Orders.Single(o => o.Pawn == "producer").Release);
        Assert.Equal("merged-observed-stack", next.Orders.Single(o => o.Pawn == "helper").Steps[0].Target);
        Assert.Equal(8, next.Tasks.Single().Step.Count); // Partial binding is not eight units delivered.
        Assert.Equal(2000, next.Tasks.Single().Step.Deadline);
    }

    [Fact]
    public void PartialDeliveryAndCarriedRecoveryRetainRemainingQuantityAndOriginalDeadline()
    {
        var task = new SelectedActivityTask("deliver", "producer", [],
            new(PawnActivityKind.Deliver, "old", "", "", 8, 2000) { Destination = new(30, 10) }, new(10, 10), 100)
            { Resource = "Rice", Source = new(10, 10) };
        var batch = new PawnActivities(1, [new("producer", "method/producer", "method", 1, 2, false,
            [task.Step with { Target = "split", Count = 3 }]) { Task = "deliver" }]) { Tasks = [task] };
        var remaining = ContinueCoordinatedActivity(batch, Known(true), Read(batch, bindings:
            [new("deliver", task.Step with { Target = "fresh", Count = 5 })]))!;
        Assert.Equal(5, remaining.Tasks.Single().Step.Count);
        var retry = ContinueCoordinatedActivity(remaining, Known(true), Read(remaining, "failed", bindings:
            [new("deliver", task.Step with { Target = "observed-carried", Count = 5 })]))!;
        Assert.Equal("observed-carried", retry.Orders.Single().Steps[0].Target);
        Assert.Equal(2, retry.Tasks.Single().AttemptsLeft);
        Assert.Equal(2000, retry.Orders.Single().Steps[0].Deadline);
    }

    [Fact]
    public void KnownTimeoutAndTakeoverReleaseRemainingOwnersDespiteUnknownConcern()
    {
        var batch = Start(Food("producer", 0, 20, 400), Food("helper", 5, 500, 10, harvest: false));
        Assert.All(ContinueCoordinatedActivity(batch, default, Read(batch, "started", tick: 2000))!.Orders, o => Assert.True(o.Release));
        var read = Read(batch, "started");
        read = read with { Pawns = EquatableArray.From(read.Pawns.Select(p => p.Pawn == "helper" ? p with { Execution = null } : p)) };
        var cleanup = ContinueCoordinatedActivity(batch, default, read)!;
        Assert.Equal("producer", Assert.Single(cleanup.Orders).Pawn);
        Assert.True(cleanup.Orders[0].Release);
    }

    [Fact]
    public void FailedConsumingWorkNeverReplaysAndUnknownExecutionNeverCompletes()
    {
        var batch = Start(Food("producer", 0, 20, 400));
        Assert.True(ContinueCoordinatedActivity(batch, Known(true), Read(batch, "failed"))!.Orders.Single().Release);
        Assert.Null(ContinueCoordinatedActivity(batch, Known(true), Read(batch) with { Known = false }));
        Assert.Null(ContinueCoordinatedActivity(batch, Known(true), Read(batch) with { Pawns = [] }));
        Assert.All(ContinueCoordinatedActivity(batch, Known(false), Read(batch, "started"))!.Orders, o => Assert.True(o.Release));
    }

    [Fact]
    public void CompletingOneIndependentTaskKeepsOtherRunningOrderIdentical()
    {
        var work = new PawnActivityStep(PawnActivityKind.Work, "target-a", "Harvest", "Harvest", 0, 2000);
        var a = new PawnActivityOrder("producer", "m/a", "m", 0, 1, false, [work]) { Task = "a" };
        var b = new PawnActivityOrder("helper", "m/b", "m", 0, 1, false, [work with { Target = "target-b" }]) { Task = "b" };
        var batch = new PawnActivities(1, [a, b]) { Tasks = [new("a", a.Pawn, [], work, new(1, 1), 100), new("b", b.Pawn, [], b.Steps[0], new(2, 2), 100)] };
        var next = ContinueCoordinatedActivity(batch, Known(true), Read(batch, helper: "started"))!;
        Assert.True(PawnActivities.SameOrder(b, next.Orders.Single(o => o.Pawn == b.Pawn)));
        Assert.Equal("b", next.Tasks.Single().Id);
        var exhausted = batch with { Orders = [a with { Revision = long.MaxValue }, b] };
        Assert.Null(ContinueCoordinatedActivity(exhausted, Known(true), Read(exhausted, helper: "started")));
    }
}
