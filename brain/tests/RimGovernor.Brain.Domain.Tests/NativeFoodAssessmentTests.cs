using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts.Observations;
using Xunit;
using FoodRunway = RimGovernor.Brain.Policy.FoodRunway;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class NativeFoodAssessmentTests
{
    internal static ReviewFacts Facts(double days = 10, double lead = 12)
    {
        var supply = FoodReserveStateTests.Provision(days);
        var plan = FoodReserveStateTests.RecoveryPlan(supply, lead);
        var facts = ColonyFactsCoreTests.Reading().Facts;
        var observed = ForecastCoupledSelectionTests.Review(sourceCount: 0).ForecastObserved;
        observed = observed with
        {
            Needs = observed.Needs! with { Pawns = [observed.Needs.Pawns[0] with { Pawn = "a", Targets = [] }] },
            WorkPawns = new([Worker()]),
            Coupled = observed.Coupled! with { FoodUses = [], FoodConversions = [CoupledFixtures.Conversion("rice", "a", "bench", "recipe", "Rice", 4, "meal", 1,
                new(1, 1), new(1, 1), new(1, 1), 0, 1, 10, 1, 4, 4) with { RequiresHarvest = false, WorkGiver = "observed-cook", Bill = "bill" }] },
        };
        return facts with
        {
            Tick = new(100), AvailableNeeds = [AdminFamily.WorkAssignments, FoodRules.FoodSupplyNeed],
            Census = facts.Census with { WorkResources = [new WorkResourcePawn("a", "", 0, "", "", "", 0) { LocalHour = 6 }] },
            FoodSupply = supply, CombinedFoodSupply = supply, FoodDays = new FoodRunway.Days(days), FoodPlan = plan,
            FoodReserve = FoodReserveRules.ReviewFoodReserve(supply, null, 5, 3, FoodReserveRules.FoodDeliveryDays(plan)),
            ForecastObserved = observed,
            WorkSpeeds = new() { GiverWork = new Dictionary<string, string> { ["observed-cook"] = Wire.Name(WorkType.Cooking) } },
        };
    }

    internal static WorkPawn Worker(int cooking = 10) => PawnFixtures.Worker("a") with
    {
        Work = PawnFixtures.WorkTable(true, [WorkPriority.Planned(WorkType.Cooking, 3) with { Skill = "Cooking", Order = 10 },
            WorkPriority.Planned(WorkType.Construction, 1) with { Skill = "Construction", Order = 5 },
            WorkPriority.Planned(WorkType.Doctor, 1) with { Skill = "Medicine", Order = 20 },
            WorkPriority.Planned(WorkType.Firefighter, 1), WorkPriority.Planned(WorkType.Childcare, 1)]),
        Observed = PawnFixtures.Row("a") with
        {
            Biography = PawnFixtures.Row("a").Biography with { Skills = [
                new() { DefName = "Cooking", Level = cooking, Passion = Passion.None },
                new() { DefName = "Construction", Level = 20, Passion = Passion.None },
                new() { DefName = "Medicine", Level = 10, Passion = Passion.None }] },
            Job = new() { DefName = "observed-build", WorkTypeDefName = Wire.Name(WorkType.Construction),
                TargetA = new() { Target = new RimGovernor.Brain.GameFacts.Common.Unavailable { Reason = RimGovernor.Brain.GameFacts.Common.UnavailableReason.NotApplicable } } },
        },
    };

    private static NativeFoodAssessment Assess(ReviewFacts f) => NativeFoodAssessment.Of(f.FoodDays, f.FoodPlan, f.FoodReserve);
    private static int Priority(WorkDecision decision, WorkType work) => Assert.Single(decision.Assignments).Priorities.Single(p => p.Work == work).Priority;

    [Theory]
    [InlineData(10, 9, NativeFoodRisk.NoDemonstratedShortfall)]
    [InlineData(10, 10, NativeFoodRisk.RecoveryGap)]
    [InlineData(10, 12, NativeFoodRisk.RecoveryGap)]
    [InlineData(2, 1, NativeFoodRisk.SupplyBelowMinimum)]
    public void StockAndSupportedRecoveryDrivePreferencesBeforeHarm(double days, double lead, NativeFoodRisk risk)
    {
        var facts = Facts(days, lead);
        var assessment = Assert.IsType<NativeFoodAssessment.Reviewed>(Assess(facts));
        Assert.Equal(days, assessment.RunwayDays);
        Assert.Equal(3, assessment.MinimumDays);
        Assert.Equal(risk, assessment.Risk);
        var demand = WorkRules.WorkDemand(facts, true);
        var decision = WorkRules.PlanWork([Worker()], [], demand);
        var baseline = WorkRules.PlanWork([Worker()], [], new());
        if (risk == NativeFoodRisk.NoDemonstratedShortfall) Assert.Empty(demand.FoodWork);
        else
        {
            Assert.Equal([WorkType.Cooking], demand.FoodWork);
            Assert.Equal(1, Priority(decision, WorkType.Cooking));
            Assert.Equal(2, Priority(decision, WorkType.Construction));
        }
        Assert.All(decision.Coverage, row => Assert.Equal(
            decision.Assignments.Count(a => a.Priorities.Any(p => p.Work == row.Work && p.Priority == 1)), row.Owners));
        Assert.Equal(Priority(baseline, WorkType.Doctor), Priority(decision, WorkType.Doctor));
        Assert.Equal(1, Priority(decision, WorkType.Firefighter));
        Assert.Equal(1, Priority(decision, WorkType.Childcare));
        Assert.All(WorkRules.NativeSettingsChanges([Worker()], decision.Assignments, facts.ForecastObserved.Needs),
            change => Assert.Equal(new PawnId("a"), change.Pawn));
    }

    [Fact]
    public void FreshStockClearsOldRiskAndUnsupportedRecoveryIsNeitherFailureNorSuccess()
    {
        var facts = Facts();
        Assert.True(Assess(facts).NeedsResponse);
        // The latest stock read wins over the old plan's stock scenario.
        var replenished = facts with { FoodDays = new FoodRunway.Days(20), FoodReserve = new FoodReserveState.Disabled() };
        Assert.Equal(NativeFoodRisk.NoDemonstratedShortfall, Assert.IsType<NativeFoodAssessment.Reviewed>(Assess(replenished)).Risk);
        Assert.Empty(WorkRules.WorkDemand(replenished, true).FoodWork);
        var unknown = new FoodPlanEntry { Channel = CandidateRules.FoodCandidate(CandidateKind.Crop, "uncaptured",
            new YieldSupply.Unpriced(), CandidateState.Designated, new CandidateLead.Unscheduled(), new CandidateWork.PerDay(100)), Decision = FoodPlanDecision.Hold };
        var unconfirmed = facts with { FoodPlan = facts.FoodPlan! with { Unknown = [unknown] }, FoodReserve = new FoodReserveState.Disabled() };
        var assessment = Assert.IsType<NativeFoodAssessment.Reviewed>(Assess(unconfirmed));
        Assert.IsType<FoodDeliveries.Unconfirmed>(assessment.Recovery);
        Assert.False(assessment.NeedsResponse);
        Assert.True(Assess(unconfirmed with { FoodDays = new FoodRunway.Days(2) }).NeedsResponse);
        Assert.False(Assess(facts with { FoodPlan = facts.FoodPlan! with { Portfolio = [], Unknown = [] }, FoodReserve = new FoodReserveState.Disabled() }).NeedsResponse);
    }

    [Fact]
    public void UnavailableAssessmentOrWorkMetadataDoesNotInventDemandOrBlockOtherPriorities()
    {
        var facts = Facts();
        Assert.IsType<NativeFoodAssessment.Unassessed>(Assess(facts with { FoodPlan = null }));
        Assert.IsType<NativeFoodAssessment.Unassessed>(Assess(facts with { FoodDays = null }));
        Assert.IsType<NativeFoodAssessment.NoConsumers>(Assess(facts with { FoodDays = new FoodRunway.NoEaters() }));
        foreach (var incomplete in new[] { facts with { FoodPlan = null }, facts with { WorkSpeeds = new() }, facts with { AvailableNeeds = [] } })
        {
            var demand = WorkRules.WorkDemand(incomplete, true);
            Assert.Empty(demand.FoodWork);
            var decision = WorkRules.PlanWork([Worker()], [], demand);
            Assert.True(decision.Planned);
            Assert.Equal(1, Priority(decision, WorkType.Construction));
        }
        var shortDemand = WorkRules.WorkDemand(facts, true);
        var incapable = WorkRules.PlanWork([Worker(0)], [], shortDemand);
        // The existing last-resort cook may still own Cooking below its safety floor;
        // a food-risk boost must not demote that pawn's other work as if it cleared the floor.
        Assert.Equal(Priority(WorkRules.PlanWork([Worker(0)], [], new()), WorkType.Cooking), Priority(incapable, WorkType.Cooking));
        Assert.Equal(1, Priority(incapable, WorkType.Construction));
    }

    [Fact]
    public void SharedComparisonRetainsTheFoodAssessmentWithoutManufacturingAForcedRescue()
    {
        var facts = Facts(10, 9);
        facts = facts with { ForecastObserved = facts.ForecastObserved with { Coupled = facts.ForecastObserved.Coupled! with { FoodConversions = [] } } };
        facts = WorkRules.ReviewWorkResources(facts, new ProductionBatchCensus.Read([]), new([]));
        var candidates = ForecastCoupledCandidates.GenerateCoupledActivities(facts, 300)!.Priced();
        facts = facts with { CoupledAlternatives = candidates };
        var request = ForecastCoupled.CoupledForecastRequestOf(new(100), 300d / GameTime.TicksPerDay,
            facts, new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, []);
        var selection = ForecastCoupledSelection.CompareActivityOutcomes(request, candidates);
        var assessment = Assert.IsType<NativeFoodAssessment.Reviewed>(selection.BaselineVector.NativeFood);
        Assert.Equal(NativeFoodRisk.NoDemonstratedShortfall, assessment.Risk);
        Assert.Equal(candidates.Context.NativeFood, selection.SelectedVector.NativeFood);
        Assert.IsType<ForecastTerm.Unassessed>(selection.BaselineVector.Urgent[0]);
        Assert.Empty(selection.Selection.Selected);
        Assert.Null(ForecastCoupledSelection.StartCoupledActivity(selection, candidates, 1, "no-false-rescue", 100));
    }
}
