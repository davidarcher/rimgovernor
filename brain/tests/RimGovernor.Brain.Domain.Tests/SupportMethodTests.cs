using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class SupportMethodTests
{
    private const double Horizon = 300;
    private static ReviewFacts Facts(SupportActivityRead support, bool cooking = false)
    {
        var facts = WorkResourceTestFacts.Create("pawn", "helper");
        var basePawn = ForecastCoupledSelectionTests.Review(level: .1, fall: cooking ? .001 : 0).ForecastObserved.Needs!.Pawns[0];
        facts = facts with
        {
            Census = facts.Census with { Resources = [new RimGovernor.Brain.GameFacts.Observations.Quantity { DefName = "Rice", Units = 4 }, new RimGovernor.Brain.GameFacts.Observations.Quantity { DefName = "Chunk", Units = 20 }] },
            ForecastObserved = facts.ForecastObserved with
            {
                Needs = NeedsFixtures.Empty with { Tick = new(100), Pawns = [basePawn, basePawn with { Pawn = "helper", Needs =
                    [NeedsFixtures.Rate("food", 1d, 0d, .3), NeedsFixtures.Rate("rest", 1d, 0d, .3)] }] },
                Coupled = CoupledFixtures.Empty with {
                    Research = ActivityContinuationFixtures.NoResearch, Hunt = HuntFixtures.NoHunt, Clothing = ClothingFixtures.NoClothing, Construction = ConstructionFixtures.NoConstruction(),
                    Tick = 100, Support = support,
                    FoodSources = cooking ? [new("rice", "Rice", new(1, 1), 4, new FoodFreshness.Rots(1000d))] : [],
                    FoodConversions = cooking ? [CoupledFixtures.Conversion("rice", "pawn", "bench", "CookMeal", "Rice", 4, "Meal", 1,
                        new(1, 1), new(1, 1), new(1, 1), 0d, 1d, 10d, 1d, 4, 4)
                        with { RequiresHarvest = false, Bill = "bill", WorkGiver = "Cook" }] : [],
                    FoodUses = cooking ? [new("pawn", "Meal", new(1, 1), true, .8, 10d, 1, new FoodFreshness.Rots(1000d)) { Thoughts = [], Ingredient = "Rice" },
                        new("pawn", "Rice", new(1, 1), false, .1, 10d, 4, new FoodFreshness.Rots(1000d)) { Ingredient = "", Thoughts = [] }] : [],
                    Routes = [new("pawn", new(1, 1), new(1, 1), new RouteTicks.Reachable(0d)) { FromStart = true },
                        new("helper", new(1, 1), new(1, 1), new RouteTicks.Reachable(0d)) { FromStart = true }]
                }
            }
        };
        facts = WorkRules.ReviewWorkResources(facts, new ProductionBatchCensus.Read([]), new([]));
        return facts with { CoupledAlternatives = ForecastCoupledCandidates.GenerateCoupledActivities(facts, Horizon)?.Priced() };
    }
    private static ForecastCoupledSelection Select(ReviewFacts facts, ForecastSearchBudget? budget = null) => ForecastCoupledSelection.CompareActivityOutcomes(
        ForecastCoupled.CoupledForecastRequestOf(new(100), Horizon / GameTime.TicksPerDay, facts,
            new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, []), facts.CoupledAlternatives!, budget);
    private static SelectedActivityTask[] Tasks(ReviewFacts facts, ForecastCoupledSelection selection) =>
        ForecastCoupledSelection.StartCoupledActivity(selection, facts.CoupledAlternatives!, 1, "method", 100)?.Tasks.ToArray() ?? [];

    [Fact]
    public void ACookedMealIsExposedToItsBenchRoomsPoisonChance()
    {
        var read = CoupledFixtures.NoSupport with { Demands = [CoupledFixtures.Demand("bench", "bench", "bench", new(1, 1), .02, 0d)] };
        var facts = Facts(read, true); var result = Select(facts);
        Assert.Contains(Tasks(facts, result), t => t.Step.Kind == PawnActivityKind.Recipe);
        Assert.Equal(.02, result.SelectedVector.Support.FoodPoisonExposure, 9);
    }

    [Fact]
    public void UrgentCareIsTendedAndPricedByItsFirstCareTicks()
    {
        var read = CoupledFixtures.NoSupport with { Demands = [CoupledFixtures.Demand("care", "care", "patient", new(1, 1), default, .1)],
            Workers = [new("care", "pawn", "patient", "TendPatient", "Tend", new(1, 1), 0d, 20d) { Medicine = null, Treatment = CoupledFixtures.Tend() }] };
        var facts = Facts(read); var result = Select(facts);
        Assert.Contains(Tasks(facts, result), t => t.Step.Job == "TendPatient");
        Assert.Equal(20d, result.SelectedVector.Support.UrgentFirstCareTicks);
    }

    [Fact]
    public void MedicalServiceKeepsTheNativeStandingCellBesideThePatient()
    {
        var standing = new ServicePositionRead.Located(new(1, 2));
        var read = CoupledFixtures.NoSupport with {
            Demands = [CoupledFixtures.Demand("care", "care", "patient", new(1, 1), 0, .1)],
            Workers = [new("care", "pawn", "patient", "TendPatient", "Tend", new(1, 1), 0d, 20d)
                { ServicePosition = standing, Medicine = null, Treatment = CoupledFixtures.Tend() }] };
        var facts = Facts(read);
        var context = facts.CoupledAlternatives!.Context;
        var option = Assert.Single(context.Activities.Options, o => o.Activity.StartsWith("support/care/", StringComparison.Ordinal));
        Assert.Equal(standing, option.ServicePosition);
        Assert.Equal("cell/1/1", Assert.Single(context.Activities.Activities, a => a.Id == option.Activity).Location);
    }
    private static SupportActivityRead Delivery(int required, double duration = 10) => CoupledFixtures.NoSupport with {
        Deliveries = [new("bill:stone:Chunk", "helper", "useful-chunk", "Chunk", "bench", "stone", new(2, 2), new(1, 1), required, 20, 20, 0d, duration)]
    };
    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public void DemandSelectsSmallOrBatchQuantityWithoutFullStackGate(int required)
    {
        var facts = Facts(Delivery(required)); var result = Select(facts); var delivery = Assert.Single(Tasks(facts, result));
        Assert.Equal(required, delivery.Step.Count); Assert.Equal("useful-chunk", delivery.Step.Target);
        Assert.Equal(0d, result.SelectedVector.Support.UnsuppliedDemand);
        Assert.Equal(0d, ForecastActivities.StockChangesOf(facts.CoupledAlternatives!.Context.Activities, result.Selected.Plan).Where(c => c.Resource.Value == "Chunk").Sum(c => c.Units));
        Assert.Equal(0, result.Selected.Terminal!.Drawn.GetValueOrDefault(new Resource("Chunk")));
    }
    [Fact]
    public void ChangedDemandAndInfeasibleTravelRetainNoTrip()
    {
        Assert.Empty(Select(Facts(Delivery(0))).Selection.Selected);
        Assert.Empty(Select(Facts(Delivery(1, 400))).Selection.Selected);
    }
    [Fact]
    public void BoundedSearchExposesIncompleteComparison()
    {
        var result = Select(Facts(Delivery(8)), new(1));
        Assert.True(result.Selection.SearchTruncated);
        // One discretionary evaluation was started and no more (#3722): the first root screened was evaluated once, led, and nothing else was
        // compared with it.
        Assert.Equal(1, result.OutcomeEvaluations - result.BaselineEvaluations);
        Assert.Contains(ForecastCoupledSelection.OutcomeEvaluationsExhausted, result.Selection.Findings);
        Assert.Single(result.Selection.Selected);
        Assert.Equal("seed", result.SelectedPhase);
    }

    [Fact]
    public void ExactMedicineChoiceConsumesOneObservedLotAndPreservesIntent()
    {
        var read = CoupledFixtures.NoSupport with {
            Demands = [CoupledFixtures.Demand("care", "care", "patient", new(1, 1), default, 0d)],
            Workers = [new("care", "pawn", "patient", "TendPatient", "Tend", new(1, 1), 0d, 20d)
                { Medicine = null, Treatment = CoupledFixtures.Tend(.2) },
                new("care", "pawn", "patient", "TendPatient", "Tend", new(1, 1), 0d, 20d)
                { Medicine = new("medicine", "Chunk", 1, new(1, 1), 20), Treatment = CoupledFixtures.Tend(.8) }] };
        var facts = Facts(read); var result = Select(facts); var task = Assert.Single(Tasks(facts, result));
        Assert.Equal("medicine", task.Step.Medicine); Assert.Equal("care", task.Step.SupportDemand);
        Assert.Equal(1, Assert.Single(task.Inputs).Count);
        Assert.Equal(-1d, ForecastActivities.StockChangesOf(facts.CoupledAlternatives!.Context.Activities, result.Selected.Plan).Where(c => c.Resource.Value == "Chunk").Sum(c => c.Units));
        Assert.Equal(1, result.Selected.Terminal!.Drawn[new Resource("Chunk")]);
    }

    [Theory]
    [InlineData(false, 100)]
    [InlineData(true, 10)]
    [InlineData(true, 100)]
    public void CareRoomFactorRequiresAPendingNativeCheckThatOutlastsTheTend(bool pending, double checkTicks)
    {
        var read = CoupledFixtures.NoSupport with
        {
            Demands = [CoupledFixtures.Demand("care", "care", "patient", new(1, 1), default, 0d) with { InfectionChanceFactor = 2d }],
            Workers = [new("care", "pawn", "patient", "TendPatient", "Tend", new(1, 1), 0d, 20d)
                { Medicine = null, Treatment = CoupledFixtures.Tend(.5, pending ? new InfectionCheck.Pending(checkTicks) : new InfectionCheck.None()) }]
        };
        var facts = Facts(read); var selection = Select(facts);
        Assert.Contains(Tasks(facts, selection), t => t.Step.Job == "TendPatient");
        // A batch with no pending check, or one that fires before the tend ends, leaves the room out: no factor is unknown.
        Assert.Equal(new CareRoomRead.Assessed(pending && checkTicks == 100 ? 2d : 0d), selection.SelectedVector.Support.CareRoom);
    }

    [Fact]
    public void SurgeryUsesItsOwnQueuedServiceAndNativeRoomMultiplier()
    {
        var read = CoupledFixtures.NoSupport with { Demands = [CoupledFixtures.Demand("surgery", "surgery", "patient", new(1, 1), default, 0d)
                with { SurgerySuccessFactor = .6 }], Workers =
            [new("surgery", "pawn", "patient", "DoBill", "Doctor", new(1, 1), 0d, 20d)
                { Medicine = null, Treatment = CoupledFixtures.Surgery(new("selected-medical-bill", "selected-operation", [])) }] };
        var facts = Facts(read); var result = Select(facts); var tasks = Tasks(facts, result);
        Assert.Equal("selected-medical-bill", Assert.Single(tasks, t => t.Step.Job == "DoBill").Step.Recipe!.Bill);
        Assert.Equal(.6, result.SelectedVector.Support.SurgeryRoomFactor);
    }

    [Theory]
    [InlineData(.1)]
    [InlineData(1)]
    public void TendAndSurgeryAllocationsCarryMedicineLearningWhateverTheSurgeryOutcome(double success)
    {
        var read = CoupledFixtures.NoSupport with
        {
            Demands = [CoupledFixtures.Demand("care", "care", "patient", new(1, 1), default, .1),
                CoupledFixtures.Demand("surgery", "surgery", "other", new(1, 1), default, 0d) with { SurgerySuccessFactor = success }],
            Workers = [new("care", "pawn", "patient", "TendPatient", "Tend", new(1, 1), 0d, 20d) { Medicine = null, Treatment = CoupledFixtures.Tend(learning: ProductiveLearningTests.Tend()) },
                new("surgery", "helper", "other", "DoBill", "Doctor", new(1, 1), 0d, 20d)
                { Medicine = null, Treatment = CoupledFixtures.Surgery(new("selected-medical-bill", "selected-operation", []), ProductiveLearningTests.Surgery(15)) }],
        };
        var facts = Facts(read); var result = Select(facts); var tasks = Tasks(facts, result);
        Assert.Contains(tasks, t => t.Step.Job == "TendPatient");
        Assert.Contains(tasks, t => t.Step.Job == "DoBill");
        Assert.All(tasks, t => Assert.IsNotType<BoundRecipeTask>(t));
        var allocations = result.Selected.Plan.Allocations;
        var tend = Assert.Single(allocations, a => a.Pawn == "pawn").Learning!;
        Assert.Equal(("Medicine", 250d, SkillXpPath.NormalTracker), (tend.Skill, tend.Callbacks.Single().RawXp, tend.Callbacks.Single().Path));
        // A failed surgery still pays: native learning precedes the outcome, which the callback does not read.
        var surgery = Assert.Single(allocations, a => a.Pawn == "helper").Learning!;
        Assert.Equal(("Medicine", 32d, SkillXpPath.NormalRecord), (surgery.Skill, surgery.Callbacks.Single().RawXp, surgery.Callbacks.Single().Path));
    }

    [Fact]
    public void SameBillMaterialOptionsCannotBothEarnSupplyRelief()
    {
        var read = Delivery(2);
        read = read with { Deliveries = [read.Deliveries[0], read.Deliveries[0] with
            { Demand = "bill:stone:Rice", Target = "rice", Resource = "Rice", Available = 4 }] };
        var facts = Facts(read); var result = Select(facts);
        Assert.Single(Tasks(facts, result)); Assert.Equal(0d, result.SelectedVector.Support.UnsuppliedDemand);
        Assert.Equal(1d, result.BaselineVector.Support.UnsuppliedDemand);
    }
}
