using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class NativeServiceContinuationTests
{
    public static TheoryData<string, bool, string> RemovalBoundaries
    {
        get
        {
            var data = new TheoryData<string, bool, string>();
            foreach (var job in new[] { "Deconstruct", "Uninstall", "CutPlant", "RemoveFloor", "Mine", "" })
                foreach (var dependent in new[] { false, true })
                    foreach (var id in new[] { "removal-0", "planned/room1/wall/WoodLog/clearance/composition/0" })
                        data.Add(job, dependent, id);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(RemovalBoundaries))]
    public void RoutineRemovalHandsBackAtBoundaryWithoutCompletingItsDependents(string job, bool dependent, string standing)
    {
        var (batch, read) = AtBoundary("food", true, target: false, dependent: dependent);
        batch = batch with
        {
            Tasks = EquatableArray.From(batch.Tasks.Select(t => t.Id == "service"
                ? t with { Id = standing, SiteDefinition = "Wall", Step = job.Length == 0
                    ? (t.Step with { Kind = PawnActivityKind.Deliver, Job = "", Count = 100 }).At(new(2, 2))
                    : t.Step with { Kind = PawnActivityKind.Work, Job = job } }
                : t with { Prerequisites = EquatableArray.From(t.Prerequisites.Select(p => p == "service" ? standing : p)) })),
            Orders = EquatableArray.From(batch.Orders.Select(o => o.Task == "service" ? o with { Task = standing } : o)).ToArray(),
        };
        Assert.Null(CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Unlisted, read with
        { Pawns = EquatableArray.From(read.Pawns.Select(p => p.Pawn == "pawn" ? p with { Execution = p.Execution! with { State = "started" } } : p)) }));
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
    [InlineData("completed")]
    [InlineData("failed")]
    public void OldPartialSetCannotCompleteACrossPawnPrerequisite(string state)
    {
        var (batch, read) = AtBoundary("food", true, target: false, dependent: true);
        var set = new PawnActivityStep(PawnActivityKind.Work, "tree", "PlantsCut", "CutPlant", 2, 2000)
            { Targets = ["tree", "other-tree"] };
        batch = batch with
        {
            Tasks = EquatableArray.From(batch.Tasks.Select(t => t.Id == "service" ? t with { Step = set } : t)),
            Orders = batch.Orders.Select(o => o.Pawn == "pawn"
                ? o with { Preparing = false, Steps = [set with { Count = 1, Targets = ["tree"] }] } : o).ToArray(),
        };
        read = read with { Pawns = EquatableArray.From(read.Pawns.Select(p => p.Pawn == "pawn"
            ? p with { Execution = p.Execution! with { State = state } } : p)) };
        var next = Assert.IsType<PawnActivities>(CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Owed, read));
        Assert.All(next.Orders, order => Assert.True(order.Release));
        Assert.Empty(next.Tasks);
        Assert.Empty(read.SatisfiedTasks);
    }

    [Theory]
    [InlineData("TendPatient", false)]
    [InlineData("DoBill", false)]
    [InlineData("", false)]
    [InlineData("TendPatient", true)]
    [InlineData("DoBill", true)]
    [InlineData("", true)]
    public void OldSupportCommitmentReturnsToNativeAtBoundaryWithoutCompletingDependencies(string job, bool dependent)
    {
        var (batch, read) = AtBoundary("food", true, target: false, dependent: dependent);
        var task = batch.Tasks.Single(t => t.Id == "service");
        task = task with { SupportDemand = "current-demand", Step = task.Step with
        {
            Kind = job.Length == 0 ? PawnActivityKind.Deliver : PawnActivityKind.Work,
            Job = job, WorkGiver = job.Length == 0 ? "" : "Doctor", Count = job.Length == 0 ? 1 : 0, SupportDemand = "current-demand",
            Recipe = job == "DoBill" ? new("surgery-bill", "operation", []) : null,
        } };
        if (job.Length == 0) task = task with { Step = task.Step.At(new(1, 1)) };
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
    public void UnobservedAccessDoesNotAuthorizeHoldingOrStartingAnExactFoodService()
    {
        var (batch, read) = AtBoundary("food", true, target: false);
        Assert.True(CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Owed, read)!.Orders[0].Release);
        // Without an execution census the cleanup itself still cannot claim ownership.
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
        Assert.True(CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Unlisted,
            read with { Needs = NeedsFixtures.Read(300) })!.Orders[0].Release);
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

    /// <summary>Task 5.2: a justified self-service order (the NeedShortfall exception) continues while it is the pawn's useful native
    /// service, ends on its own need, and a native job taking the pawn over is a replacement that releases it; either way the pawn
    /// returns to routine native work with nothing held. A meal, sleep or recreation order persisted before purposes (the deleted
    /// routine shape) answers nothing and is released, never continued.</summary>
    [Theory]
    [InlineData("food")]
    [InlineData("rest")]
    [InlineData("joy")]
    public void AJustifiedSelfServiceEndsOnItsNeedAndAnOldShapeOneIsReleased(string kind)
    {
        var service = new PawnActivityStep(kind == "food" ? PawnActivityKind.Eat
            : kind == "rest" ? PawnActivityKind.Sleep : PawnActivityKind.Recreation, "service", "", "", 1, 2000);
        var purpose = new InterventionPurpose(InterventionPurposeKind.NeedShortfall, kind, "pawn", 100);
        var order = new PawnActivityOrder("pawn", "coupled-self/pawn", "coupled-self", 0, 1, false, [service]) { Task = "service", Purpose = purpose };
        var batch = new PawnActivities(1, [order]) { Tasks = [new("service", "pawn", [], service, new(0, 0), 100)] };
        var target = kind == "food" ? NeedsFixtures.Food("service", "Rice", 10, 0, 10, .5) : NeedsFixtures.Service("service", kind, 0, .01);
        ActivityTaskRead Read(double level, string state) => ConstructionFixtures.NoTasks() with
        {
            Context = ConstructionFixtures.NoTasks().Context with { Tick = 300 },
            Needs = NeedsFixtures.Read(300, NeedsFixtures.Pawn("pawn") with { Needs = [NeedsFixtures.Rate(kind, level, .003, .3)], Targets = [target] }),
            Pawns = [new("pawn", new(order.Assignment, order.Method, order.Revision, 0, state, "", 300))],
        };
        PurposeCondition Condition(PawnActivities b, ActivityTaskRead read) => InterventionPurposes.Ended(b, p => InterventionPurposes.AtContinuation(p, b, read.Needs));
        static void Released(PawnActivities? next)
        {
            var release = Assert.Single(Assert.IsType<PawnActivities>(next).Orders);
            Assert.True(release.Release);
            Assert.Empty(release.Steps);
            Assert.Empty(next!.Tasks);
        }

        // Useful native service: the ordered service is running while the need stands, so the Method continues.
        var running = Read(0, "started");
        Assert.Equal(PurposeCondition.Open, Condition(batch, running));
        Assert.Equal(ActivityNeed.Owed, ForecastServiceInvestments.ReviewServiceActivityNeed(batch, ActivityNeed.Owed, running));
        Assert.Null(CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Owed, running));
        // The need answered ends the purpose; the pawn is released to routine work, holding nothing.
        var answered = Read(.9, "completed");
        Assert.Equal(PurposeCondition.Completed, Condition(batch, answered));
        Assert.Equal(ActivityNeed.Ended, ForecastServiceInvestments.ReviewServiceActivityNeed(batch, ActivityNeed.Owed, answered));
        Released(CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Ended, answered));
        // Interruption: a native job taking the pawn over replaces the order, and the review releases it.
        Released(ActivityContinuationRules.ReviewActivityOwnership(batch, ActivityNeed.Owed, 300,
            [new ActivityOwnership("pawn", order.Assignment, order.Method, order.Revision, "failed") { Reason = InterventionPurposes.NativeJobTakeover }]).Release);

        // The old routine shape: a valid persisted self-service order whose purpose is not its own need's shortfall (another
        // pawn's, another need's, or a food risk) answers nothing; it is cancelled and released even while running, and a
        // preparing order holding for such a service task is released alike.
        foreach (var carried in new[] { purpose with { SubjectPawn = "other" }, purpose with { Subject = kind == "food" ? "rest" : "food" },
            new InterventionPurpose(InterventionPurposeKind.FoodRisk, "food", "", 100) })
        {
            var old = batch with { Orders = [order with { Purpose = carried }] };
            old.Validate();
            Assert.Equal(PurposeCondition.Cancelled, Condition(old, running));
            Assert.Equal(ActivityNeed.Ended, ForecastServiceInvestments.ReviewServiceActivityNeed(old, ActivityNeed.Owed, running));
            Released(CoordinatedActivitiesRules.ContinueCoordinatedActivity(old, ActivityNeed.Ended, running));
        }
        var holding = batch with { Orders = [order with { Purpose = new InterventionPurpose(InterventionPurposeKind.FoodRisk, "food", "", 100),
            Steps = [new(PawnActivityKind.Hold, "", "", "", 0, 2000)], Preparing = true }] };
        Assert.Equal(PurposeCondition.Cancelled, Condition(holding, Read(0, "holding")));
    }
}
