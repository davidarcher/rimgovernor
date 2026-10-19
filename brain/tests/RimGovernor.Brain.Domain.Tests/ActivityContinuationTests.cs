using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Bridge.Contracts;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ActivityContinuationTests
{
    private static PawnActivityOrder Order => new("pawn", "assignment", "method", 0, 1, false,
        [new(PawnActivityKind.Hold, "", "", "", 0, 500)]);
    private static PawnActivities Batch => new(1, [Order]);
    private static ActivityOwnership Row(string state = "running") => new("pawn", "assignment", "method", 1, state);
    private static GenerationSnapshot Root => new(new("colony"), new(1), new("load"), new("root"), new(1), new(1));
    private static PlanSpec Plan(string id, PawnActivities batch) => new(new(id), new(1),
        [DomainAction.NewPawnActivitiesAction(new(id + "-action"), batch)]);
    /// <summary>Admits <paramref name="plan"/> under the test Need, filed unmet so a productive step continues (#3265).</summary>
    private static NeedOwner Admit(Journal journal, PlanSpec plan, CancellationToken ct, Receipt receipt = Receipt.Accepted)
    {
        IntentActionRules.RegisterIntentKinds();
        _ = journal.ReviewRounds(new RoundsRequest { Current = Root, Tick = new(100), Enabled = true,
            Policy = RoundsRules.DefaultRoundsPolicy(), Facts = WorkResourceTestFacts.Create("pawn", "producer", "helper") }, ct);
        var owner = TestNeed.Commit(journal, Root, new("method"), plan, ct);
        TestNeed.File(journal, met: false, new("method"), ct);
        var scope = Root with { Plan = plan.Id };
        journal.Prepare(plan.Id, plan.Actions[0].Id, scope, new(100), ct);
        var dispatched = journal.Dispatch(plan.Id, plan.Actions[0].Id, scope, new(100), ct);
        journal.RecordReceipt(plan.Id, plan.Actions[0].Id, dispatched.View.Attempt, receipt, ct);
        return owner;
    }
    private static Journal Open(CancellationToken ct) => Journal.Open(Path.Combine(Path.GetTempPath(),
        "rg-continuation-" + Guid.NewGuid().ToString("N") + ".db"), ct);

    /// <summary>#3624: a coupled worker read completed while a recipe is left is one finished task, keyed by its order, so a
    /// continuation deferred at that boundary and read again is the same event; none when no recipe is left or mid-task.</summary>
    [Fact]
    public void ACoupledWorkerThatFinishedATaskWithBillsLeftIsOneFinishedTask()
    {
        var recipe = new PawnActivityStep(PawnActivityKind.Recipe, "bench", "Cook", "", 0, 2000) { Recipe = new("bill/CookRice", "CookRice", []) };
        var first = new SelectedActivityTask("cook-1", "pawn", [], recipe, new(2, 1), 0);
        var order = new PawnActivityOrder("pawn", "assignment", "coupled-cook", 0, 1, false, [recipe]) { Task = first.Id };
        var batch = new PawnActivities(1, [order]) { Tasks = [first, first with { Id = "cook-2" }] };
        var completed = new ActivityOwnership("pawn", "assignment", "coupled-cook", 1, "completed");
        Assert.Equal(["coupled-cook/pawn/1"], ActivityContinuationRules.FinishedTasks(batch, [completed]));
        Assert.Empty(ActivityContinuationRules.FinishedTasks(batch, [completed with { State = "started" }]));
        Assert.Empty(ActivityContinuationRules.FinishedTasks(batch with { Tasks = [first] }, [completed]));
        Assert.Empty(ActivityContinuationRules.FinishedTasks(batch, [completed with { Revision = 2 }]));
    }

    [Fact]
    public void TerminalHoldCleanupDoesNotReadmitDeliveryAgainstAnotherMethodsResidualMeal()
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Open(ct);
        var delivery = new SelectedActivityTask("delivery", "helper", [],
            new(PawnActivityKind.Deliver, "observed-output", "", "", 6, 500) { Destination = new(2, 2) },
            new(1, 1), 100)
        {
            Resource = "Rice", Source = new(1, 1),
            Inputs = [new("Rice", "harvest", 6, new(1, 1), "observed harvest") { Producers = ["completed-harvest"] }]
        };
        var waiting = Order with { Pawn = "helper", Assignment = "helper-assignment", Task = delivery.Id, Preparing = true };
        var idleStep = new PawnActivityStep(PawnActivityKind.Hold, "", "", "", 0, 110);
        var idle = Order with { Steps = [idleStep], Task = "idle" };
        var batch = new PawnActivities(1, [waiting, idle])
        { Tasks = [delivery, new("idle", "pawn", [], idleStep, new(3, 3), 100)] };
        var initial = Plan("cleanup-original", batch);
        Admit(journal, initial, ct);
        var meal = new PawnActivities(1, [new("producer", "meal-assignment", "meal", 0, 1, false,
            [new(PawnActivityKind.Eat, "rice-stack", "", "", 1, 500)])
            { Task = "meal" }])
        { Tasks = [new("meal", "producer", [], new(PawnActivityKind.Eat, "rice-stack", "", "", 1, 500), new(1, 1), 100)
            { Inputs = [new("Rice", "rice-stack", 1, new(1, 1), "observed rice") { Observed = true }] }] };
        TestNeed.Commit(journal, Root, new("meal"), Plan("residual-meal", meal), ct);
        var observed = new[] { Row("completed"), new ActivityOwnership("helper", waiting.Assignment, "method", 1, "holding") };
        var bound = waiting with { ExpectedRevision = 1, Revision = 2, Preparing = false,
            Steps = [delivery.Step with { Target = "rice-stack" }] };
        var released = idle with { ExpectedRevision = 1, Revision = 2, Release = true, Task = "", Steps = [] };
        var mixed = Plan("mixed", batch with { Orders = [bound, released], Tasks = [delivery] });
        Assert.Equal(ActivityContinuationAdmission.ResourcesUnavailable,
            journal.ContinueActivityMethod(initial.Id, Root, new(110), observed, mixed, ct));
        Assert.False(journal.LoadPlan(initial.Id, ct).Retired);
        Assert.DoesNotContain(journal.LoadNeed(TestNeed.Id, Root, ct).Methods, m => m.Plan == mixed.Id);
        var cleanup = Plan("cleanup", batch with { Orders = [waiting, released], Tasks = [delivery] });
        Assert.Equal(ActivityContinuationAdmission.Admitted,
            journal.ContinueActivityMethod(initial.Id, Root, new(110), observed, cleanup, ct));
        var persisted = journal.LoadPlan(cleanup.Id, ct).Spec.Actions[0].PawnActivities!;
        Assert.True(PawnActivities.SameOrder(waiting, persisted.Orders[0]));
        Assert.Equal(delivery, Assert.Single(persisted.Tasks));
        Assert.False(journal.LoadPlan(new("residual-meal"), ct).Retired);
    }
    [Fact]
    public void PartialParticipantRevisionRetainsRunningOwnerAndRejectsStaleEvidenceAtomically()
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Open(ct);
        var helper = Order with { Pawn = "helper", Assignment = "helper-assignment" };
        var batch = Batch with { Orders = [Order, helper] };
        var initial = Plan("partial-initial", batch);
        Admit(journal, initial, ct);
        var release = Order with { ExpectedRevision = 1, Revision = 2, Release = true, Steps = [] };
        var next = Plan("partial-next", batch with { Orders = [release, helper] });
        var helperRead = new ActivityOwnership("helper", helper.Assignment, helper.Method, helper.Revision, "started");
        Assert.Throws<ConflictException>(() => journal.ContinueActivityMethod(initial.Id, Root, new(110),
            [Row("completed"), helperRead with { Revision = 2 }], next, ct));
        Assert.False(journal.LoadPlan(initial.Id, ct).Retired);
        journal.ContinueActivityMethod(initial.Id, Root, new(110), [Row("completed"), helperRead], next, ct);
        var persisted = journal.LoadPlan(next.Id, ct).Spec.Actions.Single().PawnActivities!;
        Assert.True(PawnActivities.SameOrder(helper, persisted.Orders.Single(o => o.Pawn == "helper")));
        Assert.Contains("helper", journal.WorkResources(Root, ct).Claims.Pawns);
    }

    [Fact]
    public void RecoveryReleasesButStableOrUnknownNeedDoesNotChurnUsefulWork()
    {
        Assert.Null(ActivityContinuationRules.ReviewActivityOwnership(Batch, Known(true), 110, [Row()]).Release);
        Assert.Null(ActivityContinuationRules.ReviewActivityOwnership(Batch, default, 110, [Row()]).Release);
        var release = Assert.Single(ActivityContinuationRules.ReviewActivityOwnership(Batch, Known(false), 110, [Row()]).Release!.Orders);
        Assert.True(release.Release);
        Assert.Equal(1, release.ExpectedRevision);
        Assert.Equal(2, release.Revision);
        Assert.Empty(release.Steps);
        Assert.Null(ActivityContinuationRules.ReviewActivityOwnership(Batch, Known(false), 110, []).Release);
        Assert.NotNull(ActivityContinuationRules.ReviewActivityOwnership(Batch, default, 110, [Row("failed")]).Release);
        Assert.NotNull(ActivityContinuationRules.ReviewActivityOwnership(Batch, Known(true), 500, [Row("completed")]).Release);
    }

    [Fact]
    public void LostParticipantDoesNotAuthorizeReleasingItsNewOwner()
    {
        var batch = Batch with { Orders = [Order, Order with { Pawn = "helper", Assignment = "helper-assignment" }] };
        var rows = new[] { Row(), new ActivityOwnership("helper", "player-or-other-method", "other", 9, "running") };
        var result = ActivityContinuationRules.ReviewActivityOwnership(batch, Known(false), 110, rows);
        Assert.Equal("pawn", Assert.Single(result.Release!.Orders).Pawn);
        Assert.True(ActivityContinuationRules.ReviewActivityOwnership(Batch, Known(false), 110, [Row() with { Revision = 2 }]).OwnershipEnded);
    }

    [Fact]
    public void ContinuationRequiresTerminalReadbackAndCannotReplaceNewerOrUncertainOwnership()
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Open(ct);
        var initial = Plan("initial", Batch);
        Admit(journal, initial, ct);
        var next = Plan("next", Batch with { Orders = [Order with { ExpectedRevision = 1, Revision = 2,
            Steps = [new(PawnActivityKind.Deliver, "fresh-observed-stack", "", "", 2, 600) { Destination = new Cell(3, 4) }] }] });
        Assert.Throws<ConflictException>(() => journal.ContinueActivityMethod(initial.Id, Root, new(110), [Row()], next, ct));
        Assert.Throws<ConflictException>(() => journal.ContinueActivityMethod(initial.Id, Root, new(110), [Row("completed") with { Revision = 2 }], next, ct));
        Assert.Throws<ConflictException>(() => journal.ContinueActivityMethod(initial.Id, Root with { Load = new("other") }, new(110), [Row("completed")], next, ct));
        Assert.Throws<ConflictException>(() => journal.ContinueActivityMethod(initial.Id, Root with { Native = new(2) }, new(110), [Row("completed")], next, ct));
        Assert.Throws<ConflictException>(() => journal.ContinueActivityMethod(initial.Id, Root, new(99), [Row("completed")], next, ct));
        journal.ContinueActivityMethod(initial.Id, Root, new(110), [Row("completed")], next, ct);
        Assert.Equal("fresh-observed-stack", journal.LoadPlan(next.Id, ct).Spec.Actions[0].PawnActivities!.Orders[0].Steps[0].Target);
        using var uncertain = Open(ct);
        Admit(uncertain, initial, ct, Receipt.Unknown);
        Assert.Empty(uncertain.ActivityMethods(Root, ct));
        Assert.Throws<ConflictException>(() => uncertain.ContinueActivityMethod(initial.Id, Root, new(110), [Row("completed")], next, ct));
    }

    /// <summary>
    /// A coupled activity committed under the admin/work-assignments Need (#3304, decision 13) continues under the same
    /// MethodId: its need_methods row moves to the next plan. It is needed while the Need's progress row says it is
    /// unmet, unknown without a row (never a release on absence), and a met Need permits release only.
    /// </summary>
    [Fact]
    public void NeedOwnedActivityContinuesUnderItsMethodWhileTheNeedIsUnmet()
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Open(ct);
        IntentActionRules.RegisterIntentKinds();
        _ = journal.ReviewRounds(new RoundsRequest { Current = Root, Tick = new(100), Enabled = true,
            Policy = RoundsRules.DefaultRoundsPolicy(), Facts = WorkResourceTestFacts.Create("pawn", "producer", "helper") }, ct);
        var need = AdminFamily.WorkAssignments;
        var initial = Plan("need-initial", Batch);
        Assert.Equal(ClaimOutcome.Admitted, journal.CommitClaimedMethod(need, Root, new("method"), "", initial, ct).Outcome);
        var scope = Root with { Plan = initial.Id };
        journal.Prepare(initial.Id, initial.Actions[0].Id, scope, new(100), ct);
        var dispatched = journal.Dispatch(initial.Id, initial.Actions[0].Id, scope, new(100), ct);
        journal.RecordReceipt(initial.Id, initial.Actions[0].Id, dispatched.View.Attempt, Receipt.Accepted, ct);

        // No progress row yet: unknown, so the activity is neither released nor productively continued.
        Assert.False(Assert.Single(journal.ActivityMethods(Root, ct)).Needed.IsKnown);
        var next = Plan("need-next", Batch with { Orders = [Order with { ExpectedRevision = 1, Revision = 2,
            Steps = [new(PawnActivityKind.Deliver, "fresh-observed-stack", "", "", 2, 600) { Destination = new Cell(3, 4) }] }] });
        Assert.Throws<ConflictException>(() => journal.ContinueActivityMethod(initial.Id, Root, new(110), [Row("completed")], next, ct));

        void File(bool met) => journal.RecordPlannerReasons(new Dictionary<ConcernId, PlannerNote>(),
            [new FamilyProgress(AdminFamily.FamilyId, true, met ? [] : [new NeedProgress(need, false, default, new("method"))])], ct);
        File(met: false);
        Assert.True(Assert.Single(journal.ActivityMethods(Root, ct)).Needed.Value);
        Assert.Equal(ActivityContinuationAdmission.Admitted,
            journal.ContinueActivityMethod(initial.Id, Root, new(110), [Row("completed")], next, ct));
        var bound = Assert.Single(journal.LoadNeed(need, Root, ct).Methods);
        Assert.Equal(new MethodId("method"), bound.Method);
        Assert.Equal(next.Id, bound.Plan);
        Assert.Equal(next.Id, journal.LoadPlan(initial.Id, ct).SupersededBy);

        // Met: the row is pruned, so a productive step is refused while a release still goes through.
        File(met: true);
        var nextScope = Root with { Plan = next.Id };
        journal.Prepare(next.Id, next.Actions[0].Id, nextScope, new(110), ct);
        var sent = journal.Dispatch(next.Id, next.Actions[0].Id, nextScope, new(110), ct);
        journal.RecordReceipt(next.Id, next.Actions[0].Id, sent.View.Attempt, Receipt.Accepted, ct);
        var running = new ActivityOwnership("pawn", "assignment", "method", 2, "completed");
        var further = Plan("need-further", Batch with { Orders = [Order with { ExpectedRevision = 2, Revision = 3,
            Steps = [new(PawnActivityKind.Hold, "", "", "", 0, 900)] }] });
        Assert.Throws<ConflictException>(() => journal.ContinueActivityMethod(next.Id, Root, new(120), [running], further, ct));
        var release = Plan("need-release", Batch with { Orders = [Order with { ExpectedRevision = 2, Revision = 3, Release = true, Steps = [] }] });
        Assert.Equal(ActivityContinuationAdmission.Admitted,
            journal.ContinueActivityMethod(next.Id, Root, new(120), [running], release, ct));
        Assert.Equal(release.Id, Assert.Single(journal.LoadNeed(need, Root, ct).Methods).Plan);
    }

    [Fact]
    public void KnownOwnershipEndRetiresIntentWithoutTouchingAnotherAssignment()
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Open(ct);
        var initial = Plan("initial", Batch);
        Admit(journal, initial, ct);
        Assert.Throws<ConflictException>(() => journal.ResolveActivityMethod(initial.Id, Root, new(110), [], ct));
        journal.ResolveActivityMethod(initial.Id, Root, new(110), [new("pawn", "", "", 0, "unowned")], ct);
        Assert.True(journal.LoadPlan(initial.Id, ct).Retired);
    }
}
