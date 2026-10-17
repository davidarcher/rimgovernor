using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class SupportMethodTests
{
    private const double Horizon = 300;
    private static RoundsFacts Facts(SupportActivityRead support, bool cooking = false)
    {
        var facts = WorkResourceTestFacts.Create("pawn", "helper");
        var basePawn = ForecastCoupledSelectionTests.Review(level: .4, fall: cooking ? .001 : 0).ForecastObserved.Needs!.Pawns[0];
        facts = facts with
        {
            Resources = Known<EquatableArray<RimGovernor.Brain.Policy.Amount>>([new(new("Rice"), 4), new(new("Chunk"), 20)]),
            ForecastObserved = facts.ForecastObserved with
            {
                Needs = new() { Tick = 100, Pawns = [basePawn, basePawn with { Pawn = "helper", Needs =
                    [new("food", Known(1d), Known(0d), Known(.3)), new("rest", Known(1d), Known(0d), Known(.3))] }] },
                Coupled = new()
                {
                    Tick = 100, Support = support,
                    FoodSources = cooking ? [new("rice", "Rice", new(1, 1), 4, Known(1000d))] : [],
                    FoodConversions = cooking ? [new("rice", "pawn", "bench", "CookMeal", "Rice", 4, "Meal", 1,
                        new(1, 1), new(1, 1), new(1, 1), Known(0d), Known(1d), Known(10d), Known(1d), 4, 4)
                        { RequiresHarvest = false, Bill = "bill", WorkGiver = "Cook" }] : [],
                    FoodUses = cooking ? [new("pawn", "Meal", new(1, 1), Known(true), Known(.8), Known(10d), 1, Known(1000d)),
                        new("pawn", "Rice", new(1, 1), Known(false), Known(.1), Known(10d), 4, Known(1000d))] : [],
                    Routes = [new("pawn", new(1, 1), new(1, 1), Known(0d)) { FromStart = true },
                        new("helper", new(1, 1), new(1, 1), Known(0d)) { FromStart = true }]
                }
            }
        };
        facts = WorkRules.ReviewWorkResources(facts, Known<EquatableArray<ProductionBatch>>([]), new([]));
        return facts with { CoupledAlternatives = ForecastCoupledCandidates.GenerateCoupledActivities(facts, Horizon) };
    }
    private static ForecastCoupledSelection Select(RoundsFacts facts, int budget = 16384) => ForecastCoupledSelection.CompareActivityOutcomes(
        ForecastCoupled.CoupledForecastRequestOf(new("world", 100), Horizon / GameTime.TicksPerDay, facts, 
            new() { Work = ForecastWorkInputs.ForecastWorkInputsOf(facts) }, [], new()), facts.CoupledAlternatives!, budget);
    private static SupportActivityRead Cleaning(double self = 30, double helper = 5, bool known = true) => new()
    {
        Known = true,
        Demands = [new("bench", "bench", "bench", new(1, 1), known ? Known(.15) : default, Known(0d), default)
            { FoodPoisonChanceClean = known ? Known(.01) : default }],
        Filth = [new("bench", "filth", new(1, 1), Known(2d))],
        Workers = [new("bench", "pawn", "filth", "Clean", "Clean", new(1, 1), Known(0d), Known(self)),
            new("bench", "helper", "filth", "Clean", "Clean", new(1, 1), Known(0d), Known(helper))]
    };
    private static SelectedActivityTask[] Tasks(RoundsFacts facts, ForecastCoupledSelection selection) =>
        ForecastCoupledSelection.StartCoupledActivity(selection, facts.CoupledAlternatives!, 1, "method", 100)?.Tasks.ToArray() ?? [];

    [Fact]
    public void NormalReviewProposesAndSelectsHelperSelfOrNoCleaningForSameProducer()
    {
        foreach (var (self, helper, expected) in new[] { (30d, 5d, "helper"), (5d, 30d, "pawn"), (290d, 290d, "") })
        {
            var facts = Facts(Cleaning(self, helper), true); var selection = Select(facts); var tasks = Tasks(facts, selection);
            Assert.Contains(tasks, t => t.Step.Kind == PawnActivityKind.Recipe);
            var clean = tasks.Where(t => t.Step.Job == "Clean").ToArray();
            if (expected.Length == 0) Assert.Empty(clean); else Assert.Equal(expected, Assert.Single(clean).Pawn);
            Assert.True(selection.Selected.Plan.Allocations.Sum(a => a.TravelTicks + a.WaitTicks + a.WorkTicks) > 0);
        }
    }

    [Fact]
    public void UnknownCleanlinessBenefitDoesNotBecomeMandatoryCleaning()
    {
        var facts = Facts(Cleaning(known: false), true); var result = Select(facts);
        Assert.DoesNotContain(Tasks(facts, result), t => t.Step.Job == "Clean");
        Assert.Contains(result.SelectedVector.Unknown, f => f.StartsWith("food_poison_effect_unknown:"));
    }

    [Fact]
    public void UrgentCareBeatsCleaningAndPostcareDoesNotInventHealthBenefit()
    {
        var read = Cleaning() with { Demands = [new("care", "care", "patient", new(1, 1), default, Known(.1), default)],
            Filth = [new("care", "filth", new(1, 1), Known(2d))], Workers =
            [new("care", "pawn", "patient", "TendPatient", "Tend", new(1, 1), Known(0d), Known(20d)),
                new("care", "helper", "filth", "Clean", "Clean", new(1, 1), Known(0d), Known(10d))] };
        var facts = Facts(read); var result = Select(facts); var tasks = Tasks(facts, result);
        Assert.Contains(tasks, t => t.Step.Job == "TendPatient");
        Assert.DoesNotContain(tasks, t => t.Step.Job == "Clean");
        Assert.Equal(Known(20d), result.SelectedVector.Support.UrgentFirstCareTicks);
        Assert.Contains(facts.CoupledAlternatives!.Alternatives, c => c.Id.Contains("post-clean"));
    }

    private static SupportActivityRead Delivery(int required, double duration = 10) => new()
    {
        Known = true,
        Deliveries = [new("bill:stone:Chunk", "helper", "useful-chunk", "Chunk", "bench", "stone", new(2, 2), new(1, 1), required, 20, 20, Known(0d), Known(duration))]
    };
    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public void DemandSelectsSmallOrBatchQuantityWithoutFullStackGate(int required)
    {
        var facts = Facts(Delivery(required)); var result = Select(facts); var delivery = Assert.Single(Tasks(facts, result));
        Assert.Equal(required, delivery.Step.Count); Assert.Equal("useful-chunk", delivery.Step.Target);
        Assert.Equal(0d, result.SelectedVector.Support.UnsuppliedDemand);
        Assert.Equal(20d, result.Selected.Stocks.Single(s => s.Resource.Value == "Chunk").Points[^1].BalanceUnits.Value);
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
        var result = Select(Facts(Delivery(8)), 1);
        Assert.True(result.Selection.SearchTruncated);
        Assert.Empty(result.Selection.Selected);
    }

    [Fact]
    public void TravelReversesHelperChoiceAndUnknownRouteCannotWin()
    {
        foreach (var approach in new[] { Known(200d), default(Fact<double>) })
        {
            var support = Cleaning() with { Filth = [new("bench", "filth", new(2, 2), Known(2d))], Workers =
                [new("bench", "pawn", "filth", "Clean", "Clean", new(2, 2), Known(0d), Known(5d)),
                 new("bench", "helper", "filth", "Clean", "Clean", new(2, 2), approach, Known(1d))] };
            var facts = Facts(support, true);
            facts = facts with { ForecastObserved = facts.ForecastObserved with { Coupled = facts.ForecastObserved.Coupled! with
                { Routes = [.. facts.ForecastObserved.Coupled!.Routes, new("pawn", new(2, 2), new(1, 1), Known(0d)), new("helper", new(2, 2), new(1, 1), Known(0d))] } } };
            facts = facts with { CoupledAlternatives = ForecastCoupledCandidates.GenerateCoupledActivities(facts, Horizon) };
            Assert.Equal("pawn", Assert.Single(Tasks(facts, Select(facts)), t => t.Step.Job == "Clean").Pawn);
        }
    }

    [Fact]
    public void ExactMedicineChoiceConsumesOneObservedLotAndPreservesIntent()
    {
        var read = new SupportActivityRead { Known = true,
            Demands = [new("care", "care", "patient", new(1, 1), default, Known(0d), default)
                { InfectionChanceFactor = Known(1d), InfectionChanceFactorClean = Known(1d) }],
            Workers = [new("care", "pawn", "patient", "TendPatient", "Tend", new(1, 1), Known(0d), Known(20d))
                { TendQuality = Known(.2) },
                new("care", "pawn", "patient", "TendPatient", "Tend", new(1, 1), Known(0d), Known(20d))
                { Medicine = "medicine", MedicineResource = "Chunk", MedicineCount = 1, MedicineAvailable = 20, MedicineLocation = new(1, 1), TendQuality = Known(.8) }] };
        var facts = Facts(read); var result = Select(facts); var task = Assert.Single(Tasks(facts, result));
        Assert.Equal("medicine", task.Step.Medicine); Assert.Equal("care", task.Step.SupportDemand);
        Assert.Equal(1, Assert.Single(task.Inputs).Count);
        Assert.Equal(19d, result.Selected.Stocks.Single(s => s.Resource.Value == "Chunk").Points[^1].BalanceUnits.Value);
    }

    [Theory]
    [InlineData(false, 100, false)]
    [InlineData(null, 100, false)]
    [InlineData(true, 10, false)]
    [InlineData(true, 100, true)]
    public void CleaningCareBenefitRequiresApplicableNativeBatchAndAnUnexpiredCheck(bool? applicable, double checkTicks, bool clean)
    {
        var read = Cleaning() with
        {
            Demands = [new("care", "care", "patient", new(1, 1), default, Known(0d), default)
                { InfectionChanceFactor = Known(2d), InfectionChanceFactorClean = Known(.5) }],
            Filth = [new("care", "filth", new(1, 1), Known(2d))],
            Workers = [new("care", "pawn", "patient", "TendPatient", "Tend", new(1, 1), Known(0d), Known(20d))
                { InfectionRoomEffectApplies = applicable is { } value ? Known(value) : default,
                    InfectionCheckTicks = Known(checkTicks), TendQuality = Known(.5) },
                new("care", "helper", "filth", "Clean", "Clean", new(1, 1), Known(0d), Known(5d))]
        };
        var facts = Facts(read); var selection = Select(facts); var tasks = Tasks(facts, selection);
        Assert.Contains(tasks, t => t.Step.Job == "TendPatient");
        Assert.Equal(clean, tasks.Any(t => t.Step.Job == "Clean"));
        if (applicable == false) Assert.Equal(Known(0d), selection.SelectedVector.Support.CareRoomFactor);
        else if (!clean) Assert.False(selection.SelectedVector.Support.CareRoomFactor.IsKnown);
        else Assert.Equal(Known(.5), selection.SelectedVector.Support.CareRoomFactor);
    }

    [Fact]
    public void SurgeryUsesItsOwnQueuedServiceAndNativeRoomMultiplier()
    {
        var read = Cleaning() with { Demands = [new("surgery", "surgery", "patient", new(1, 1), default, Known(0d), default)
                { SurgerySuccessFactor = Known(.6), SurgerySuccessFactorClean = Known(1d) }],
            Filth = [new("surgery", "filth", new(1, 1), Known(2d))], Workers =
            [new("surgery", "pawn", "patient", "DoBill", "Doctor", new(1, 1), Known(0d), Known(20d))
                { Recipe = new("selected-medical-bill", "selected-operation", []) },
                new("surgery", "helper", "filth", "Clean", "Clean", new(1, 1), Known(0d), Known(5d))] };
        var facts = Facts(read); var result = Select(facts); var tasks = Tasks(facts, result);
        Assert.Contains(tasks, t => t.Step.Job == "Clean");
        Assert.Equal("selected-medical-bill", Assert.Single(tasks, t => t.Step.Job == "DoBill").Step.Recipe!.Bill);
        Assert.Equal(Known(1d), result.SelectedVector.Support.SurgeryRoomFactor);
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

    [Fact]
    public void PostcareCleanupCanServeLaterActualProducerThroughSharedCondition()
    {
        var clean = Cleaning(5, 500);
        var read = clean with { Demands = [.. clean.Demands, new("care", "care", "patient", new(1, 1), default, Known(.1), default)],
            Filth = [.. clean.Filth, new("care", "filth", new(1, 1), Known(2d))],
            Workers = [clean.Workers[0], new("care", "pawn", "patient", "TendPatient", "Tend", new(1, 1), Known(0d), Known(20d))] };
        var facts = Facts(read, true); var result = Select(facts); var tasks = Tasks(facts, result);
        var care = Assert.Single(tasks, t => t.Step.Job == "TendPatient");
        var cleanup = Assert.Single(tasks, t => t.Step.Job == "Clean");
        var recipe = Assert.Single(tasks, t => t.Step.Kind == PawnActivityKind.Recipe);
        var allocations = result.Selected.Plan.Allocations.ToDictionary(a => a.Activity);
        Assert.True(allocations[care.Id].End <= allocations[cleanup.Id].Start);
        Assert.True(allocations[cleanup.Id].End <= allocations[recipe.Id].Start);
        Assert.Equal(Known(.01), result.SelectedVector.Support.FoodPoisonExposure);
    }
}
