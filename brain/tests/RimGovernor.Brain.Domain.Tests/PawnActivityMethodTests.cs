using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using RimGovernor.Brain.Bridge.Contracts;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class PawnActivityMethodTests
{
    private static StandardState Owner(Journal journal, CancellationToken ct)
    {
        var world = new GenerationSnapshot(new ColonyId("colony"), new MapId(1), new LoadId("load"),
            new PlanId("review"), new PlanRevision(1), default);
        var review = journal.ReviewRounds(new RoundsRequest { Current = world, Tick = new Tick(100),
            Enabled = true, Policy = DefaultRoundsPolicy(), Facts = WorkResourceTestFacts.Create("pawn", "producer", "helper") }, ct);
        return Assert.Single(review.Standards, s => s.Standard.Kind == MaintainWorkLedger);
    }

    private static PawnActivityOrder Order(string pawn = "pawn", string method = "food-method", bool release = false) =>
        new(pawn, "assignment-" + pawn, method, 0, 1, release, release ? [] :
            [new(PawnActivityKind.Deliver, "observed-stack-" + pawn, "", "", 2, 500) { Destination = new Cell(10, 11) }]);

    private static DomainAction Action(string id, params PawnActivityOrder[] orders) =>
        DomainAction.NewPawnActivitiesAction(new ActionId(id), new PawnActivities(1, orders));

    [Fact]
    public void RealMethodJournalRetainsCoordinatedAssignmentsForHandsWithoutCompletingWork()
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-activity-method-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var owner = Owner(journal, ct);
        var action = Action("dispatch", Order("producer"), Order("helper"));
        var plan = new PlanSpec(new PlanId("food-plan"), new PlanRevision(1), [action]);
        var committed = journal.CommitMethod(owner.Standard.Id, owner.Revision, new MethodId("food-method"), plan, ct);
        Assert.Equal(plan.Id, Assert.Single(committed.Methods).Plan);
        var stored = journal.LoadPlan(plan.Id, ct);
        Assert.Equal(Stage.Pending, Assert.Single(stored.Progress).View.Stage);
        var persisted = Assert.Single(stored.Spec.Actions);
        BridgeFunctions.RegisterIntentKinds();
        var intent = BridgeFunctions.IntentAction(persisted.Id.Value, persisted).Intent!.PawnActivities!.Batch!;
        Assert.Equal(2, intent.Orders.Length);
        Assert.All(intent.Orders, order => Assert.Equal("food-method", order.Method));
        Assert.Equal(action.PawnActivities!.Orders[1].Steps, intent.Orders[1].Steps);
        Assert.Throws<OpenMethodException>(() => journal.CommitMethod(committed.Standard.Id, committed.Revision,
            new MethodId("another-method"), new PlanSpec(new PlanId("another-plan"), new PlanRevision(1),
                [Action("another-action", Order(method: "another-method"))]), ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WrongMethodIncludingReleaseRollsBackBeforeBinding(bool release)
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-activity-owner-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var owner = Owner(journal, ct);
        var plan = new PlanSpec(new PlanId("food-plan"), new PlanRevision(1), [Action("dispatch", Order(method: "unrelated", release: release))]);
        var error = Assert.Throws<ConflictException>(() => journal.CommitMethod(owner.Standard.Id, owner.Revision,
            new MethodId("food-method"), plan, ct));
        Assert.Contains("does not match", error.Message);
        Assert.Empty(journal.LoadOwnerMethods(owner, ct));
        Assert.Equal(owner.Revision, journal.LoadStandard(owner.Standard.Id, ct).Revision);
        // Reusing the same plan/action/Method identities succeeds: rejection left no partial plan or binding.
        var fixedPlan = new PlanSpec(plan.Id, plan.Revision, [Action("dispatch", Order(release: release))]);
        journal.CommitMethod(owner.Standard.Id, owner.Revision, new MethodId("food-method"), fixedPlan, ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InstallationDependencyCannotAuthorizeTheNextPawnRevision(bool dependency)
    {
        var ct = TestContext.Current.CancellationToken;
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-activity-revisions-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var owner = Owner(journal, ct);
        var next = Order() with { ExpectedRevision = 1, Revision = 2 };
        var plan = new PlanSpec(new PlanId("food-plan"), new PlanRevision(1), [Action("first", Order()), Action("second", next)],
            dependency ? [new ActionDependency(new ActionId("second"), new ActionId("first"), true)] : []);
        var error = Assert.Throws<ConflictException>(() => journal.CommitMethod(owner.Standard.Id, owner.Revision,
            new MethodId("food-method"), plan, ct));
        Assert.Contains("repeats a pawn", error.Message);
        Assert.Empty(journal.LoadOwnerMethods(owner, ct));
        var distinct = new PlanSpec(plan.Id, plan.Revision, [Action("first", Order()), Action("second", Order("helper"))]);
        journal.CommitMethod(owner.Standard.Id, owner.Revision, new MethodId("food-method"), distinct, ct);
    }
}
