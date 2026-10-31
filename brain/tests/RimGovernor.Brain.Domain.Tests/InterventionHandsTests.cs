using RimGovernor.Brain.Executor;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Policy.Methods.Binding;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Store;
using Xunit;
using Hands = RimGovernor.Brain.Executor.Executor;
using Journal = RimGovernor.Brain.Store.Store;
using NativeReceipt = RimGovernor.Brain.Executor.Receipt;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>Task 4.2: a justified exact intervention is bound through the existing Method claims, safeguards, journal and Hands
/// with its bounded purpose; admission and continuation tell accepted, running, completed and replaced work apart, refuse a
/// stale prerequisite, and neither issue an open purpose twice nor reissue one from a justification older than its end.</summary>
public sealed class InterventionHandsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("review"), new(1), new(1));
    private static readonly InterventionPurpose Hunger = new(InterventionPurposeKind.NeedShortfall, "food", "a", 100);
    private sealed class Clock : IClock { public DateTimeOffset Now() => DateTimeOffset.UtcNow; }

    // Exercises real journal/Hands and typed transport. It makes no claim about native job execution.
    private sealed class Native : IBoundary
    {
        public List<PawnActivities> Written { get; } = [];
        public Task<IntentInspection> InspectIntentAsync(Target target, CancellationToken ct) => Task.FromResult(new IntentInspection
        {
            StartedAt = DateTimeOffset.UtcNow, ObservedAt = DateTimeOffset.UtcNow, Current = target.Snapshot, Tick = new(100),
        });
        public Task<IReadOnlyList<NativeReceipt>> WriteIntentsAsync(IReadOnlyList<Placement> placements, CancellationToken ct)
        {
            Written.AddRange(placements.Select(p => p.Action.PawnActivities!));
            return Task.FromResult<IReadOnlyList<NativeReceipt>>([.. placements.Select(p => new NativeReceipt
            {
                Action = p.Action.Id, Attempt = p.Attempt, Snapshot = p.Snapshot, Kind = Receipt.Accepted,
            })]);
        }
    }

    private static Journal Open(ReviewFacts facts)
    {
        IntentActionRules.RegisterIntentKinds();
        var journal = Journal.Open(Path.Combine(Path.GetTempPath(), "rg-intervention-" + Guid.NewGuid().ToString("N") + ".db"), Ct);
        journal.Review(new() { Current = World, Tick = new(100), Enabled = true, Policy = Rules.DefaultPlannerSettings(), Facts = facts }, Ct);
        return journal;
    }

    private static PawnActivityOrder Order(string method, InterventionPurpose? purpose, string pawn = "a") =>
        new(pawn, method + "/" + pawn, method, 0, 1, false, [new PawnActivityStep(PawnActivityKind.Hold, "", "", "", 0, 900)]) { Purpose = purpose };

    private static PlanSpec Plan(string method, params PawnActivityOrder[] orders) => new(new(method + "-plan"), new(1),
        [DomainAction.NewPawnActivitiesAction(new(method + "-plan-0"), new PawnActivities(1, orders))]);

    private static NeedCommit Commit(Journal journal, PlanSpec plan, GenerationSnapshot? world = null) =>
        journal.CommitClaimedMethod(AdminFamily.WorkAssignments, world ?? World, new(plan.Id.Value[..^"-plan".Length]), "", plan, Ct);

    private static void Install(Journal journal, PlanSpec plan)
    {
        var scope = World with { Plan = plan.Id };
        journal.Prepare(plan.Id, plan.Actions[0].Id, scope, new(100), Ct);
        var sent = journal.Dispatch(plan.Id, plan.Actions[0].Id, scope, new(100), Ct);
        journal.RecordReceipt(plan.Id, plan.Actions[0].Id, sent.View.Attempt, Receipt.Accepted, Ct);
    }

    /// <summary>(a) Acceptance is not completion, and a release or replacement is never completed work.</summary>
    [Fact]
    public void ObservedOutcomesTellAcceptedRunningCompletedAndReplacedWorkApart()
    {
        var order = Order("coupled-a", Hunger);
        ActivityOwnership Row(string state, string reason = "", string method = "coupled-a") =>
            new("a", "coupled-a/a", method, 1, state) { Reason = reason };
        Assert.Equal(ObservedOutcome.Accepted, InterventionPurposes.Observe(order, Row("installed")));
        Assert.Equal(ObservedOutcome.Accepted, InterventionPurposes.Observe(order, Row("offered")));
        Assert.Equal(ObservedOutcome.Running, InterventionPurposes.Observe(order, Row("started")));
        Assert.Equal(ObservedOutcome.Running, InterventionPurposes.Observe(order, Row("holding")));
        Assert.Equal(ObservedOutcome.Completed, InterventionPurposes.Observe(order, Row("completed")));
        Assert.Equal(ObservedOutcome.Failed, InterventionPurposes.Observe(order, Row("failed", "deadline")));
        Assert.Equal(ObservedOutcome.Replaced, InterventionPurposes.Observe(order, Row("failed", InterventionPurposes.NativeJobTakeover)));
        Assert.Equal(ObservedOutcome.Replaced, InterventionPurposes.Observe(order, Row("started", method: "player")));
        Assert.Equal(ObservedOutcome.Released, InterventionPurposes.Observe(order, new ActivityOwnership("a", "", "", 0, "unowned")));
        // Only a replacement or the order's own failure ends the attempt; accepted and running work goes on.
        var batch = new PawnActivities(1, [order]);
        Assert.Null(ActivityContinuationRules.ReviewActivityOwnership(batch, ActivityNeed.Owed, 100, [Row("installed")]).Release);
        Assert.Null(ActivityContinuationRules.ReviewActivityOwnership(batch, ActivityNeed.Owed, 100, [Row("started")]).Release);
        var replaced = ActivityContinuationRules.ReviewActivityOwnership(batch, ActivityNeed.Owed, 100,
            [Row("failed", InterventionPurposes.NativeJobTakeover)]).Release;
        Assert.True(Assert.Single(Assert.IsType<PawnActivities>(replaced).Orders).Release);
        Assert.Equal(["a:replaced"], InterventionPurposes.Outcomes(batch, [Row("failed", InterventionPurposes.NativeJobTakeover)]));

        // Through the journal, the accepted receipt installs the order: the plan's progress is completed, its purpose is not.
        using var journal = Open(WorkResourceTestFacts.Create("a"));
        var plan = Plan("coupled-a", order);
        Assert.Equal(ClaimOutcome.Admitted, Commit(journal, plan).Outcome);
        Install(journal, plan);
        var stored = Assert.Single(journal.LoadPlan(plan.Id, Ct).Progress);
        Assert.Equal(Stage.Completed, stored.View.Stage);
        Assert.Equal(Hunger, Assert.Single(stored.Action.PawnActivities!.Orders).Purpose);
        Assert.Equal(PurposeCondition.Open, InterventionPurposes.AtContinuation(Hunger, batch, ConstructionFixtures.NoTasks(100).Needs));
    }

    /// <summary>(a) The review reads the same classification from its own native ownership rows: a native job takeover of an
    /// installed intervention is replaced, not failed, and the coupled selection row reports it.</summary>
    [Fact]
    public void TheReviewClassifiesANativeJobTakeoverAsReplacedNotFailed()
    {
        using var journal = Open(WorkResourceTestFacts.Create("a"));
        var plan = Plan("coupled-a", Order("coupled-a", Hunger));
        Assert.Equal(ClaimOutcome.Admitted, Commit(journal, plan).Outcome);
        Install(journal, plan);
        var census = journal.WorkActions(World, Ct);
        static WorkResourcePawn Native(string state, string reason) =>
            new("a", "coupled-a/a", 1, state, "", "", 0) { LocalHour = 8, Method = "coupled-a", Reason = reason };
        var replaced = InterventionPurposes.ReviewOutcomes(census, [Native("failed", InterventionPurposes.NativeJobTakeover)]);
        Assert.Equal(new InterventionReviewOutcome(plan.Id, ["a:replaced"]), Assert.Single(replaced));
        Assert.Equal(["a:failed"], Assert.Single(InterventionPurposes.ReviewOutcomes(census, [Native("failed", "deadline")])).Outcomes);
        Assert.Equal(["a:accepted"], Assert.Single(InterventionPurposes.ReviewOutcomes(census, [Native("installed", "")])).Outcomes);
        // The review row and the continuation row are classified alike.
        var continuation = new ActivityOwnership("a", "coupled-a/a", "coupled-a", 1, "failed") { Reason = InterventionPurposes.NativeJobTakeover };
        Assert.Equal(continuation, InterventionPurposes.Ownership(Native("failed", InterventionPurposes.NativeJobTakeover)));

        var (selection, _) = InterventionJustificationTests.Practice(-95d);
        var attrs = ColonyReview.CoupledSelectionAttrs(selection, 100, false, null, replaced);
        var row = Assert.Single(Assert.IsType<Dictionary<string, object?>[]>(attrs["intervention_outcomes"]));
        Assert.Equal(plan.Id.Value, row["plan"]);
        Assert.Equal(["a:replaced"], Assert.IsType<string[]>(row["observed_outcomes"]));
    }

    /// <summary>(b) A prerequisite gone stale since the justification is refused at admission and at continuation.</summary>
    [Fact]
    public void StalePrerequisitesAreRefusedAtAdmissionAndAtContinuation()
    {
        // The ordered pawn was drafted when the review froze its evidence.
        var facts = WorkResourceTestFacts.Create("a");
        var drafted = facts with { ForecastObserved = facts.ForecastObserved with { WorkPawns = new WorkPawnsRead(EquatableArray.From(
            ((WorkPawnsRead)facts.ForecastObserved.WorkPawns).Pawns.Select(p => p with { Observed = p.Observed with { Drafted = true } }))) } };
        using (var journal = Open(drafted))
        {
            var refused = Commit(journal, Plan("coupled-drafted", Order("coupled-drafted", Hunger)));
            Assert.Equal(ClaimOutcome.Refused, refused.Outcome);
            Assert.Equal(InterventionPurposes.StalePrerequisitePrefix + "pawn_unavailable:a", refused.Refusal!.Reason);
        }
        // An observed input the stock no longer holds (10 rice stand where the task consumes 20) is refused before any claim.
        using (var journal = Open(WorkResourceTestFacts.Create("a")))
        {
            var task = new SelectedActivityTask("supply", "a", [], new(PawnActivityKind.Supply, "stack", "", "", 20, 900) { Container = "frame0" }, new(1, 1), 100)
            { Resource = "Rice", SiteDefinition = "TestWall", Source = new(1, 1), Inputs = [new("Rice", "stack", 20, new(1, 1), "observed") { Observed = true }] };
            var order = new PawnActivityOrder("a", "coupled-supply/a", "coupled-supply", 0, 1, false, [task.Step]) { Task = task.Id, Purpose = Hunger };
            var plan = new PlanSpec(new("coupled-supply-plan"), new(1),
                [DomainAction.NewPawnActivitiesAction(new("coupled-supply-plan-0"), new PawnActivities(1, [order]) { Tasks = [task] })]);
            var refused = Commit(journal, plan);
            Assert.Equal(ClaimOutcome.Refused, refused.Outcome);
            Assert.Equal(InterventionPurposes.StalePrerequisitePrefix + "input_consumed:supply:Rice", refused.Refusal!.Reason);
        }
        // At continuation: the pawn finished its first delivery and its next task's target no longer binds.
        var first = new SelectedActivityTask("first", "a", [], new PawnActivityStep(PawnActivityKind.Deliver, "rice", "", "", 1, 900).At(new Cell(2, 2)), new(1, 1), 0) { Resource = "Rice" };
        var next = new SelectedActivityTask("next", "a", [], new PawnActivityStep(PawnActivityKind.Deliver, "rice-2", "", "", 1, 900).At(new Cell(2, 2)), new(1, 1), 0) { Resource = "Rice" };
        var batch = new PawnActivities(1, [new PawnActivityOrder("a", "coupled-c/a", "coupled-c", 0, 1, false, [first.Step]) { Task = first.Id, Purpose = Hunger }])
            { Tasks = [first, next] };
        var read = ConstructionFixtures.NoTasks(100) with { SatisfiedTasks = ["first"],
            Pawns = [new ActivityTaskObservation("a", new("coupled-c/a", "coupled-c", 1, 0, "completed", "", 100))] };
        var reason = "";
        var release = CoordinatedActivitiesRules.ContinueCoordinatedActivity(batch, ActivityNeed.Owed, read, r => reason = r);
        Assert.True(Assert.Single(Assert.IsType<PawnActivities>(release).Orders).Release);
        Assert.Equal(InterventionPurposes.StalePrerequisitePrefix + "target_gone:next", reason);
    }

    /// <summary>(c) An open purpose is not issued twice: the same pawn and aim under another Method is refused while the first
    /// is pending or installed, so its work and inputs are never claimed or consumed twice.</summary>
    [Fact]
    public void AnOpenPurposeIsNotIssuedTwice()
    {
        using var journal = Open(WorkResourceTestFacts.Create("a", "b"));
        var first = Plan("coupled-first", Order("coupled-first", Hunger));
        Assert.Equal(ClaimOutcome.Admitted, Commit(journal, first).Outcome);
        var duplicate = Commit(journal, Plan("coupled-again", Order("coupled-again", Hunger with { EvidenceTick = 101 })));
        Assert.Equal(ClaimOutcome.Refused, duplicate.Outcome);
        Assert.Equal(InterventionPurposes.AlreadyOpenPrefix + "a:" + Hunger.Aim, duplicate.Refusal!.Reason);
        Install(journal, first);
        Assert.Equal(InterventionPurposes.AlreadyOpenPrefix + "a:" + Hunger.Aim,
            Commit(journal, Plan("coupled-installed", Order("coupled-installed", Hunger))).Refusal!.Reason);
        // Another pawn's purpose is not a duplicate.
        var other = Hunger with { SubjectPawn = "b" };
        Assert.Equal(ClaimOutcome.Admitted, Commit(journal, Plan("coupled-other", Order("coupled-other", other, "b"))).Outcome);
    }

    /// <summary>(d) Once a purpose's Method ends, a justification whose evidence is not newer than that end is stale.</summary>
    [Fact]
    public void AnEndedPurposeIsNotReissuedFromAStaleJustification()
    {
        using var journal = Open(WorkResourceTestFacts.Create("a"));
        var first = Plan("coupled-first", Order("coupled-first", Hunger));
        Assert.Equal(ClaimOutcome.Admitted, Commit(journal, first).Outcome);
        Install(journal, first);
        journal.ResolveActivityMethod(first.Id, World, new(120), [new("a", "", "", 0, "unowned")], Ct);
        Assert.True(journal.LoadPlan(first.Id, Ct).Retired);
        foreach (var tick in new long[] { 100, 120 })
        {
            var stale = Commit(journal, Plan("coupled-stale-" + tick, Order("coupled-stale-" + tick, Hunger with { EvidenceTick = tick })));
            Assert.Equal(ClaimOutcome.Refused, stale.Outcome);
            Assert.Equal(InterventionPurposes.StaleJustificationPrefix + "a:" + Hunger.Aim, stale.Refusal!.Reason);
        }
        // Evidence newer than the end is admitted.
        Assert.Equal(ClaimOutcome.Admitted, Commit(journal, Plan("coupled-fresh", Order("coupled-fresh", Hunger with { EvidenceTick = 121 }))).Outcome);
    }

    /// <summary>(d) Every retirement path records its tick: a purpose whose plan the review retired (cancelled before dispatch) is
    /// stale for evidence at or before that review, and newer evidence is admitted.</summary>
    [Fact]
    public void APurposeRetiredByTheReviewIsNotReissuedFromAStaleJustification()
    {
        var facts = WorkResourceTestFacts.Create("a");
        using var journal = Open(facts);
        var first = Plan("coupled-first", Order("coupled-first", Hunger));
        Assert.Equal(ClaimOutcome.Admitted, Commit(journal, first).Outcome);
        _ = journal.Cancel(first.Id, first.Actions[0].Id, Ct);
        var later = World with { Plan = new("review-2") };
        journal.Review(new() { Revision = journal.LoadReview(Ct).Revision, Current = later, Tick = new(130), Enabled = true,
            Policy = Rules.DefaultPlannerSettings(), Facts = facts with { Tick = new(130) } }, Ct);
        Assert.True(journal.LoadPlan(first.Id, Ct).Retired);
        foreach (var tick in new long[] { 100, 130 })
        {
            var stale = Commit(journal, Plan("coupled-stale-" + tick, Order("coupled-stale-" + tick, Hunger with { EvidenceTick = tick })), later);
            Assert.Equal(ClaimOutcome.Refused, stale.Outcome);
            Assert.Equal(InterventionPurposes.StaleJustificationPrefix + "a:" + Hunger.Aim, stale.Refusal!.Reason);
        }
        Assert.Equal(ClaimOutcome.Admitted,
            Commit(journal, Plan("coupled-fresh", Order("coupled-fresh", Hunger with { EvidenceTick = 131 })), later).Outcome);
    }

    /// <summary>(e) The shared comparison reaches Hands only with its own pawns' justified purposes: a detailed forecast that beats
    /// the native baseline without a concrete premise cannot start an order, and an exact order without a purpose is invalid.</summary>
    [Fact]
    public async Task TheComparisonToHandsPathCarriesEachOrdersJustifiedPurpose()
    {
        var (selection, candidates) = InterventionJustificationTests.Practice(-95d);
        var justified = Assert.IsType<InterventionJustification.Justified>(selection.Justification);
        var batch = Assert.IsType<PawnActivities>(ForecastCoupledSelection.StartCoupledActivity(selection, candidates, 1, "coupled-practice", 100));
        var assessed = Assert.Single(justified.Interventions);
        Assert.All(batch.Orders.Where(o => !o.Release), o => Assert.Equal(assessed.Purpose, o.Purpose));
        Assert.Equal(InterventionPurposeKind.CapabilityPractice, assessed.Purpose!.Kind);

        // The same winner under a refused premise starts nothing, and a hand-built exact order without a purpose is invalid.
        var refused = selection with { Justification = new InterventionJustification.Refused(InterventionJustification.MarginalGainWithoutPremise + ":pawn",
            new InterventionPremise.None(), justified.Response, justified.Displaced, justified.Stage, justified.Uncertainty) };
        Assert.Throws<DomainException>(() => ForecastCoupledSelection.StartCoupledActivity(refused, candidates, 1, "coupled-practice", 100));
        Assert.Throws<DomainException>(() => DomainAction.NewPawnActivitiesAction(new("bare-0"),
            batch with { Orders = [.. batch.Orders.Select(o => o with { Purpose = null })] }));

        var written = await Deliver(batch, "coupled-practice");
        Assert.All(written.Orders.Where(o => !o.Release), o => Assert.Equal(assessed.Purpose, o.Purpose));
    }

    /// <summary>Commits <paramref name="batch"/> as <paramref name="method"/>'s claimed plan and runs it through real Hands.</summary>
    private static async Task<PawnActivities> Deliver(PawnActivities batch, string method, ReviewFacts? evidence = null)
    {
        using var journal = Open(evidence ?? WorkResourceTestFacts.Create("pawn"));
        var plan = new PlanSpec(new(method + "-plan"), new(1), [DomainAction.NewPawnActivitiesAction(new(method + "-plan-0"), batch)]);
        Assert.Equal(ClaimOutcome.Admitted, journal.CommitClaimedMethod(AdminFamily.WorkAssignments, World, new(method), "", plan, Ct).Outcome);
        var native = new Native();
        using var hands = new Hands(new ExecutorJournal(journal), native, new Clock(), new Limits
        {
            MaxAge = TimeSpan.FromSeconds(30), RunTimeout = TimeSpan.FromSeconds(10), JournalTimeout = TimeSpan.FromSeconds(10),
        }, new PlanAuthorizer(journal, true));
        hands.UpdateAuthority(new() { Snapshot = World, Enabled = true });
        Assert.Null(Assert.Single(await hands.RunBatchAsync(plan.Id, [plan.Actions[0].Id], Ct)).Error);
        return Assert.Single(native.Written);
    }

    /// <summary>(f) Task 5.1/5.2: a pawn at zero food beside legal rice that no native job is serving is an observed shortfall. The
    /// generator's own eat binding, otherwise left to native operation, is re-admitted for that pawn and reaches Hands as a bounded
    /// self-service order carrying the NeedShortfall(food) purpose; under a conditional scenario nothing starts.</summary>
    [Fact]
    public async Task AnUnansweredHungerReachesHandsAsAJustifiedEatFromTheGenerator()
    {
        var (selection, candidates) = InterventionJustificationTests.FoodColony(0, stock: 20,
            InterventionJustificationTests.Assess(NativeFoodAssessmentTests.Facts(10, 9)));
        Assert.Equal(NativeNeedService.Unanswered, NativeNeedServices.Observe(candidates.Context.Needs, "pawn", "food"));
        var justified = Assert.IsType<InterventionJustification.Justified>(selection.Justification);
        Assert.Equal(new InterventionPremise.ObservedShortfall("food", "pawn", InterventionJustification.UnansweredAtZero), justified.Premise);
        var readmitted = Assert.Single(justified.Readmitted);
        Assert.Equal(PawnActivityKind.Eat, candidates.Context.Bindings[(readmitted, "pawn")].Kind);
        Assert.Null(ForecastCoupledSelection.StartCoupledActivity(selection with { Basis = ForecastComparisonBasis.ConditionalScenario },
            candidates, 1, "coupled-hunger", 100));
        var batch = Assert.IsType<PawnActivities>(ForecastCoupledSelection.StartCoupledActivity(selection, candidates, 1, "coupled-hunger", 100));
        var order = Assert.Single(batch.Orders, o => !o.Release);
        Assert.Contains(order.Steps, s => s.Kind == PawnActivityKind.Eat);
        var purpose = Assert.IsType<InterventionPurpose>(order.Purpose);
        Assert.Equal((InterventionPurposeKind.NeedShortfall, "food", "pawn"), (purpose.Kind, purpose.Subject, purpose.SubjectPawn));
        Assert.Equal(purpose, Assert.Single((await Deliver(batch, "coupled-hunger")).Orders, o => !o.Release).Purpose);

        // The same hungry pawn already walking to its native meal is answered: no premise, nothing re-admitted, nothing started.
        var served = candidates with { Context = candidates.Context with { Needs = candidates.Context.Needs with
            { Pawns = [.. candidates.Context.Needs.Pawns.Select(p => p with { ServedNeed = "food" })] } } };
        Assert.Equal(NativeNeedService.Available, NativeNeedServices.Observe(served.Context.Needs, "pawn", "food"));
        Assert.Empty(InterventionRules.Readmitted(selection.Selected.Plan, selection.BaselineVector, selection.SelectedVector, served.Context,
            InterventionJustificationTests.Catalog));
    }

    /// <summary>(g) Task 5.1: the generator's native harvest, otherwise left to native operation, is re-admitted for a pawn only under
    /// a credible food risk native settings answer too late, and reaches Hands as a bounded Work order carrying the FoodRisk purpose.
    /// Under a recovery that arrives in time the same harvest is withheld and nothing is dispatched.</summary>
    [Fact]
    public async Task ACredibleFoodRiskReachesHandsAsAJustifiedHarvestFromTheGenerator()
    {
        var facts = ForecastCoupledSelectionTests.Review(level: .9) with { Items = InterventionJustificationTests.Catalog };
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts, 300)!.Priced();
        (ForecastCoupledSelection, ForecastCoupledCandidates) Compared(NativeFoodAssessment food)
        {
            var candidates = generated with { Context = generated.Context with { NativeFood = food } };
            var request = ForecastCoupled.CoupledForecastRequestOf(new(100), 300d / GameTime.TicksPerDay, facts with { CoupledAlternatives = candidates },
                new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, []);
            return (ForecastCoupledSelection.CompareActivityOutcomes(request, candidates), candidates);
        }
        var harvest = generated.Context.Activities.Activities.First(t => t.NativeExecution && t.Prerequisites.Length == 0
            && generated.Context.Bindings.ContainsKey((t.Id, "pawn")));
        Assert.Equal(PawnActivityKind.Work, generated.Context.Bindings[(harvest.Id, "pawn")].Kind);
        var plan = new ForecastActivityPlan([new(harvest.Id, "pawn", 0, 0, 0, 100, 0, 100, 0, 0)], 100, 100);

        var (timely, recovering) = Compared(InterventionJustificationTests.Assess(NativeFoodAssessmentTests.Facts(10, 9)));
        Assert.Empty(CoordinatedActivityBinding.ForDispatch(plan, recovering.Context).Allocations);
        Assert.Null(InterventionRules.Gate([harvest.Id], plan, timely.BaselineVector, timely.BaselineVector, recovering.Context,
            InterventionJustificationTests.Catalog));

        var (risky, candidates) = Compared(InterventionJustificationTests.Assess(NativeFoodAssessmentTests.Facts(10, 12)));
        var justified = Assert.IsType<InterventionJustification.Justified>(InterventionRules.Gate([harvest.Id], plan, risky.BaselineVector,
            risky.BaselineVector, candidates.Context, InterventionJustificationTests.Catalog));
        Assert.IsType<InterventionPremise.CredibleRisk>(justified.Premise);
        Assert.Equal([harvest.Id], justified.Readmitted);
        var selection = risky with { Selection = risky.Selection with { Selected = [harvest.Id], Plan = plan }, Justification = justified };
        var batch = Assert.IsType<PawnActivities>(ForecastCoupledSelection.StartCoupledActivity(selection, candidates, 1, "coupled-harvest", 100));
        var order = Assert.Single(batch.Orders, o => !o.Release);
        Assert.Contains(order.Steps, s => s.Kind == PawnActivityKind.Work);
        Assert.Equal(InterventionPurposeKind.FoodRisk, order.Purpose!.Kind);
        Assert.Equal(order.Purpose, Assert.Single((await Deliver(batch, "coupled-harvest")).Orders, o => !o.Release).Purpose);
    }

    /// <summary>A stove whose cooking bill makes Meal from 8 stored rice, under the dispatcher's <paramref name="calibration"/>: the
    /// generator's own ingredient supply, cooking and delivery, all left to native bills and work settings, compared with the
    /// pawn well fed and the food owner's recovery arriving in time (no food premise).</summary>
    private static (ForecastCoupledSelection Selection, ForecastCoupledCandidates Candidates, ForecastActivity Supply) StalledKitchen(ProductionCalibration calibration)
    {
        var facts = ForecastCoupledSelectionTests.Review(level: .9, sourceCount: 0) with { Items = Kitchen };
        facts = facts with
        {
            Census = facts.Census with { Resources = [new Quantity { DefName = "Rice", Units = 8 }] },
            ForecastObserved = facts.ForecastObserved with
            {
                Coupled = facts.ForecastObserved.Coupled! with
                {
                    FoodSources = [new("rice-stack", "Rice", new(1, 1), 8, new FoodFreshness.Rots(1000.0))],
                    FoodConversions = [CoupledFixtures.Conversion("rice-stack", "pawn", "bench", "CookMeal", "Rice", 4, "Meal", 1, new(1, 1), new(1, 1), new(1, 1),
                        0.0, 10.0, 20.0, 10.0, 8, 8) with { RequiresHarvest = false, Bill = "bill", WorkGiver = "Cook" }],
                    FoodUses = [new("pawn", "Meal", new(1, 1), true, .8, 10.0, 1, new FoodFreshness.Rots(1000.0)) { Thoughts = [], Ingredient = "Rice" }],
                },
            },
        };
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts, 300)!.Priced();
        var candidates = generated with { Context = generated.Context with { ProductionCalibration = calibration,
            NativeFood = InterventionJustificationTests.Assess(NativeFoodAssessmentTests.Facts(10, 9)) } };
        var request = ForecastCoupled.CoupledForecastRequestOf(new(100), 300d / GameTime.TicksPerDay, facts with { CoupledAlternatives = candidates },
            new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, []);
        var supply = candidates.Context.Activities.Activities.First(t => t.Id.Contains("/supply/", StringComparison.Ordinal)
            && candidates.Context.Bindings.ContainsKey((t.Id, "pawn")));
        return (ForecastCoupledSelection.CompareActivityOutcomes(request, candidates), candidates, supply);
    }

    private static readonly ItemFacts Kitchen = new()
    {
        Nutrition = new Dictionary<Resource, double> { [new("Rice")] = .05, [new("Meal")] = .9 },
        Market = new Dictionary<Resource, double> { [new("Rice")] = 1.1, [new("Meal")] = 15 },
    };

    private static ProductionCalibration.Observed MealStall(UnmetReason reason) =>
        new([new ProductionStall("bill", new("Meal"), "bench", 2, reason, 90)]);

    /// <summary>(h) Task 5.3: a closed short window of the stove's Meal whose bill jobs showed pending ingredients is a demonstrated
    /// stall. The generator's ingredient supply feeding that bill, otherwise left to native operation, is re-admitted alone (its
    /// cooking never is) and reaches Hands as a bounded delivery carrying the ProductionStall purpose of Meal.</summary>
    [Fact]
    public async Task AStalledBillWithIngredientsPendingReachesHandsAsAJustifiedIngredientDelivery()
    {
        var (compared, candidates, supply) = StalledKitchen(MealStall(UnmetReason.IngredientsPending));
        var binding = candidates.Context.Bindings[(supply.Id, "pawn")];
        Assert.Equal(PawnActivityKind.Deliver, binding.Kind);
        Assert.Equal(["Meal"], binding.Feeds);
        var cook = candidates.Context.Bindings.Single(b => b.Value.Kind == PawnActivityKind.Recipe && b.Key.Pawn == "pawn"
            && candidates.Context.Activities.Activities.Single(t => t.Id == b.Key.Task).Prerequisites.Contains(supply.Id)).Key.Task;
        var plan = new ForecastActivityPlan([new(supply.Id, "pawn", 0, 0, 0, 100, 0, 100, 0, 0)], 100, 100);
        Assert.Empty(CoordinatedActivityBinding.ForDispatch(plan, candidates.Context).Allocations);
        // Bill work itself is never re-admitted, even under the stall it would answer.
        var withCook = plan with { Allocations = [.. plan.Allocations, new(cook, "pawn", 100, 100, 100, 200, 100, 200, 0, 0)] };
        Assert.Equal([supply.Id], InterventionRules.Readmitted(withCook, compared.BaselineVector, compared.BaselineVector, candidates.Context, Kitchen).Order());

        var justified = Assert.IsType<InterventionJustification.Justified>(InterventionRules.Gate([supply.Id], plan, compared.BaselineVector,
            compared.BaselineVector, candidates.Context, Kitchen));
        var stall = Assert.IsType<InterventionPremise.DemonstratedStall>(justified.Premise).Stall;
        Assert.Equal((new Resource("Meal"), UnmetReason.IngredientsPending), (stall.Product, stall.Reason));
        Assert.Equal([supply.Id], justified.Readmitted);
        var selection = compared with { Selection = compared.Selection with { Selected = [supply.Id], Plan = plan }, Justification = justified };
        var batch = Assert.IsType<PawnActivities>(ForecastCoupledSelection.StartCoupledActivity(selection, candidates, 1, "coupled-ingredients", 100));
        var order = Assert.Single(batch.Orders, o => !o.Release);
        Assert.All(order.Steps, s => Assert.NotEqual(PawnActivityKind.Recipe, s.Kind));
        Assert.Contains(order.Steps, s => s.Kind == PawnActivityKind.Deliver);
        Assert.Equal(new InterventionPurpose(InterventionPurposeKind.ProductionStall, "Meal", "", 90), order.Purpose);
        Assert.Equal(order.Purpose, Assert.Single((await Deliver(batch, "coupled-ingredients")).Orders, o => !o.Release).Purpose);
    }

    /// <summary>(i) Task 5.3: a stall whose bill has every usable bench (benches exhausted), or whose work merely stays unstarted,
    /// is answered by no exact order: the same ingredient supply is refused <c>stall_not_answered_by_orders</c>, nothing is
    /// re-admitted and nothing is dispatched.</summary>
    [Theory]
    [InlineData(UnmetReason.BenchesExhausted, "benches_exhausted")]
    [InlineData(UnmetReason.WorkUnstarted, "work_unstarted")]
    public void AStallNoOrderAnswersIsRefusedAndDispatchesNothing(UnmetReason reason, string cause)
    {
        var (compared, candidates, supply) = StalledKitchen(MealStall(reason));
        var plan = new ForecastActivityPlan([new(supply.Id, "pawn", 0, 0, 0, 100, 0, 100, 0, 0)], 100, 100);
        Assert.Empty(InterventionRules.Readmitted(plan, compared.BaselineVector, compared.BaselineVector, candidates.Context, Kitchen));
        Assert.Null(InterventionRules.Gate([supply.Id], plan, compared.BaselineVector, compared.BaselineVector, candidates.Context, Kitchen));
        var refused = Assert.IsType<InterventionJustification.Refused>(InterventionRules.Justify(plan, plan.Allocations,
            compared.BaselineVector, compared.BaselineVector, candidates.Context, Kitchen));
        Assert.Equal(InterventionJustification.StallNotAnsweredByOrders + ":Meal:" + cause + ":pawn", refused.Reason);
    }

    /// <summary>A wall blueprint at (30,30) whose one row is the pawn's: owing 20 wood carried from (10,10), or, when
    /// <paramref name="supplied"/>, holding its whole cost so only its finish remains. The generator's supply and finish are
    /// native execution; the context carries <paramref name="calibration"/> and the pawn is well fed (no food premise).</summary>
    private static (ForecastCoupledSelection Selection, ForecastCoupledCandidates Candidates, ForecastActivity Work) StalledSite(
        ConstructionCalibration calibration, bool supplied = false)
    {
        var facts = ForecastCoupledSelectionTests.Review(level: .9, sourceCount: 0) with { Items = Lumber };
        Row row = supplied
            ? new("blueprint", "Wall", new(30, 30), "pawn", "WoodLog", "", new(10, 10), 0, 10, "FinishFrames", null, null, 20) { SiteApproachTicks = 20 }
            : new("blueprint", "Wall", new(30, 30), "pawn", "WoodLog", "wood", new(10, 10), 20, 10, "FinishFrames", 0, 20, 20) { SiteApproachTicks = 0, AvailableInput = 20 };
        facts = facts with
        {
            Census = facts.Census with { Resources = [new Quantity { DefName = "WoodLog", Units = 20 }] },
            ForecastObserved = facts.ForecastObserved with
            {
                Coupled = facts.ForecastObserved.Coupled! with { Construction = ConstructionFixtures.NoConstruction(100) with { Options = [row] } },
            },
        };
        facts = WorkRules.ReviewWorkResources(facts, new ProductionBatchCensus.Read([]), new([]));
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts, 1900)!.Priced();
        var candidates = generated with { Context = generated.Context with { ConstructionCalibration = calibration,
            NativeFood = InterventionJustificationTests.Assess(NativeFoodAssessmentTests.Facts(10, 9)) } };
        var request = ForecastCoupled.CoupledForecastRequestOf(new(100), 1900d / GameTime.TicksPerDay, facts with { CoupledAlternatives = candidates },
            new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, []);
        var work = candidates.Context.Activities.Activities.First(t => candidates.Context.Bindings.TryGetValue((t.Id, "pawn"), out var b)
            && (supplied ? b.Job == "FinishFrame" : b.Kind == PawnActivityKind.Supply));
        return (ForecastCoupledSelection.CompareActivityOutcomes(request, candidates), candidates, work);
    }

    /// <summary>The admission evidence with the site's 20 wood stocked, so the delivery's observed input is not stale.</summary>
    private static ReviewFacts Stocked()
    {
        var facts = WorkResourceTestFacts.Create("pawn");
        return facts with { Census = facts.Census with { Resources = [new Quantity { DefName = "WoodLog", Units = 20 }] } };
    }

    private static readonly ItemFacts Lumber = new() { Market = new Dictionary<Resource, double> { [new("WoodLog")] = 1.2 } };

    private static ConstructionCalibration.Observed SiteStall(string site, ConstructionStallCause cause) =>
        new([new ConstructionProgressWindow(site, 0, 20, 0, 10, 10)], [new ConstructionStall(site, cause, 60010)]);

    /// <summary>(j) Task 5.4: a placed wall no native job targeted over a whole progress window while it owed wood is an observed
    /// construction stall. The generator's supply of that site, otherwise left to native construction, is re-admitted and reaches
    /// Hands as a bounded supply carrying the ConstructionStall purpose of the site; its finish, waiting on the delivery, is not.</summary>
    [Fact]
    public async Task AStalledSiteReachesHandsAsAJustifiedSupplyWithItsPurpose()
    {
        var (compared, candidates, supply) = StalledSite(SiteStall("blueprint", ConstructionStallCause.SupplyUnstarted));
        var binding = candidates.Context.Bindings[(supply.Id, "pawn")];
        Assert.Equal(("blueprint", PawnActivityKind.Supply), (binding.Container, binding.Kind));
        Assert.True(supply.NativeExecution);
        var plan = new ForecastActivityPlan([new(supply.Id, "pawn", 0, 0, 0, 100, 0, 100, 0, 0)], 100, 100);
        Assert.Empty(CoordinatedActivityBinding.ForDispatch(plan, candidates.Context).Allocations);
        var finish = candidates.Context.Bindings.Single(b => b.Value.Job == "FinishFrame").Key.Task;
        var withFinish = plan with { Allocations = [.. plan.Allocations, new(finish, "pawn", 100, 100, 100, 200, 100, 200, 0, 0)] };
        Assert.Equal([supply.Id], InterventionRules.Readmitted(withFinish, compared.BaselineVector, compared.BaselineVector, candidates.Context, Lumber).Order());

        var justified = Assert.IsType<InterventionJustification.Justified>(InterventionRules.Gate([supply.Id], plan, compared.BaselineVector,
            compared.BaselineVector, candidates.Context, Lumber));
        Assert.Equal(new InterventionPremise.StalledSite(new("blueprint", ConstructionStallCause.SupplyUnstarted, 60010)), justified.Premise);
        Assert.Equal(new NativeResponse.Unassessed("construction_native_response_unprojected"), justified.Response);
        Assert.Equal([supply.Id], justified.Readmitted);
        var selection = compared with { Selection = compared.Selection with { Selected = [supply.Id], Plan = plan }, Justification = justified };
        var batch = Assert.IsType<PawnActivities>(ForecastCoupledSelection.StartCoupledActivity(selection, candidates, 1, "coupled-site", 100));
        var order = Assert.Single(batch.Orders, o => !o.Release);
        Assert.Contains(order.Steps, s => s.Kind == PawnActivityKind.Supply && s.Target == binding.Target);
        Assert.Equal(new InterventionPurpose(InterventionPurposeKind.ConstructionStall, "blueprint", "", 60010), order.Purpose);
        Assert.Equal(order.Purpose, Assert.Single((await Deliver(batch, "coupled-site", Stocked())).Orders, o => !o.Release).Purpose);

        // The purpose stays open while the site's progress is unchanged, worked or not, and completes once its progress changes
        // after the stall closed or the site is no longer read; unobserved progress leaves it open.
        ConstructionCalibration Windows(params ConstructionProgressWindow[] windows) => new ConstructionCalibration.Observed(EquatableArray.From(windows), []);
        PurposeCondition At(ConstructionCalibration calibration) =>
            InterventionPurposes.AtReview(order.Purpose!, batch, candidates.Context with { ConstructionCalibration = calibration }, null);
        var open = new ConstructionProgressWindow("blueprint", 0, 20, 0, 10, 70000);
        Assert.Equal(PurposeCondition.Open, At(Windows(open)));
        Assert.Equal(PurposeCondition.Completed, At(Windows(open with { Delivered = 10, Opened = 70000 })));
        Assert.Equal(PurposeCondition.Completed, At(Windows()));
        Assert.Equal(PurposeCondition.Open, At(new ConstructionCalibration.Unobserved(ConstructionCalibration.UnobservedReason)));
    }

    /// <summary>(k) Task 5.4: a supplied frame no native job finished over a whole window re-admits its finish, which waits on
    /// nothing, under the ConstructionStall purpose of the site.</summary>
    [Fact]
    public async Task AStalledSuppliedFrameReachesHandsAsAJustifiedFinish()
    {
        var (compared, candidates, finish) = StalledSite(SiteStall("blueprint", ConstructionStallCause.FinishUnstarted), supplied: true);
        Assert.DoesNotContain(candidates.Context.Bindings.Values, b => b.Kind == PawnActivityKind.Supply);
        Assert.Empty(finish.Prerequisites);
        var plan = new ForecastActivityPlan([new(finish.Id, "pawn", 0, 0, 0, 100, 0, 100, 0, 0)], 100, 100);
        var justified = Assert.IsType<InterventionJustification.Justified>(InterventionRules.Gate([finish.Id], plan, compared.BaselineVector,
            compared.BaselineVector, candidates.Context, Lumber));
        Assert.Equal([finish.Id], justified.Readmitted);
        var selection = compared with { Selection = compared.Selection with { Selected = [finish.Id], Plan = plan }, Justification = justified };
        var batch = Assert.IsType<PawnActivities>(ForecastCoupledSelection.StartCoupledActivity(selection, candidates, 1, "coupled-frame", 100));
        var order = Assert.Single(batch.Orders, o => !o.Release);
        Assert.Contains(order.Steps, s => s is { Kind: PawnActivityKind.Work, Job: "FinishFrame", Target: "blueprint" });
        Assert.Equal(new InterventionPurpose(InterventionPurposeKind.ConstructionStall, "blueprint", "", 60010),
            Assert.Single((await Deliver(batch, "coupled-frame")).Orders, o => !o.Release).Purpose);
    }

    /// <summary>(l) Task 5.4: nothing else reaches Hands. No stall, another site's stall, or a stall whose cause the work does not
    /// answer re-admits nothing and is refused without a premise; unobserved progress is an unassessed premise.</summary>
    [Fact]
    public void ASiteWithoutAnAnsweredStallReadmitsNothing()
    {
        foreach (var calibration in new ConstructionCalibration[]
        {
            new ConstructionCalibration.Observed([], []),
            SiteStall("other", ConstructionStallCause.SupplyUnstarted),
            SiteStall("blueprint", ConstructionStallCause.FinishUnstarted),
        })
        {
            var (compared, candidates, supply) = StalledSite(calibration);
            var plan = new ForecastActivityPlan([new(supply.Id, "pawn", 0, 0, 0, 100, 0, 100, 0, 0)], 100, 100);
            Assert.Empty(InterventionRules.Readmitted(plan, compared.BaselineVector, compared.BaselineVector, candidates.Context, Lumber));
            Assert.Null(InterventionRules.Gate([supply.Id], plan, compared.BaselineVector, compared.BaselineVector, candidates.Context, Lumber));
            var refused = Assert.IsType<InterventionJustification.Refused>(InterventionRules.Justify(plan, plan.Allocations,
                compared.BaselineVector, compared.BaselineVector, candidates.Context, Lumber));
            Assert.IsType<InterventionPremise.None>(refused.Premise);
        }
        var (unobserved, context, work) = StalledSite(new ConstructionCalibration.Unobserved(ConstructionCalibration.UnobservedReason));
        var single = new ForecastActivityPlan([new(work.Id, "pawn", 0, 0, 0, 100, 0, 100, 0, 0)], 100, 100);
        Assert.Empty(InterventionRules.Readmitted(single, unobserved.BaselineVector, unobserved.BaselineVector, context.Context, Lumber));
        var unassessed = Assert.IsType<InterventionJustification.Refused>(InterventionRules.Justify(single, single.Allocations,
            unobserved.BaselineVector, unobserved.BaselineVector, context.Context, Lumber));
        Assert.Equal(InterventionJustification.PremiseUnassessedPrefix + ConstructionCalibration.UnobservedReason + ":pawn", unassessed.Reason);
    }
}
