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
            Policy = DefaultRoundsPolicy(), Facts = new() { Tick = new(100), LedgerOwed = Known(true) } }, ct);
        var owner = Assert.Single(review.Standards, s => s.Standard.Kind == MaintainWorkLedger);
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
