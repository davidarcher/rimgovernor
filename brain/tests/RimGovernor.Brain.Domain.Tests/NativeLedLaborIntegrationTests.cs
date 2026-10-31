using RimGovernor.Brain.Executor;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Policy.Methods.Binding;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Store;
using Xunit;
using Hands = RimGovernor.Brain.Executor.Executor;
using Journal = RimGovernor.Brain.Store.Store;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Task 6.1 of native-led-labor-execution: the native-led path end to end through the real comparison, intervention gate,
/// journal admission and Hands, every step on one synchronized tick (the review, the batch, the evidence and Hands' inspection
/// all read tick 100). The wild potato colony of <see cref="ForecastCoupledSelectionTests.Review"/> has one native harvest no
/// setting performs; <see cref="NativeFoodAssessmentTests.Facts"/> sets whether the food owner's recovery arrives in time.
/// The scenarios other tests already cover are cited on each test.
/// </summary>
public sealed class NativeLedLaborIntegrationTests
{
    private static CancellationToken Ct => InterventionHandsTests.Ct;
    private static readonly GenerationSnapshot World = InterventionHandsTests.World;
    private const long Tick = 100;

    /// <summary>The harvest colony's generated alternatives, priced, under the food catalog.</summary>
    internal static (ReviewFacts Facts, ForecastCoupledCandidates Generated, ForecastActivity Harvest) HarvestColony()
    {
        var facts = ForecastCoupledSelectionTests.Review(level: .9) with { Items = InterventionJustificationTests.Catalog };
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts, 300)!.Priced();
        var harvest = generated.Context.Activities.Activities.First(t => t.NativeExecution && t.Prerequisites.Length == 0
            && generated.Context.Bindings.ContainsKey((t.Id, "pawn")));
        return (facts, generated, harvest);
    }

    /// <summary>The real shared comparison of the harvest colony under the food owner's <paramref name="food"/> assessment.</summary>
    internal static (ForecastCoupledSelection Selection, ForecastCoupledCandidates Candidates) CompareHarvestColony(NativeFoodAssessment food)
    {
        var (facts, generated, _) = HarvestColony();
        var candidates = generated with { Context = generated.Context with { NativeFood = food } };
        var request = ForecastCoupled.CoupledForecastRequestOf(new(Tick), 300d / GameTime.TicksPerDay, facts with { CoupledAlternatives = candidates },
            new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, []);
        return (ForecastCoupledSelection.CompareActivityOutcomes(request, candidates), candidates);
    }

    private static NativeFoodAssessment Risky => InterventionJustificationTests.Assess(NativeFoodAssessmentTests.Facts(10, 12));
    private static NativeFoodAssessment Timely => InterventionJustificationTests.Assess(NativeFoodAssessmentTests.Facts(10, 9));

    private static PlanSpec Plan(string method, PawnActivities batch) =>
        new(new(method + "-plan"), new(1), [DomainAction.NewPawnActivitiesAction(new(method + "-plan-0"), batch)]);

    private static NeedCommit Commit(Journal journal, NeedId need, string method, PawnActivities batch) =>
        journal.CommitClaimedMethod(need, World, new(method), "", Plan(method, batch), Ct);

    /// <summary>Runs <paramref name="method"/>'s admitted plan through real Hands and returns what reached the boundary.</summary>
    private static async Task<IReadOnlyList<PawnActivities>> Dispatch(Journal journal, string method)
    {
        var native = new InterventionHandsTests.Native();
        using var hands = new Hands(new ExecutorJournal(journal), native, new InterventionHandsTests.Clock(), new Limits
        {
            MaxAge = TimeSpan.FromSeconds(30), RunTimeout = TimeSpan.FromSeconds(10), JournalTimeout = TimeSpan.FromSeconds(10),
        }, new PlanAuthorizer(journal, true));
        hands.UpdateAuthority(new() { Snapshot = World, Enabled = true });
        Assert.Null(Assert.Single(await hands.RunBatchAsync(new(method + "-plan"), [new(method + "-plan-0")], Ct)).Error);
        return native.Written;
    }

    /// <summary>The comparison-selected harvest batch under a credible food risk, started at the review tick.</summary>
    private static PawnActivities RiskyHarvest(string method)
    {
        var (selection, candidates) = CompareHarvestColony(Risky);
        return Assert.IsType<PawnActivities>(ForecastCoupledSelection.StartCoupledActivity(selection, candidates, 1, method, Tick));
    }

    /// <summary>(a) False starvation: with the food owner's recovery arriving in time, the harvest colony's comparison finds no
    /// crisis to answer. The baseline's projected hunger is not a premise, nothing is selected or started, and the journal
    /// holds no Method for Hands to run. Also: <c>NativeBaselineComparisonTests.AModeledMealAloneIsNeitherACrisisNorADispatch</c>
    /// and <c>AvailableFoodWithoutASelectedMealLeavesBaselineStarvationUnassessedAndPrunesNothing</c>,
    /// <c>InterventionJustificationTests.ARescueFromANoModeledServiceBaselineIsRefused</c>.</summary>
    [Fact]
    public void AFalseStarvationIsNeitherACrisisNorADispatch()
    {
        var (selection, candidates) = CompareHarvestColony(Timely);
        Assert.Empty(selection.Selection.Selected);
        Assert.False(selection.Justification is InterventionJustification.Justified);
        Assert.Null(ForecastCoupledSelection.StartCoupledActivity(selection, candidates, 1, "coupled-false-starvation", Tick));
        using var journal = InterventionHandsTests.Open(WorkResourceTestFacts.Create("pawn"));
        Assert.Empty(journal.ActivityMethods(World, Ct));
    }

    /// <summary>(b) No production baseline: a stove whose stocked Meal bill native work serves, with no observed stall, gets no
    /// exact order: the ingredient supply is not re-admitted, the gate refuses it and nothing starts. Also:
    /// <c>NativeBaselineComparisonTests.OmittedNativeProductionWithObservedStockClaimsNeitherStallNorStarvation</c>,
    /// <c>InterventionBypassTests.ProductionBillWorkNeverReachesHands</c> and
    /// <c>InterventionHandsTests.AStallNoOrderAnswersIsRefusedAndDispatchesNothing</c>.</summary>
    [Fact]
    public void AStockedBillWithNativeWorkGetsNoExactOrder()
    {
        var (compared, candidates, supply) = InterventionHandsTests.StalledKitchen(new ProductionCalibration.Observed([]));
        var plan = new ForecastActivityPlan([new(supply.Id, "pawn", 0, 0, 0, 100, 0, 100, 0, 0)], 100, 100);
        Assert.Empty(CoordinatedActivityBinding.ForDispatch(plan, candidates.Context).Allocations);
        Assert.Empty(InterventionRules.Readmitted(plan, compared.BaselineVector, compared.BaselineVector, candidates.Context, InterventionHandsTests.Kitchen));
        Assert.Null(InterventionRules.Gate([supply.Id], plan, compared.BaselineVector, compared.BaselineVector, candidates.Context, InterventionHandsTests.Kitchen));
        Assert.Null(ForecastCoupledSelection.StartCoupledActivity(compared, candidates, 1, "coupled-stocked-bill", Tick));
    }

    /// <summary>(c) A concrete preventive intervention: under a credible food risk the comparison itself selects the harvest (its
    /// food-risk supply decides it), the gate justifies it as a CredibleRisk, and journal admission and Hands carry it to the
    /// boundary as one exact Harvest with its FoodRisk purpose. Also (hand-built plan):
    /// <c>InterventionHandsTests.ACredibleFoodRiskReachesHandsAsAJustifiedHarvestFromTheGenerator</c>.</summary>
    [Fact]
    public async Task ACredibleFoodRiskSelectsAndDispatchesThePreventiveHarvest()
    {
        var (_, _, harvest) = HarvestColony();
        var (selection, candidates) = CompareHarvestColony(Risky);
        var justified = Assert.IsType<InterventionJustification.Justified>(selection.Justification);
        Assert.IsType<InterventionPremise.CredibleRisk>(justified.Premise);
        Assert.Contains(selection.Selection.Plan!.Allocations, a => a.Activity == harvest.Id);
        Assert.Contains(harvest.Id, justified.Readmitted);
        Assert.Equal(ForecastCoupledSelection.FoodRiskSupplyStage, justified.Stage);
        var batch = Assert.IsType<PawnActivities>(ForecastCoupledSelection.StartCoupledActivity(selection, candidates, 1, "coupled-preventive", Tick));

        using var journal = InterventionHandsTests.Open(WorkResourceTestFacts.Create("pawn"));
        Assert.Equal(ClaimOutcome.Admitted, Commit(journal, AdminFamily.WorkAssignments, "coupled-preventive", batch).Outcome);
        var order = Assert.Single(Assert.Single(await Dispatch(journal, "coupled-preventive")).Orders, o => !o.Release);
        Assert.NotEmpty(order.Steps);
        Assert.All(order.Steps, step => Assert.Equal((PawnActivityKind.Work, "Harvest"), (step.Kind, step.Job)));
        Assert.Equal(InterventionPurposeKind.FoodRisk, order.Purpose!.Kind);
    }

    /// <summary>(d) A stale premise after native progress: once native stock clears the risk, the installed harvest's FoodRisk purpose
    /// reads completed, the Method resolves, the next review's comparison selects nothing, and the earlier review's batch is refused
    /// at admission as a stale justification; evidence newer than the end is admitted. Also:
    /// <c>InterventionJustificationTests.FreshNativeProgressInvalidatesAStalePremise</c>,
    /// <c>InterventionHandsTests.AnEndedPurposeIsNotReissuedFromAStaleJustification</c> and
    /// <c>APurposeRetiredByTheReviewIsNotReissuedFromAStaleJustification</c>.</summary>
    [Fact]
    public void ANativeRecoveryRetiresTheHarvestAndItsPremiseGoesStale()
    {
        var batch = RiskyHarvest("coupled-harvest");
        using var journal = InterventionHandsTests.Open(WorkResourceTestFacts.Create("pawn"));
        var plan = Plan("coupled-harvest", batch);
        Assert.Equal(ClaimOutcome.Admitted, Commit(journal, AdminFamily.WorkAssignments, "coupled-harvest", batch).Outcome);
        InterventionHandsTests.Install(journal, plan);

        // Native progress: the food owner now reviews no response needed.
        var recovered = InterventionJustificationTests.Assess(NativeFoodAssessmentTests.Facts(30, 9));
        Assert.False(recovered.NeedsResponse);
        var (next, candidates) = CompareHarvestColony(recovered);
        var purpose = Assert.Single(InterventionPurposes.Purposes(batch));
        Assert.Equal(PurposeCondition.Completed, InterventionPurposes.AtReview(purpose, batch, candidates.Context, next.BaselineVector));
        Assert.Empty(next.Selection.Selected);
        Assert.Null(ForecastCoupledSelection.StartCoupledActivity(next, candidates, 1, "coupled-next", Tick + 20));

        journal.ResolveActivityMethod(plan.Id, World, new(Tick + 20), [new("pawn", "", "", 0, "unowned")], Ct);
        var stale = Commit(journal, AdminFamily.WorkAssignments, "coupled-again", batch with { Orders =
            [.. batch.Orders.Select(o => o with { Method = "coupled-again", Assignment = "coupled-again/" + o.Pawn })] });
        Assert.Equal(ClaimOutcome.Refused, stale.Outcome);
        Assert.StartsWith(InterventionPurposes.StaleJustificationPrefix + "pawn:", stale.Refusal!.Reason);
        var fresh = batch with { Orders = [.. batch.Orders.Select(o => o with { Method = "coupled-fresh", Assignment = "coupled-fresh/" + o.Pawn,
            Purpose = o.Purpose! with { EvidenceTick = Tick + 21 } })] };
        Assert.Equal(ClaimOutcome.Admitted, Commit(journal, AdminFamily.WorkAssignments, "coupled-fresh", fresh).Outcome);
    }

    /// <summary>(e) A cross-family claim race: while the justified harvest holds its pawn, another family's exact order for the same
    /// pawn is a typed claim conflict naming the holder; it stores no Method, and Hands dispatches the harvest once. Also:
    /// <c>ClaimAdmissionTests</c> (claim conflicts within and across owners, e.g. <c>GearDressedTwiceForOnePawnIsRefused</c>) and
    /// <c>InterventionHandsTests.AnOpenPurposeIsNotIssuedTwice</c>.</summary>
    [Fact]
    public async Task ACrossFamilyClaimRaceIsATypedConflictWithNoDoubleDispatch()
    {
        var batch = RiskyHarvest("coupled-harvest");
        using var journal = InterventionHandsTests.Open(WorkResourceTestFacts.Create("pawn"));
        Assert.Equal(ClaimOutcome.Admitted, Commit(journal, AdminFamily.WorkAssignments, "coupled-harvest", batch).Outcome);
        var rival = new PawnActivities(1, [new PawnActivityOrder("pawn", "food-rival/pawn", "food-rival", 0, 1, false,
            [new PawnActivityStep(PawnActivityKind.Hold, "", "", "", 0, 900)]) { Purpose = new(InterventionPurposeKind.NeedShortfall, "food", "pawn", Tick) }]);
        var raced = Commit(journal, FoodRules.FoodSupplyNeed, "food-rival", rival);
        Assert.Equal(ClaimOutcome.Conflict, raced.Outcome);
        Assert.Equal("pawn/pawn", raced.Conflict!.Claim);
        Assert.Equal("coupled-harvest", raced.Conflict.Method.Value);
        Assert.Empty(journal.LoadNeed(FoodRules.FoodSupplyNeed, World, Ct).Methods);
        var written = Assert.Single(await Dispatch(journal, "coupled-harvest"));
        Assert.Equal("coupled-harvest", Assert.Single(written.Orders).Method);
    }

    /// <summary>(f) A live-care race: the harvest was justified at the review, but by admission its pawn has started native baby
    /// care. The frozen admission evidence reads the carer unavailable, the batch is refused as a stale prerequisite and nothing
    /// reaches Hands. Also: <c>BabyFeedingResponseTests.SharedAdmissionWithholdsCareCapacityAndDoesNotRefundCarriedInputs</c> and
    /// <c>InterventionHandsTests.StalePrerequisitesAreRefusedAtAdmissionAndAtContinuation</c>.</summary>
    [Fact]
    public void ALiveCareRaceRefusesTheHarvestAsAStalePrerequisite()
    {
        var batch = RiskyHarvest("coupled-harvest");
        var baby = BabyFeedingResponseTests.Baby("baby", BabyFeedingResponseTests.Nursing());
        baby = baby with { Care = baby.Care with { Active = new("pawn", BabyCareKind.Breastfeed, new(0, 0), new(0, 0),
            BabyFeedingResponseTests.Here, ActivitySocialMode.Normal) } };
        var facts = WorkResourceTestFacts.Create("pawn");
        facts = facts with { ForecastObserved = facts.ForecastObserved with { Needs = BabyFeedingResponseTests.Read(baby) } };
        using var journal = InterventionHandsTests.Open(facts);
        var raced = Commit(journal, AdminFamily.WorkAssignments, "coupled-harvest", batch);
        Assert.Equal(ClaimOutcome.Refused, raced.Outcome);
        Assert.Equal(InterventionPurposes.StalePrerequisitePrefix + "pawn_unavailable:pawn", raced.Refusal!.Reason);
        Assert.Empty(journal.ActivityMethods(World, Ct));
    }
}
