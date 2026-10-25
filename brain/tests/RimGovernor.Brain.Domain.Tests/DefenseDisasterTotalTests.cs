using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class DefenseDisasterTotalTests
{
    [Fact]
    public void ColdBudgetNamesFiniteUnboundedAndUnmodelledHazards()
    {
        var tuning = DashEstimateRules.PublishedHypothermiaTuning;
        Assert.IsType<OutdoorBudget.Finite>(DashEstimateRules.OutdoorBudgetFor(HazardKind.ExtremeCold, -30, 0, 0, .04, tuning));
        Assert.IsType<OutdoorBudget.Unbounded>(DashEstimateRules.OutdoorBudgetFor(HazardKind.ExtremeCold, 0, 0, 0, .04, tuning));
        Assert.IsType<OutdoorBudget.Unmodelled>(DashEstimateRules.OutdoorBudgetFor(HazardKind.ExtremeHeat, 50, 0, 0, .04, tuning));
        Assert.Equal(0, Assert.IsType<OutdoorBudget.Finite>(DashEstimateRules.OutdoorBudgetFor(HazardKind.ExtremeCold, -30, 0, .04, .04, tuning)).Ticks);
    }

    [Fact]
    public void SleepingAbsenceCannotClearAWeatherShelterOrRestoreTemperature()
    {
        var facts = new RoundsFacts
        {
            Hostiles = new HostileCensus.Quiet(),
            SleepingTemperature = new SleepingTemperature.NoIndoorBed(),
            DisasterConditions = [new DisasterCondition { Definition = DisasterRules.ConditionColdSnap, Duration = new ConditionDuration.Permanent() }],
        };
        Assert.Equal(ShelterTrigger.NoIndoorBed, ShelterRules.ShelterTriggerOf(facts));
        Assert.Equal(DisasterServiceStatus.Deficit, RoundsRules.DisasterServiceFacts(facts, RoundsRules.DefaultRoundsPolicy())[DisasterService.Temperature]);
        Assert.Equal(DisasterServiceStatus.AwaitingFoodPlan, RoundsRules.DisasterServiceFacts(facts, RoundsRules.DefaultRoundsPolicy())[DisasterService.Food]);
        Assert.Equal(ShelterTrigger.Temperature, ShelterRules.ShelterTriggerOf(facts with { SleepingTemperature = new SleepingTemperature.Indoor(15, 25), OutdoorTemperature = -30 }));
        Assert.Equal(ShelterTrigger.None, ShelterRules.ShelterTriggerOf(facts with { DisasterConditions = [] }));
        Assert.Equal(ShelterTrigger.AwaitingEmergency, ShelterRules.ShelterTriggerOf(facts with { DisasterConditions = [], Hostiles = new HostileCensus.AwaitingEmergency() }));
    }

    [Fact]
    public void DashUsesObservedBodyAndClosesWhenThePawnCannotMove()
    {
        var request = new DashRequest { Temperature = -30, ComfortMin = 0, Severity = 0, Speed = 4, Tuning = DashEstimateRules.PublishedHypothermiaTuning, Candidates = [new DashCandidate { Id = "stack", PathCells = 1, Value = 10 }] };
        Assert.Single(DashPlanRules.PlanDash(request).Trips);
        Assert.Empty(DashPlanRules.PlanDash(request with { Speed = 0 }).Trips);
        Assert.Empty(DashPlanRules.PlanDash(request with { Severity = DashPlanRules.DashSeverityCap }).Trips);
    }
    [Fact]
    public void HuntingAdmissionUsesZeroOrTheObservedIntegerBudget()
    {
        var prey = new AcquisitionSource { Id = "prey", Token = "prey", Resource = "Meat_Deer", Cell = new(1, 1), Hunt = true, Food = true, Yield = 1, NutritionYield = 2 };
        var held = new Dictionary<string, bool>();
        Assert.Empty(AcquisitionRules.SelectAcquisition([prey], 2, 0, true, held, 0));
        Assert.Equal(prey, Assert.Single(AcquisitionRules.SelectAcquisition([prey], 2, 0, true, held, 1)));
        Assert.Throws<DomainException>(() => AcquisitionRules.SelectAcquisition([prey], 2, 0, true, held, -1));
        Assert.Throws<DomainException>(() => AcquisitionRules.SelectAcquisition([prey], 2, 0, true, held, PawnRules.MaxHuntRows + 1));
    }
}
