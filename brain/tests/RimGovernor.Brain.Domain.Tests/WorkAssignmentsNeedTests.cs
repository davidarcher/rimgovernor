using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Store;
using Xunit;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The admin/work-assignments Need (#3304): it replaced the work-assignments Concern. Its predicate is the review's
/// coverage, cleanup, settings and coupled-activity measure; its Method commits under the Need owner, claims each pawn's
/// settings and each written policy, and is the only Need owner whose plan may carry work priorities.
/// </summary>
public sealed class WorkAssignmentsNeedTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));

    [Fact]
    public void TheNeedRequiresACompletedWorkPlannerReview()
    {
        Assert.Equal(Satisfaction.Unmet, AdminFamily.WorkAssignmentsNeed(WorkAssignmentsReview.Owed).Satisfaction);
        Assert.Equal(Satisfaction.Met, AdminFamily.WorkAssignmentsNeed(WorkAssignmentsReview.Settled).Satisfaction);
        Assert.Equal("work assignments review", Assert.Throws<RequiredFactException>(() => Assert.Single(new AdminFamily(new AdminNeeds { WorkAssignments = true }).Derive(new AdminFacts()))).Fact);
        Assert.Empty(new AdminFamily(new AdminNeeds()).Derive(new AdminFacts { WorkAssignments = WorkAssignmentsReview.Owed }));
    }

    [Fact]
    public void WorkAndStockpileReviewsCannotReuseASettledPreviousWorld()
    {
        var other = World with { Load = new("next-load") };
        var work = new ReviewMemory<WorkAssignmentsReview>(WorkAssignmentsReview.AwaitingPlanner);
        Assert.Throws<RequiredFactException>(() => AdminFamily.WorkAssignmentsNeed(work.Get(World)));
        work.Set(World, WorkAssignmentsReview.Settled);
        Assert.Equal(Satisfaction.Met, AdminFamily.WorkAssignmentsNeed(work.Get(World)).Satisfaction);
        Assert.Throws<RequiredFactException>(() => AdminFamily.WorkAssignmentsNeed(work.Get(other)));

        var stockpiles = new StockpileMemory();
        Assert.Throws<RequiredFactException>(() => stockpiles.Active(World));
        stockpiles.SetActive(World, false);
        Assert.False(stockpiles.Active(World));
        Assert.Throws<RequiredFactException>(() => stockpiles.Active(other));
        stockpiles.SetActive(World, null);
        Assert.Throws<RequiredFactException>(() => stockpiles.Active(World));
    }

    [Fact]
    public void ARoundsFactsRecordNoWorkPlanUntilThePlannerRan()
    {
        var facts = WorkResourceTestFacts.Create("pawn");

        Assert.Equal(WorkCoverageState.Unread, facts.WorkCoverage);
        Assert.IsType<WorkRosterRead.Unread>(facts.WorkRoster);
        Assert.IsType<WorkRosterRead.Read>(new WorkRosterRead.Read([]));
        Assert.IsType<WorkPawnsRead>(new WorkPawnsRead([]));
        Assert.Equal(WorkAssignmentsReview.Settled, RoundsRules.WorkAssignmentsOwed(facts with { WorkCoverage = WorkCoverageState.Matched, CleanupPawns = false }, null));
        Assert.Equal(WorkAssignmentsReview.Owed, RoundsRules.WorkAssignmentsOwed(facts with { WorkCoverage = WorkCoverageState.Unmatched, CleanupPawns = false }, null));
    }

    [Fact]
    public void WorkReviewNamesTheAwaitingPlannerStateAndPreservesKnownWork()
    {
        var covered = WorkResourceTestFacts.Create("pawn") with { WorkCoverage = WorkCoverageState.Matched, CleanupPawns = false };
        Assert.Equal(WorkAssignmentsReview.Settled, RoundsRules.WorkAssignmentsOwed(covered, null));
        Assert.Equal(WorkAssignmentsReview.Owed, RoundsRules.WorkAssignmentsOwed(covered with { WorkCoverage = WorkCoverageState.Unmatched }, null));
        Assert.Equal(WorkAssignmentsReview.Owed, RoundsRules.WorkAssignmentsOwed(covered with { CleanupPawns = true }, null));
        Assert.Equal(WorkAssignmentsReview.Owed, RoundsRules.WorkAssignmentsOwed(covered with { HostilityOwed = true }, null));
        Assert.Equal(WorkAssignmentsReview.Owed, RoundsRules.WorkAssignmentsOwed(covered with { NamesOwed = true }, null));
        Assert.Equal(WorkAssignmentsReview.Owed, RoundsRules.WorkAssignmentsOwed(covered with { CoupledWorkActive = true }, null));
        Assert.Equal(WorkAssignmentsReview.AwaitingPlanner, RoundsRules.WorkAssignmentsOwed(covered with { WorkCoverage = WorkCoverageState.Unread }, null));
        // An owed setting is owed whatever the coverage read says.
        Assert.Equal(WorkAssignmentsReview.Owed, RoundsRules.WorkAssignmentsOwed(covered with { WorkCoverage = WorkCoverageState.Unread, MedicalCareOwed = true }, null));
    }

    [Fact]
    public void AssignmentsCommitUnderTheNeedAloneAndAWrittenPolicyIsClaimed()
    {
        var ct = TestContext.Current.CancellationToken;
        IntentActionRules.RegisterIntentKinds();
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-work-assignments-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        journal.ReviewRounds(new RoundsRequest { Current = World, Tick = new(100), Enabled = true, Policy = RoundsRules.DefaultRoundsPolicy(), Facts = WorkResourceTestFacts.Create("a", "b") }, ct);
        static PlanSpec Plan(params Func<ActionId, DomainAction>[] actions)
        {
            var id = PlanIds.MintPlanId();
            return new PlanSpec(id, new PlanRevision(1), [.. actions.Select((a, i) => a(new ActionId(id.Value + "-" + i)))]);
        }
        Func<ActionId, DomainAction> Assign(string pawn) => a => DomainAction.NewWorkAssignmentAction(a, WorkAssignment.NewWorkAssignment(new(pawn), [new WorkSetting("TestWork", 1)]));
        Func<ActionId, DomainAction> Reading(string name) => a => DomainAction.NewReadingPolicyAction(a, new ReadingPolicy(name, ["TestBook"]));

        var work = journal.CommitClaimedMethod(AdminFamily.WorkAssignments, World, new MethodId("work-a"), "", Plan(Assign("a"), Reading("a")), ct);
        Assert.Equal(ClaimOutcome.Admitted, work.Outcome);
        Assert.Equal(AdminFamily.WorkAssignments.FilingKey, journal.PlanMethod(work.Owner.Methods[0].Plan, ct)!.Concern);
        Assert.Equal(ClaimOutcome.Continued, journal.CommitClaimedMethod(AdminFamily.WorkAssignments, World, new MethodId("work-a"), "", Plan(Assign("a")), ct).Outcome);

        // Another Method writing the same reading policy waits on its claim; another pawn's priorities are admitted.
        var held = journal.CommitClaimedMethod(AdminFamily.WorkAssignments, World, new MethodId("work-reading"), "", Plan(Reading("a")), ct);
        Assert.Equal(ClaimOutcome.Conflict, held.Outcome);
        Assert.Equal("policy/reading/a", held.Conflict!.Claim);
        Assert.Equal(ClaimOutcome.Admitted, journal.CommitClaimedMethod(AdminFamily.WorkAssignments, World, new MethodId("work-b"), "", Plan(Assign("b")), ct).Outcome);

        // No other Need sets work priorities (an unclaimed pawn, so admission rather than a claim refuses it).
        Assert.ThrowsAny<Exception>(() => journal.CommitClaimedMethod(AdminFamily.Burial, World, new MethodId("work-c"), "", Plan(Assign("c")), ct));
        Assert.Empty(journal.LoadNeed(AdminFamily.Burial, World, ct).Methods);
    }

    [Fact]
    public void ACoupledMethodUnderTheNeedIsNotThePlannersExistingWork()
    {
        // #3557: the coupled Methods share the Need; counting their open plans as the Work planner's own
        // left it "already working" for whole reviews, so no priorities or finishing-skill choices were written.
        var ct = TestContext.Current.CancellationToken;
        IntentActionRules.RegisterIntentKinds();
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-work-coupled-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var facts = WorkRules.ReviewWorkResources(WorkResourceTestFacts.Create("a", "b"), new ProductionBatchCensus.Read([]), new([]));
        journal.ReviewRounds(new RoundsRequest { Current = World, Tick = new(100), Enabled = true, Policy = RoundsRules.DefaultRoundsPolicy(), Facts = facts }, ct);
        var coupled = PlanIds.MintPlanId();
        var method = "coupled-" + coupled.Value;
        var held = journal.CommitClaimedMethod(AdminFamily.WorkAssignments, World, new MethodId(method), "shared colony consequence portfolio",
            new PlanSpec(coupled, new PlanRevision(1), [DomainAction.NewPawnActivitiesAction(new ActionId(coupled.Value + "-0"),
                new PawnActivities(1, [new PawnActivityOrder("a", method + "/a", method, 0, 1, false, [new PawnActivityStep(PawnActivityKind.Hold, "", "", "", 0, long.MaxValue)])]))]), ct);
        Assert.Equal(ClaimOutcome.Admitted, held.Outcome);
        Assert.True(StorePackage.PlanOpen(journal.LoadPlan(held.Owner.Methods[0].Plan, ct)));
        Assert.Empty(BuildingRuntime.RoundsWorkPlanner.OpenWorkPlans(journal, held.Owner, ct));

        var work = PlanIds.MintPlanId();
        var committed = journal.CommitClaimedMethod(AdminFamily.WorkAssignments, World, new MethodId("work-a"), "", new PlanSpec(work, new PlanRevision(1),
            [DomainAction.NewWorkAssignmentAction(new ActionId(work.Value + "-0"), WorkAssignment.NewWorkAssignment(new("b"), [new WorkSetting("TestWork", 1)]))]), ct);
        Assert.Equal(ClaimOutcome.Admitted, committed.Outcome);
        var open = Assert.Single(BuildingRuntime.RoundsWorkPlanner.OpenWorkPlans(journal, committed.Owner, ct));
        Assert.Equal(work, open.Spec.Id);
    }

    [Fact]
    public void AFinishingSkillChoiceRidesTheWorkMethodBesideItsAssignments()
    {
        // #3558: the step commits work priorities and construction finishing-skill choices as one Method; admission
        // refused the choice's building action as a non-assignment.
        var ct = TestContext.Current.CancellationToken;
        IntentActionRules.RegisterIntentKinds();
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-work-skill-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        journal.ReviewRounds(new RoundsRequest { Current = World, Tick = new(100), Enabled = true, Policy = RoundsRules.DefaultRoundsPolicy(), Facts = WorkResourceTestFacts.Create("a", "b") }, ct);
        var id = PlanIds.MintPlanId();
        var assign = DomainAction.NewWorkAssignmentAction(new ActionId(id.Value + "-0"), WorkAssignment.NewWorkAssignment(new("a"), [new WorkSetting("TestWork", 1)]));
        var wall = DomainAction.NewBuildingAction(new ActionId(id.Value + "-1"), new Building("TestWall", new Cell(3, 3), Rotation.North, "Wood"), ConstructionTier.Survive);
        var plan = new PlanSpec(id, new PlanRevision(1), [assign, wall.WithFinishingSkill(6, "site-1")]);

        Assert.Equal(ClaimOutcome.Admitted, journal.CommitClaimedMethod(AdminFamily.WorkAssignments, World, new MethodId("work-skill"), "", plan, ct).Outcome);

        // A plain building action is still not the work method's, and the refusal names it.
        var other = PlanIds.MintPlanId();
        var refused = new PlanSpec(other, new PlanRevision(1), [
            DomainAction.NewWorkAssignmentAction(new ActionId(other.Value + "-0"), WorkAssignment.NewWorkAssignment(new("b"), [new WorkSetting("TestWork", 2)])),
            DomainAction.NewBuildingAction(new ActionId(other.Value + "-1"), new Building("TestWall", new Cell(5, 5), Rotation.North, "Wood"), ConstructionTier.Survive)]);
        var error = Assert.Throws<ConflictException>(() => journal.CommitClaimedMethod(AdminFamily.WorkAssignments, World, new MethodId("work-wall"), "", refused, ct));
        Assert.Contains(other.Value + "-1", error.Message);
    }
}
