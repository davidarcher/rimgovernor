using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Runtime;
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
    public void GeneratedTimetableTravelsThroughSharedAdmissionJournalAndTypedIntent()
    {
        var ct = TestContext.Current.CancellationToken;
        IntentActionRules.RegisterIntentKinds();
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-generated-schedule-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        journal.Review(new ReviewRequest { Current = World, Tick = new(100), Enabled = true,
            Policy = Rules.DefaultPlannerSettings(), Facts = WorkResourceTestFacts.Create("a") }, ct);
        var change = Assert.Single(WorkRules.NativeSettingsChanges([PawnFixtures.Worker("a")], [new(new("a"), [])],
            NeedsFixtures.Read(100, NativeTimetableTests.Needs())));
        var action = DomainAction.NewWorkAssignmentAction(new("schedule-0"), change);
        var plan = new PlanSpec(new("schedule"), new(1), [action]);
        Assert.Equal(ClaimOutcome.Admitted, journal.CommitClaimedMethod(AdminFamily.WorkAssignments, World, new("work-schedule"), "", plan, ct).Outcome);
        journal.Prepare(plan.Id, action.Id, World with { Plan = plan.Id }, new(101), ct);
        journal.Dispatch(plan.Id, action.Id, World with { Plan = plan.Id }, new(101), ct);
        var intent = IntentActionRules.IntentAction(action.Id.Value, action).Intent!.WorkSettings!;
        Assert.Empty(intent.Work);
        Assert.Equal(change.Schedule, intent.Schedule!.AssignmentDefs);
        Assert.Equal(Stage.Dispatched, Assert.Single(journal.LoadPlan(plan.Id, ct).Progress).View.Stage);
        // Dispatch is still pending observed application, never a claim that the pawn slept or produced.
        Assert.True(StorePackage.PlanOpen(journal.LoadPlan(plan.Id, ct)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FreshTimetableChangesCancelOnlyStaleUndispatchedSettingsAndReleaseTheirClaim(bool prepared, bool changed)
    {
        var ct = TestContext.Current.CancellationToken;
        IntentActionRules.RegisterIntentKinds();
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-work-schedule-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        journal.Review(new ReviewRequest { Current = World, Tick = new(100), Enabled = true,
            Policy = Rules.DefaultPlannerSettings(), Facts = WorkResourceTestFacts.Create("a") }, ct);
        var schedule = Enumerable.Repeat(PawnRules.ScheduleAnything, 24).ToArray();
        schedule[22] = PawnRules.ScheduleSleep;
        var fresh = (string[])schedule.Clone();
        if (changed) { fresh[22] = PawnRules.ScheduleAnything; fresh[9] = PawnRules.ScheduleSleep; }
        var old = WorkAssignment.NewScheduleAssignment(new("a"), [new WorkSetting("TestWork", 1)], schedule);
        var wanted = WorkAssignment.NewScheduleAssignment(new("a"), [new WorkSetting("TestWork", 1)], fresh);
        var plan = new PlanSpec(new("old"), new(1), [DomainAction.NewWorkAssignmentAction(new("old-0"), old)]);
        TestNeed.Commit(journal, World, new("work-old"), plan, ct);
        if (prepared) journal.Prepare(plan.Id, plan.Actions[0].Id, World with { Plan = plan.Id }, new(100), ct);

        WorkPlanner.CancelStaleWorkActions(journal, journal.LoadPlan(plan.Id, ct), [wanted], [], [], ct);

        var progress = Assert.Single(journal.LoadPlan(plan.Id, ct).Progress).View;
        Assert.Equal(changed ? Stage.Cancelled : prepared ? Stage.Prepared : Stage.Pending, progress.Stage);
        var action = DomainAction.NewWorkAssignmentAction(new("fresh-0"), wanted);
        var next = new PlanSpec(new("fresh"), new(1), [action]);
        var admitted = journal.CommitClaimedMethod(AdminFamily.WorkAssignments, World, new("work-fresh"), "", next, ct);
        Assert.Equal(changed ? ClaimOutcome.Admitted : ClaimOutcome.Conflict, admitted.Outcome);
        if (!changed) return;
        journal.Prepare(next.Id, action.Id, World with { Plan = next.Id }, new(101), ct);
        journal.Dispatch(next.Id, action.Id, World with { Plan = next.Id }, new(101), ct);
        var intent = IntentActionRules.IntentAction(action.Id.Value, action).Intent!.WorkSettings!;
        Assert.Equal(fresh, intent.Schedule!.AssignmentDefs);
        Assert.Equal(1, Assert.Single(intent.Work).Priority);
    }

    [Fact]
    public void StalenessChecksEveryWrittenSettingWithoutRequiringUnwrittenFieldsToMatch()
    {
        var area = WorkAssignment.NewAreaAssignment(new("a"), false, "old-area");
        Assert.True(WorkRules.WorkAssignmentStale(area, new Dictionary<PawnId, WorkAssignment>
            { [new("a")] = WorkAssignment.NewAreaAssignment(new("a"), false, "new-area") }));
        Assert.True(WorkRules.WorkAssignmentStale(area, new Dictionary<PawnId, WorkAssignment>
            { [new("a")] = WorkAssignment.NewAreaAssignment(new("a"), true, "") }));
        var priorities = WorkAssignment.NewWorkAssignment(new("a"), [new WorkSetting("TestWork", 1)]);
        Assert.False(WorkRules.WorkAssignmentStale(priorities, new Dictionary<PawnId, WorkAssignment>
            { [new("a")] = WorkAssignment.NewScheduleAssignment(new("a"), priorities.Settings,
                Enumerable.Repeat(PawnRules.ScheduleAnything, 24).ToArray()) }));
        Assert.True(WorkRules.WorkAssignmentStale(priorities, new Dictionary<PawnId, WorkAssignment>()));
    }

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
        Assert.Equal(WorkAssignmentsReview.Settled, Rules.WorkAssignmentsOwed(facts with { WorkCoverage = WorkCoverageState.Matched, CleanupPawns = false }, null));
        Assert.Equal(WorkAssignmentsReview.Owed, Rules.WorkAssignmentsOwed(facts with { WorkCoverage = WorkCoverageState.Unmatched, CleanupPawns = false }, null));
    }

    [Fact]
    public void WorkReviewNamesTheAwaitingPlannerStateAndPreservesKnownWork()
    {
        var covered = WorkResourceTestFacts.Create("pawn") with { WorkCoverage = WorkCoverageState.Matched, CleanupPawns = false };
        Assert.Equal(WorkAssignmentsReview.Settled, Rules.WorkAssignmentsOwed(covered, null));
        Assert.Equal(WorkAssignmentsReview.Owed, Rules.WorkAssignmentsOwed(covered with { WorkCoverage = WorkCoverageState.Unmatched }, null));
        Assert.Equal(WorkAssignmentsReview.Owed, Rules.WorkAssignmentsOwed(covered with { CleanupPawns = true }, null));
        Assert.Equal(WorkAssignmentsReview.Owed, Rules.WorkAssignmentsOwed(covered with { HostilityOwed = true }, null));
        Assert.Equal(WorkAssignmentsReview.Owed, Rules.WorkAssignmentsOwed(covered with { NamesOwed = true }, null));
        Assert.Equal(WorkAssignmentsReview.Owed, Rules.WorkAssignmentsOwed(covered with { CoupledWorkActive = true }, null));
        Assert.Equal(WorkAssignmentsReview.AwaitingPlanner, Rules.WorkAssignmentsOwed(covered with { WorkCoverage = WorkCoverageState.Unread }, null));
        // An owed setting is owed whatever the coverage read says.
        Assert.Equal(WorkAssignmentsReview.Owed, Rules.WorkAssignmentsOwed(covered with { WorkCoverage = WorkCoverageState.Unread, MedicalCareOwed = true }, null));
    }

    [Fact]
    public void AssignmentsCommitUnderTheNeedAloneAndAWrittenPolicyIsClaimed()
    {
        var ct = TestContext.Current.CancellationToken;
        IntentActionRules.RegisterIntentKinds();
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-work-assignments-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        journal.Review(new ReviewRequest { Current = World, Tick = new(100), Enabled = true, Policy = Rules.DefaultPlannerSettings(), Facts = WorkResourceTestFacts.Create("a", "b") }, ct);
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
    public void WorkSettingsCanChangeThePawnWhoseExecutionMethodRemainsOpen()
    {
        // #3557: the coupled Methods share the Need; counting their open plans as the Work planner's own
        // left it "already working" for whole reviews, so no priorities or finishing-skill choices were written.
        var ct = TestContext.Current.CancellationToken;
        IntentActionRules.RegisterIntentKinds();
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-work-coupled-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        var facts = WorkRules.ReviewWorkResources(WorkResourceTestFacts.Create("a", "b"), new ProductionBatchCensus.Read([]), new([]));
        journal.Review(new ReviewRequest { Current = World, Tick = new(100), Enabled = true, Policy = Rules.DefaultPlannerSettings(), Facts = facts }, ct);
        var coupled = PlanIds.MintPlanId();
        var method = "coupled-" + coupled.Value;
        var held = journal.CommitClaimedMethod(AdminFamily.WorkAssignments, World, new MethodId(method), "shared colony consequence portfolio",
            new PlanSpec(coupled, new PlanRevision(1), [DomainAction.NewPawnActivitiesAction(new ActionId(coupled.Value + "-0"),
                TestNeed.Purposed(new PawnActivities(1, [new PawnActivityOrder("a", method + "/a", method, 0, 1, false, [new PawnActivityStep(PawnActivityKind.Hold, "", "", "", 0, long.MaxValue)])])))]), ct);
        Assert.Equal(ClaimOutcome.Admitted, held.Outcome);
        Assert.True(StorePackage.PlanOpen(journal.LoadPlan(held.Owner.Methods[0].Plan, ct)));
        Assert.Empty(Runtime.WorkPlanner.OpenWorkPlans(journal, held.Owner, ct));

        var work = PlanIds.MintPlanId();
        var committed = journal.CommitClaimedMethod(AdminFamily.WorkAssignments, World, new MethodId("work-a"), "", new PlanSpec(work, new PlanRevision(1),
            [DomainAction.NewWorkAssignmentAction(new ActionId(work.Value + "-0"), WorkAssignment.NewScheduleAssignment(new("a"),
                [new WorkSetting("TestWork", 1)], Enumerable.Repeat(PawnRules.ScheduleAnything, 24).ToArray()))]), ct);
        Assert.Equal(ClaimOutcome.Admitted, committed.Outcome);
        var open = Assert.Single(Runtime.WorkPlanner.OpenWorkPlans(journal, committed.Owner, ct));
        Assert.Equal(work, open.Spec.Id);
        Assert.True(StorePackage.PlanOpen(journal.LoadPlan(held.Owner.Methods[0].Plan, ct)));
        Assert.Equal("a", Assert.Single(open.Spec.Actions).WorkAssignment!.Pawn.Value);
        Assert.Equal(24, Assert.Single(open.Spec.Actions).WorkAssignment!.Schedule.Count);
    }

    [Fact]
    public void AFinishingSkillChoiceRidesTheWorkMethodBesideItsAssignments()
    {
        // #3558: the step commits work priorities and construction finishing-skill choices as one Method; admission
        // refused the choice's building action as a non-assignment.
        var ct = TestContext.Current.CancellationToken;
        IntentActionRules.RegisterIntentKinds();
        using var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-work-skill-" + Guid.NewGuid().ToString("N") + ".db"), ct);
        journal.Review(new ReviewRequest { Current = World, Tick = new(100), Enabled = true, Policy = Rules.DefaultPlannerSettings(), Facts = WorkResourceTestFacts.Create("a", "b") }, ct);
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
