using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Bridge.Contracts;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;
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
    private static StandardState Admit(Journal journal, PlanSpec plan, CancellationToken ct, Receipt receipt = Receipt.Accepted)
    {
        BridgeFunctions.RegisterIntentKinds();
        var review = journal.ReviewRounds(new RoundsRequest { Current = Root, Tick = new(100), Enabled = true,
            Policy = DefaultRoundsPolicy(), Facts = WorkResourceTestFacts.Create("pawn", "producer", "helper") }, ct);
        var owner = Assert.Single(review.Standards, s => s.Standard.Kind == MaintainMechs);
        owner = journal.CommitMethod(owner.Standard.Id, owner.Revision, new("method"), plan, ct);
        var scope = Root with { Plan = plan.Id };
        journal.Prepare(plan.Id, plan.Actions[0].Id, scope, new(100), ct);
        var dispatched = journal.Dispatch(plan.Id, plan.Actions[0].Id, scope, new(100), ct);
        journal.RecordReceipt(plan.Id, plan.Actions[0].Id, dispatched.View.Attempt, receipt, ct);
        return owner;
    }
    private static Journal Open(CancellationToken ct) => Journal.Open(Path.Combine(Path.GetTempPath(),
        "rg-continuation-" + Guid.NewGuid().ToString("N") + ".db"), ct);

    [Fact]
    public void TerminalIdleCleanupDoesNotReadmitDeliveryAgainstAnotherMethodsResidualMeal()
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
        var idleStep = new PawnActivityStep(PawnActivityKind.Idle, "", "", "", 0, 110);
        var idle = Order with { Steps = [idleStep], Task = "idle" };
        var batch = new PawnActivities(1, [waiting, idle])
        { Tasks = [delivery, new("idle", "pawn", [], idleStep, new(3, 3), 100)] };
        var initial = Plan("cleanup-original", batch);
        var owner = Admit(journal, initial, ct);
        var meal = new PawnActivities(1, [new("producer", "meal-assignment", "meal", 0, 1, false,
            [new(PawnActivityKind.Eat, "rice-stack", "", "", 1, 500)])
            { Task = "meal" }])
        { Tasks = [new("meal", "producer", [], new(PawnActivityKind.Eat, "rice-stack", "", "", 1, 500), new(1, 1), 100)
            { Inputs = [new("Rice", "rice-stack", 1, new(1, 1), "observed rice") { Observed = true }] }] };
        journal.CommitMethod(owner.Standard.Id, owner.Revision, new("meal"), Plan("residual-meal", meal), ct);
        var observed = new[] { Row("completed"), new ActivityOwnership("helper", waiting.Assignment, "method", 1, "holding") };
        var bound = waiting with { ExpectedRevision = 1, Revision = 2, Preparing = false,
            Steps = [delivery.Step with { Target = "rice-stack" }] };
        var released = idle with { ExpectedRevision = 1, Revision = 2, Release = true, Task = "", Steps = [] };
        var mixed = Plan("mixed", batch with { Orders = [bound, released], Tasks = [delivery] });
        var before = journal.LoadStandard(owner.Standard.Id, ct);
        Assert.Equal(ActivityContinuationAdmission.ResourcesUnavailable,
            journal.ContinueActivityMethod(initial.Id, Root, new(110), observed, mixed, ct));
        Assert.False(journal.LoadPlan(initial.Id, ct).Retired);
        Assert.Equal(before.Revision, journal.LoadStandard(owner.Standard.Id, ct).Revision);
        Assert.DoesNotContain(journal.LoadOwnerMethods(before, ct), m => m.Plan == mixed.Id);
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
        Assert.Null(ReviewActivityOwnership(Batch, Known(true), 110, [Row()]).Release);
        Assert.Null(ReviewActivityOwnership(Batch, default, 110, [Row()]).Release);
        var release = Assert.Single(ReviewActivityOwnership(Batch, Known(false), 110, [Row()]).Release!.Orders);
        Assert.True(release.Release);
        Assert.Equal(1, release.ExpectedRevision);
        Assert.Equal(2, release.Revision);
        Assert.Empty(release.Steps);
        Assert.Null(ReviewActivityOwnership(Batch, Known(false), 110, []).Release);
        Assert.NotNull(ReviewActivityOwnership(Batch, default, 110, [Row("failed")]).Release);
        Assert.NotNull(ReviewActivityOwnership(Batch, Known(true), 500, [Row("completed")]).Release);
    }

    [Fact]
    public void LostParticipantDoesNotAuthorizeReleasingItsNewOwner()
    {
        var batch = Batch with { Orders = [Order, Order with { Pawn = "helper", Assignment = "helper-assignment" }] };
        var rows = new[] { Row(), new ActivityOwnership("helper", "player-or-other-method", "other", 9, "running") };
        var result = ReviewActivityOwnership(batch, Known(false), 110, rows);
        Assert.Equal("pawn", Assert.Single(result.Release!.Orders).Pawn);
        Assert.True(ReviewActivityOwnership(Batch, Known(false), 110, [Row() with { Revision = 2 }]).OwnershipEnded);
    }

    [Fact]
    public void RecoveredConcernCanDispatchReleaseThroughItsOriginalMethod()
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Open(ct);
        var initial = Plan("initial", Batch);
        var owner = Admit(journal, initial, ct);
        owner = journal.ReviewStandard(owner.Standard.Id, owner.Revision, Root, Finding.Met, ct);
        var method = Assert.Single(journal.ActivityMethods(Root, ct));
        Assert.False(method.Needed.Value);
        var release = Plan("release", ReviewActivityOwnership(Batch, method.Needed, 110, [Row()]).Release!);
        journal.ContinueActivityMethod(initial.Id, Root, new(110), [Row()], release, ct);
        Assert.Contains("pawn", journal.WorkResources(Root, ct).Claims.Pawns);
        var old = journal.LoadPlan(initial.Id, ct);
        Assert.True(old.Retired);
        Assert.Equal(release.Id, old.SupersededBy);
        Assert.Equal(release.Id, Assert.Single(journal.LoadOwnerMethods(owner, ct)).Plan);
        var scope = Root with { Plan = release.Id };
        owner = journal.LoadStandard(owner.Standard.Id, ct);
        journal.ReviewStandard(owner.Standard.Id, owner.Revision, Root, Finding.Met, ct);
        journal.AuthorizeRoundsPlan(Root, scope, ct);
        journal.Prepare(release.Id, release.Actions[0].Id, scope, new(110), ct);
        var sent = journal.Dispatch(release.Id, release.Actions[0].Id, scope, new(110), ct);
        journal.RecordReceipt(release.Id, release.Actions[0].Id, sent.View.Attempt, Receipt.Accepted, ct);
        Assert.Empty(journal.ActivityMethods(Root, ct));
        Assert.DoesNotContain("pawn", journal.WorkResources(Root, ct).Claims.Pawns);
        Assert.Throws<ConflictException>(() => journal.ContinueActivityMethod(initial.Id, Root, new(110), [Row()], release, ct));
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
