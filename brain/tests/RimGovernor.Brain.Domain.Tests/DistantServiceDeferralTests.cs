using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Policy.Methods.Binding;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class DistantServiceDeferralTests
{
    private const long Tick = 100;
    private static SelectedActivityTask Service(string pawn, long depart, params string[] prerequisites) =>
        new("service/" + pawn, pawn, EquatableArray.From(prerequisites), new(PawnActivityKind.Sleep, "bed", "", "", 0, depart + 2000), new(3, 4), depart);
    private static SelectedActivityTask Work(string pawn, long depart = Tick) =>
        new("work/" + pawn, pawn, [], new(PawnActivityKind.Work, "site", "", "FinishFrame", 0, depart + 500), new(1, 1), depart);
    private static PawnActivityOrder Preparing(SelectedActivityTask task) =>
        new(task.Pawn, "m/" + task.Pawn, "m", 0, 1, false,
            [new(PawnActivityKind.Approach, "", "", "", 0, task.Step.Deadline) { Destination = task.Location, NotBefore = task.DepartTick },
             new(PawnActivityKind.Hold, "", "", "", 0, task.Step.Deadline)]) { Task = task.Id, Preparing = true };
    private static PawnActivityOrder Active(SelectedActivityTask task) =>
        new(task.Pawn, "m/" + task.Pawn, "m", 0, 1, false, [task.Step]) { Task = task.Id };
    private static PawnActivities Batch(PawnActivityOrder[] orders, params SelectedActivityTask[] tasks) =>
        new(1, orders) { Tasks = EquatableArray.From(tasks) };

    [Fact]
    public void ADistantServiceDefersItsPawnInsteadOfHoldingTheColonyForIt()
    {
        var sleep = Service("sleeper", 30000); var work = Work("worker");
        var batch = Batch([Preparing(sleep), Active(work)], sleep, work);
        var deferred = new List<ForecastDeferredPawn>();
        var active = CoordinatedActivityBinding.DeferDistantServices(batch, Tick, deferred)!;
        Assert.Equal("worker", Assert.Single(active.Orders).Pawn);
        Assert.Equal(work, Assert.Single(active.Tasks));
        var pawn = Assert.Single(deferred);
        Assert.Equal(new ForecastDeferredPawn("sleeper", new(3, 4), Tick + 600), pawn);
    }

    [Fact]
    public void ADistantMealDefersItsPawnInsteadOfHoldingItUntilMealtime()
    {
        // Live playtest: a coupled food Method held four colonists on "Waiting for the colony plan" for ~20k ticks until their meals.
        var meal = Service("eater", 30000) with { Step = new(PawnActivityKind.Eat, "meal", "", "", 1, 32000) };
        var deferred = new List<ForecastDeferredPawn>();
        Assert.Null(CoordinatedActivityBinding.DeferDistantServices(Batch([Preparing(meal)], meal), Tick, deferred));
        Assert.Equal("eater", Assert.Single(deferred).Pawn);
    }

    [Fact]
    public void ADistantMealWaitingOnAnotherPawnsCookingDefersTheEaterAndKeepsTheCook()
    {
        // #3167: development-reassignment held both eaters ~9000 ticks for the cook's meals, so no colonist was ever free.
        var cook = Work("cook") with { Step = new(PawnActivityKind.Recipe, "stove", "", "", 0, Tick + 500) };
        // The scheduler departed the eater at once to wait at the table: the distance is its planned start, not its departure.
        var meal = Service("eater", Tick, cook.Id) with { Step = new(PawnActivityKind.Eat, "meal", "", "", 1, 11000) };
        var deferred = new List<ForecastDeferredPawn>();
        var batch = Batch([Active(cook), Preparing(meal)], cook, meal);
        Assert.Same(batch, CoordinatedActivityBinding.DeferDistantServices(batch, Tick, deferred));
        var active = CoordinatedActivityBinding.DeferDistantServices(batch, Tick, deferred, new Dictionary<string, long> { [meal.Id] = 9000 })!;
        Assert.Equal("cook", Assert.Single(active.Orders).Pawn);
        Assert.Equal(cook, Assert.Single(active.Tasks));
        Assert.Equal("eater", Assert.Single(deferred).Pawn);
    }

    [Fact]
    public void ADistantServiceAloneLeavesOnlyTheDeferral()
    {
        var sleep = Service("sleeper", 30000);
        var deferred = new List<ForecastDeferredPawn>();
        Assert.Null(CoordinatedActivityBinding.DeferDistantServices(Batch([Preparing(sleep)], sleep), Tick, deferred));
        Assert.Single(deferred);
    }

    [Fact]
    public void ADueServiceKeepsItsPreparationAndSoDoesOneOthersWaitOn()
    {
        var due = Service("sleeper", Tick + 600);
        var deferred = new List<ForecastDeferredPawn>();
        var batch = Batch([Preparing(due)], due);
        Assert.Same(batch, CoordinatedActivityBinding.DeferDistantServices(batch, Tick, deferred));
        var sleep = Service("sleeper", 30000);
        var dependent = Work("worker", 30000) with { Prerequisites = [sleep.Id] };
        batch = Batch([Preparing(sleep), Preparing(dependent)], sleep, dependent);
        Assert.Same(batch, CoordinatedActivityBinding.DeferDistantServices(batch, Tick, deferred));
        Assert.Empty(deferred);
    }
}
