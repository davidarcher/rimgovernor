using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class NativeServiceContinuationTests
{
    [Theory]
    [InlineData("", false)]
    [InlineData("FinishFrame", false)]
    [InlineData("BuildRoof", false)]
    [InlineData("", true)]
    [InlineData("FinishFrame", true)]
    [InlineData("BuildRoof", true)]
    public void OldConstructionCommitmentReturnsToNativeWithoutCreditingDependentCompletion(string job, bool dependent)
    {
        var (batch, read) = AtBoundary("food", true, target: false, dependent: dependent);
        var task = batch.Tasks.Single(t => t.Id == "service");
        task = task with { Step = task.Step with { Kind = job.Length == 0 ? PawnActivityKind.Supply : PawnActivityKind.Work,
            Job = job } };
        if (job == "BuildRoof") task = task with { Step = task.Step.At(task.Location) };
        batch = batch with { Tasks = EquatableArray.From(batch.Tasks.Select(t => t.Id == task.Id ? task : t)) };
        var next = Assert.IsType<PawnActivities>(CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Unlisted, read));
        Assert.True(next.Orders[0].Release);
        Assert.Empty(read.SatisfiedTasks);
        if (dependent)
        {
            Assert.All(next.Orders, order => Assert.True(order.Release));
            Assert.Empty(next.Tasks);
        }
        else
        {
            Assert.True(PawnActivities.SameOrder(batch.Orders[1], next.Orders[1]));
            Assert.Equal("independent", Assert.Single(next.Tasks).Id);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OldTailoringCommitmentReturnsToNativeAtBoundary(bool delivery)
    {
        var (batch, read) = AtBoundary("food", true, target: false);
        var task = batch.Tasks.Single(t => t.Id == "service");
        task = delivery ? task with { ProductStuff = "Cloth", Step = task.Step with { Kind = PawnActivityKind.Deliver } }
            : task with { Step = task.Step with { Kind = PawnActivityKind.Recipe,
                Recipe = new("bill", "MakeShirt", []) { Stuff = "Cloth" } } };
        batch = batch with { Tasks = EquatableArray.From(batch.Tasks.Select(t => t.Id == task.Id ? task : t)) };
        var next = Assert.IsType<PawnActivities>(CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Unlisted, read));
        Assert.True(next.Orders[0].Release);
        Assert.True(PawnActivities.SameOrder(batch.Orders[1], next.Orders[1]));
        Assert.Equal("independent", Assert.Single(next.Tasks).Id);
        Assert.Empty(read.SatisfiedTasks);
    }

    private static (PawnActivities Batch, ActivityTaskRead Read) AtBoundary(string kind, bool preparing,
        bool target = true, bool dependent = false)
    {
        var work = new PawnActivityStep(PawnActivityKind.Work, "site", "Build", "FinishFrame", 0, 2000);
        var service = new PawnActivityStep(kind == "food" ? PawnActivityKind.Eat
            : kind == "rest" ? PawnActivityKind.Sleep : PawnActivityKind.Recreation, "service", "", "", 1, 2000);
        var first = new PawnActivityOrder("pawn", "method/pawn", "method", 0, 1, false,
            [preparing ? new(PawnActivityKind.Hold, "", "", "", 0, 2000) : work])
            { Task = preparing ? "service" : "before", Preparing = preparing };
        var other = new PawnActivityOrder("helper", "method/helper", "method", 0, 1, false, [work]) { Task = "independent" };
        var batch = new PawnActivities(1, [first, other]) { Tasks = [
            .. preparing ? Array.Empty<SelectedActivityTask>() : [new("before", "pawn", [], work, new(0, 0), 100)],
            new("service", "pawn", [], service, new(0, 0), 400),
            new("after", "pawn", ["service"], work, new(0, 0), 500),
            new("independent", "helper", [], work, new(0, 0), 100),
            .. dependent ? new SelectedActivityTask[] { new("dependent", "helper", ["after"], work, new(0, 0), 600) } : []] };
        var nativeTarget = kind == "food" ? NeedsFixtures.Food("service", "Rice", 10, 0, 10, .5)
            : NeedsFixtures.Service("service", kind, 0, .01);
        var read = ConstructionFixtures.NoTasks() with
        {
            Context = ConstructionFixtures.NoTasks().Context with { Tick = 300 },
            Needs = NeedsFixtures.Read(300, NeedsFixtures.Pawn("pawn") with { Targets = target ? [nativeTarget] : [] }),
            Pawns = [new("pawn", new(first.Assignment, first.Method, first.Revision, 0, preparing ? "holding" : "completed", "", 300)),
                new("helper", new(other.Assignment, other.Method, other.Revision, 0, "started", "", 300))],
            Bindings = [new ActivityTaskBinding.Bound("service", service)]
        };
        return (batch, read);
    }

    [Theory]
    [InlineData("food", false)]
    [InlineData("food", true)]
    [InlineData("rest", false)]
    [InlineData("rest", true)]
    [InlineData("joy", false)]
    [InlineData("joy", true)]
    public void NativeServiceReleasesAtBoundaryWithoutHoldingFutureWorkOrRestartingIndependentWork(string kind, bool preparing)
    {
        var (batch, read) = AtBoundary(kind, preparing);
        var next = Assert.IsType<PawnActivities>(CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Owed, read));
        var released = next.Orders.Single(o => o.Pawn == "pawn");
        Assert.True(released.Release);
        Assert.Equal(1, released.ExpectedRevision);
        Assert.Equal(2, released.Revision);
        Assert.Empty(released.Steps);
        Assert.True(PawnActivities.SameOrder(batch.Orders[1], next.Orders[1]));
        Assert.Equal("independent", Assert.Single(next.Tasks).Id);

        // The cleanup cannot restart the service, and is not evidence of an intake or completed work.
        Assert.Null(CoordinatedActivitiesRules.ContinueCoordinatedActivity(next, ActivityNeed.Owed, read));
        Assert.Empty(read.SatisfiedTasks);
    }

    [Theory]
    [InlineData("rest")]
    [InlineData("joy")]
    public void NoCapturedObjectDoesNotKeepAnOrdinaryServiceCommitment(string kind)
    {
        var (batch, read) = AtBoundary(kind, true, target: false);
        Assert.True(CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Owed, read)!.Orders[0].Release);
    }

    [Fact]
    public void UnobservedAccessCannotBeReclassifiedAsUsefulNativeFoodService()
    {
        var (batch, read) = AtBoundary("food", true, target: false);
        Assert.Null(CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Owed, read));
        Assert.Null(CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Owed, read with { Pawns = [] }));
    }

    [Fact]
    public void RunningWorkFinishesBeforeTheNativeServiceBoundary()
    {
        var (batch, read) = AtBoundary("food", false);
        read = read with { Pawns = EquatableArray.From(read.Pawns.Select(p => p.Pawn == "pawn"
            ? p with { Execution = p.Execution! with { State = "started" } } : p)) };
        Assert.Null(CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Owed, read));
    }

    [Theory]
    [InlineData("food", false)]
    [InlineData("food", true)]
    [InlineData("rest", false)]
    [InlineData("rest", true)]
    [InlineData("joy", false)]
    [InlineData("joy", true)]
    public void UnlistedDemandStillAllowsNativeHandbackButCannotStartUnassessedService(string kind, bool preparing)
    {
        var (batch, read) = AtBoundary(kind, preparing);
        Assert.True(CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Unlisted, read)!.Orders[0].Release);
        var service = batch.Tasks.Single(t => t.Id == "service") with { DepartTick = 100 };
        batch = batch with { Tasks = EquatableArray.From(batch.Tasks.Select(t => t.Id == service.Id ? service : t)) };
        Assert.Null(CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Unlisted,
            read with { Needs = NeedsFixtures.Read(300) }));
    }

    [Fact]
    public void DependentAttemptEndsWithoutTreatingNativeServiceAsACompletedPrerequisite()
    {
        var (batch, read) = AtBoundary("food", true, dependent: true);
        var next = Assert.IsType<PawnActivities>(CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Owed, read));
        Assert.Equal(2, next.Orders.Length);
        Assert.All(next.Orders, o => Assert.True(o.Release));
        Assert.Empty(next.Tasks);
        Assert.Empty(read.SatisfiedTasks);
    }
}
