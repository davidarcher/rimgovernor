using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Host;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>The in-process goals seam (#3492): a cached view, a validated editor and the wake of the goals planner.</summary>
public sealed class HostGoalsTests
{
    private static readonly RimGovernor.Brain.Store.World Home = new(new("colony"), new("load"), new(1));

    private static (HostGoals Goals, Journal Store, WakeSignal Wake) Open()
    {
        var store = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-hostgoals-" + Guid.NewGuid().ToString("N") + ".db"), TestContext.Current.CancellationToken);
        var wake = new WakeSignal();
        return (new HostGoals(store, () => Home, wake), store, wake);
    }

    private static GoalsRecord OneGoal() =>
        new GoalsRecord { World = Home }.WithNewGoal(new GoalRecord { Kind = GoalKind.ResearchProject, Target = "Smithing", Importance = Importance.High });

    [Fact]
    public async Task SaveIsVisibleOnTheNextViewAndInTheStoreAndWakesTheGoalsPlanner()
    {
        var ct = TestContext.Current.CancellationToken;
        var (goals, store, wake) = Open();
        using (store)
        {
            Assert.True(goals.Current().Record.IsEmpty);

            await goals.SaveAsync(OneGoal(), ct);

            Assert.Equal(OneGoal().Goals, goals.Current().Record.Goals);
            Assert.Equal(OneGoal().Goals, store.LoadGoals(Home, ct).Goals);
            Assert.Equal([HostGoals.PlannerName], wake.TakeInvalidated().Planners);
        }
    }

    [Fact]
    public async Task InvalidRecordIsRefusedAndLeavesTheStoredRecordUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var (goals, store, wake) = Open();
        using (store)
        {
            await goals.SaveAsync(OneGoal(), ct);
            _ = wake.TakeInvalidated();

            var bad = OneGoal() with { NextId = 0 };
            var refused = await Assert.ThrowsAsync<DomainException>(() => goals.SaveAsync(bad, ct));

            Assert.Contains("goals next id", refused.Message, StringComparison.Ordinal);
            Assert.Equal(OneGoal().Goals, goals.Current().Record.Goals);
            Assert.Equal(OneGoal().Goals, store.LoadGoals(Home, ct).Goals);
            Assert.Empty(wake.TakeInvalidated().Planners);
        }
    }

    [Fact]
    public void NamedPlannerIsMarkedDueAndAnUnknownNameIsIgnored()
    {
        var queue = new PlannerQueue { Catalog = () => ClockPlannerCatalog.Entries };
        var before = queue.Seq;

        queue.Wake(new StepReason { Cause = StepCause.Wake, Planners = ["no-such-planner"] });
        Assert.Equal(before, queue.Seq);

        queue.Wake(new StepReason { Cause = StepCause.Wake, Planners = [ClockScheduler.DefensePlanner] });
        Assert.Equal(before + 1, queue.Seq);
    }
}
