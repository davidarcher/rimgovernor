using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Policy.Methods;
using RimGovernor.Brain.Policy.Methods.Binding;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Whom a schedule orders at once (CoordinatedActivityBinding.OrderedAtStart, #3884): StartSelected installs exactly
/// those pawns' orders.</summary>
public sealed class DistantServiceDeferralTests
{
    private const long Tick = 100;
    private static ActivityTaskCandidate Service(string pawn, PawnActivityKind kind = PawnActivityKind.Sleep, params string[] prerequisites) =>
        new("service/" + pawn, pawn, EquatableArray.From(prerequisites), kind, "bed", "", "", kind == PawnActivityKind.Eat ? 1 : 0, 90000, new(3, 4));
    private static ActivityTaskCandidate Work(string pawn, params string[] prerequisites) =>
        new("work/" + pawn, pawn, EquatableArray.From(prerequisites), PawnActivityKind.Work, "site", "", "FinishFrame", 0, 90000, new(1, 1));
    /// <summary>The task's schedule row: departing at <paramref name="depart"/> with no travel, starting at <paramref name="start"/>.</summary>
    private static ForecastActivityAllocation Row(ActivityTaskCandidate task, double depart, double? start = null) =>
        new(task.Id, task.Pawn, depart, depart, start ?? depart, (start ?? depart) + 2000, 0, (start ?? depart) - depart, 2000, 0, 0);
    private static PawnActivities? Started(ActivityTaskCandidate[] tasks, params ForecastActivityAllocation[] rows) =>
        CoordinatedActivityBinding.StartSelected(new(["selected"], new(rows, 0, 0), Known(0d), [], 0, false),
            tasks.ToDictionary(t => (t.Id, t.Pawn)), 1, "m", Tick);
    private static IReadOnlySet<string> Ordered(ActivityTaskCandidate[] tasks, params ForecastActivityAllocation[] rows) =>
        CoordinatedActivityBinding.OrderedAtStart(["selected"], rows, tasks.ToDictionary(t => (t.Id, t.Pawn)), new HashSet<string>());

    [Fact]
    public void ADistantServiceReleasesItsPawnInsteadOfHoldingTheColonyForIt()
    {
        var (sleep, work) = (Service("sleeper"), Work("worker"));
        var active = Started([sleep, work], Row(sleep, 30000), Row(work, 0))!;
        Assert.Equal("worker", Assert.Single(active.Orders).Pawn);
        Assert.Equal(work.Id, Assert.Single(active.Tasks).Id);
        Assert.Equal("worker", Assert.Single(Ordered([sleep, work], Row(sleep, 30000), Row(work, 0))));
    }

    [Fact]
    public void ADistantMealReleasesItsPawnInsteadOfHoldingItUntilMealtime()
    {
        // Live playtest: a coupled food Method held four colonists on "Waiting for the colony plan" for ~20k ticks until their meals.
        var meal = Service("eater", PawnActivityKind.Eat);
        Assert.Null(Started([meal], Row(meal, 30000)));
    }

    [Fact]
    public void ADistantMealWaitingOnAnotherPawnsCookingReleasesTheEaterAndKeepsTheCook()
    {
        // #3167: development-reassignment held both eaters ~9000 ticks for the cook's meals, so no colonist was ever free.
        var cook = Work("cook");
        // The scheduler departed the eater at once to wait at the table: the distance is its planned start, not its departure.
        var meal = Service("eater", PawnActivityKind.Eat, cook.Id);
        Assert.Equal(2, Started([cook, meal], Row(cook, 0), Row(meal, 0, 300))!.Orders.Length);
        var active = Started([cook, meal], Row(cook, 0), Row(meal, 0, 9000))!;
        Assert.Equal("cook", Assert.Single(active.Orders).Pawn);
        Assert.Equal(cook.Id, Assert.Single(active.Tasks).Id);
    }

    [Fact]
    public void ADistantServiceAloneLeavesNothing()
    {
        var sleep = Service("sleeper");
        Assert.Null(Started([sleep], Row(sleep, 30000)));
        Assert.Empty(Ordered([sleep], Row(sleep, 30000)));
    }

    [Fact]
    public void ADueServiceKeepsItsPreparationAndSoDoesOneOthersWaitOn()
    {
        var sleep = Service("sleeper");
        Assert.True(Assert.Single(Started([sleep], Row(sleep, 600))!.Orders).Preparing);
        var dependent = Work("worker", sleep.Id);
        Assert.Equal(2, Started([sleep, dependent], Row(sleep, 30000), Row(dependent, 30000))!.Orders.Length);
    }

    /// <summary>A service the pawn departs for now with no prerequisite is its order now, however late it starts: nothing is
    /// prepared, so nothing is deferred.</summary>
    [Fact]
    public void AServiceStartedNowIsOrderedHoweverLateItsStart()
    {
        var sleep = Service("sleeper");
        Assert.False(Assert.Single(Started([sleep], Row(sleep, 0, 9000))!.Orders).Preparing);
        Assert.Equal("sleeper", Assert.Single(Ordered([sleep], Row(sleep, 0, 9000))));
    }

    /// <summary>Start orders no one when a placed allocation has no binding (#3884), so neither does the rule.</summary>
    [Fact]
    public void AnUnboundAllocationOrdersNoOne()
    {
        var (work, other) = (Work("worker"), Work("other"));
        Assert.Null(Started([work], Row(work, 0), Row(other, 0)));
        Assert.Empty(Ordered([work], Row(work, 0), Row(other, 0)));
    }
}
