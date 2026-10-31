using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>A running task is never interrupted. At the boundary between tasks of a coupled batch, a participant
/// whose rest or food is at or below its threshold is not handed the next task: its order is released so the next
/// review compares the need service against the remaining work.</summary>
public sealed class ActivityNeedBoundaryTests
{
    private static ActivityTaskRead Read(PawnActivities batch, string state, double rest) => ConstructionFixtures.NoTasks() with
    {
        Context = new() { NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.UnclaimedCase(), Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 200 },
        Pawns = EquatableArray.From(batch.Orders.Select(o => new ActivityTaskObservation(o.Pawn,
            new(o.Assignment, o.Method, o.Revision, 0, state, "", 200)))),
        Needs = NeedsFixtures.Empty with { Tick = new(200), Pawns = [NeedsFixtures.Pawn() with { Pawn = "pawn", State = "eligible",
            Needs = [NeedsFixtures.Rate("rest", rest, .00002, .3), NeedsFixtures.Rate("food", .9, .00003, .3)] }] }
    };

    /// <summary>A finished ingredient delivery returns its pawn to native work, tired or rested: the bill it fed is native work.</summary>
    [Theory]
    [InlineData(.08)]
    [InlineData(.9)]
    public void AFinishedIngredientDeliveryReturnsToNativeWhateverTheCarriersRest(double rest)
    {
        var supply = ActivityContinuationFixtures.IngredientDelivery();
        var next = CoordinatedActivitiesRules.ContinueCoordinatedActivity(supply, ActivityNeed.Owed, Read(supply, "completed", rest))!;
        Assert.True(Assert.Single(next.Orders).Release);
        Assert.Empty(next.Tasks);
    }

    [Fact]
    public void TiredParticipantWaitingToDepartIsReleasedWhileTheRunningTaskIsUntouched()
    {
        var batch = ActivityContinuationFixtures.Harvest("producer", "helper", 2000);
        var read = ConstructionFixtures.NoTasks() with
        {
            Context = new() { NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.UnclaimedCase(), Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 0 }, Tick = 300 },
            Pawns = EquatableArray.From(batch.Orders.Select(o => new ActivityTaskObservation(o.Pawn,
                new(o.Assignment, o.Method, o.Revision, o.Preparing ? 1 : 0, o.Pawn == "producer" ? "started" : "holding", "", 300)))),
            Needs = NeedsFixtures.Empty with { Tick = new(300), Pawns = [NeedsFixtures.Pawn() with { Pawn = "helper", State = "eligible", Needs = [NeedsFixtures.Rate("rest", .05, .00002, .3)] }] }
        };
        var next = CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Owed, read)!;
        Assert.False(next.Orders.Single(o => o.Pawn == "producer").Release);
        Assert.True(next.Orders.Single(o => o.Pawn == "helper").Release);
    }

    private static PawnActivities Service(PawnActivityKind kind, long depart)
    {
        var service = new SelectedActivityTask("service", "pawn", [], new(kind, "target", "", "", kind == PawnActivityKind.Eat ? 1 : 0, 5000), new(3, 4), depart);
        return new(1, [new("pawn", "method/pawn", "method", 0, 1, false,
            [new PawnActivityStep(PawnActivityKind.Approach, "", "", "", 0, 5000) { NotBefore = depart }.At(service.Location),
             new(PawnActivityKind.Hold, "", "", "", 0, 5000)]) { Task = service.Id, Preparing = true }]) { Tasks = [service] };
    }

    [Theory]
    [InlineData(PawnActivityKind.Eat)]
    [InlineData(PawnActivityKind.Sleep)]
    [InlineData(PawnActivityKind.Recreation)]
    public void WaitingServiceReturnsToNativeEvenWhenAccessIsUnobserved(PawnActivityKind kind)
    {
        var batch = Service(kind, 700);
        var next = CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Owed, Read(batch, "holding", .08));
        Assert.True(Assert.Single(next!.Orders).Release);
    }

    [Fact]
    public void TiredParticipantWhoseServiceIsDueReturnsToNative()
    {
        var batch = Service(PawnActivityKind.Eat, 100);
        var task = batch.Tasks.Single();
        var next = CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Owed, Read(batch, "holding", .08) with { Bindings = [new ActivityTaskBinding.Bound(task.Id, task.Step)] })!;
        var order = Assert.Single(next.Orders);
        Assert.True(order.Release);
        Assert.Empty(order.Steps);
    }

    [Fact]
    public void HypotheticalLaterMealCannotExemptAWorkersCurrentNeedFromHandback()
    {
        // The old exact plan's later meal is not authority to keep a tired pawn harvesting.
        var play = new SelectedActivityTask("play", "pawn", [], new(PawnActivityKind.Recreation, "pin", "", "", 0, 5000), new(1, 1), 0);
        var harvest = new SelectedActivityTask("harvest", "pawn", [], new(PawnActivityKind.Work, "crop", "Harvest", "Harvest", 0, 5000), new(2, 2), 100);
        var haul = new SelectedActivityTask("haul", "pawn", [harvest.Id],
            new PawnActivityStep(PawnActivityKind.Deliver, "crop", "", "", 8, 5000).At(new(4, 4)), new(2, 2), 150) { Resource = "Rice", Source = new(2, 2) };
        var eat = new SelectedActivityTask("eat", "pawn", [haul.Id], new(PawnActivityKind.Eat, "observed-output", "", "", 8, 5000), new(4, 4), 200)
            { Resource = "Rice", Source = new(4, 4) };
        var batch = new PawnActivities(1, [new("pawn", "method/pawn", "method", 0, 1, false, [play.Step]) { Task = play.Id }])
            { Tasks = [play, harvest, haul, eat] };
        var next = CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Owed, Read(batch, "completed", .08) with { Bindings = [new ActivityTaskBinding.Bound(harvest.Id, harvest.Step)] })!;
        var order = Assert.Single(next.Orders);
        Assert.True(order.Release);
        Assert.Empty(order.Steps);
    }

    private static PawnActivities WokeBeforeItsNextService(long playDepart, long sleepEnd = 4000)
    {
        var sleep = new SelectedActivityTask("sleep", "pawn", [], new(PawnActivityKind.Sleep, "bed", "", "", 0, sleepEnd), new(1, 1), 0);
        var play = new SelectedActivityTask("play", "pawn", [], new(PawnActivityKind.Recreation, "pin", "", "", 0, 9000), new(2, 2), playDepart);
        return new(1, [new("pawn", "method/pawn", "method", 0, 1, false, [sleep.Step]) { Task = sleep.Id }]) { Tasks = [sleep, play] };
    }

    [Theory]
    [InlineData(4000)]
    [InlineData(150)]
    public void ParticipantIsReleasedRatherThanHeldForAFarService(long sleepEnd)
    {
        // Woken early (#3598) or with its sleep run to its planned end (#3649), long before the planned play: holding it would
        // idle it, owned and unreviewable.
        var batch = WokeBeforeItsNextService(5000, sleepEnd);
        var next = CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Owed, Read(batch, "completed", .95))!;
        Assert.True(Assert.Single(next.Orders).Release);
        Assert.Empty(next.Tasks);
    }

    [Theory]
    [InlineData(5000)]
    [InlineData(700)]
    public void AWorkerWhoseOwnMealDepartsLaterReturnsToNative(long eatDepart)
    {
        // #3649: a colonist that finished its work was held on "Waiting for the colony plan" for a meal most of a day ahead.
        var cut = new SelectedActivityTask("cut", "pawn", [], new(PawnActivityKind.Work, "brambles", "PlantsCut", "CutPlant", 1, 9000), new(1, 1), 0);
        var eat = new SelectedActivityTask("eat", "pawn", [], new(PawnActivityKind.Eat, "meal", "", "", 1, 9000), new(3, 3), eatDepart);
        var batch = new PawnActivities(1, [new("pawn", "method/pawn", "method", 0, 1, false, [cut.Step]) { Task = cut.Id }]) { Tasks = [cut, eat] };
        var order = Assert.Single(CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Owed, Read(batch, "completed", .9))!.Orders);
        Assert.True(order.Release);
        Assert.False(order.Preparing);
    }

    [Fact]
    public void AColonistHoldingForAFarMealNoOneWaitsOnIsReleased()
    {
        // #3649: its meal was kept at the start because another colonist's work waited on it; that work is gone now.
        var batch = Service(PawnActivityKind.Eat, 3000);
        var order = Assert.Single(CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Owed, Read(batch, "holding", .9))!.Orders);
        Assert.True(order.Release);
    }

    [Theory]
    [InlineData(5000)]
    [InlineData(500)]
    public void AnEaterWhoseMealWaitsOnTheCookIsReleasedNotHeld(long eatDepart)
    {
        // #3167: development-reassignment held both eaters ~9000 ticks for the cook's meals, owned and unreviewable.
        var cook = new SelectedActivityTask("cook", "cook", [], new(PawnActivityKind.Recipe, "stove", "", "", 0, 9000), new(2, 2), 0);
        var haul = new SelectedActivityTask("haul", "pawn", [], new PawnActivityStep(PawnActivityKind.Deliver, "rice", "", "", 8, 9000).At(new(2, 2)), new(1, 1), 0);
        var eat = new SelectedActivityTask("eat", "pawn", [cook.Id], new(PawnActivityKind.Eat, "observed-output", "", "", 1, 9000), new(3, 3), eatDepart);
        var batch = new PawnActivities(1, [new("cook", "method/cook", "method", 0, 1, false, [cook.Step]) { Task = cook.Id },
            new("pawn", "method/pawn", "method", 0, 1, false, [haul.Step]) { Task = haul.Id }]) { Tasks = [cook, haul, eat] };
        var read = Read(batch, "completed", .9) with { Pawns = [new("cook", new("method/cook", "method", 1, 0, "started", "", 200)),
            new("pawn", new("method/pawn", "method", 1, 0, "completed", "", 200))] };
        var next = CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Owed, read)!;
        var order = Assert.Single(next.Orders, o => o.Pawn == "pawn");
        Assert.True(order.Release);
        Assert.DoesNotContain(next.Tasks, t => t.Id == eat.Id);
        Assert.Contains(next.Tasks, t => t.Id == cook.Id);
    }

    [Theory]
    [InlineData(500, 4000)]
    [InlineData(500, 150)]
    public void ParticipantWhoseNextNativeServiceIsNearStillReturnsToVanilla(long playDepart, long sleepEnd)
    {
        var batch = WokeBeforeItsNextService(playDepart, sleepEnd);
        var order = Assert.Single(CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Owed, Read(batch, "completed", .95))!.Orders);
        Assert.True(order.Release);
        Assert.False(order.Preparing);
        Assert.Empty(order.Steps);
    }

    [Fact]
    public void ARunningTaskIsNeverInterruptedByAUrgentNeed()
    {
        var supply = ActivityContinuationFixtures.IngredientDelivery();
        Assert.Null(CoordinatedActivitiesRules.ContinueCoordinatedActivity(supply, ActivityNeed.Owed, Read(supply, "started", .08)));
    }
}
