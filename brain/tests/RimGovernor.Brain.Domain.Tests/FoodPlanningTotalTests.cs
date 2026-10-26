using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class FoodPlanningTotalTests
{
    private static readonly Resource Food = new("RawBerries");
    private static readonly AcquisitionSource Bush = new()
    {
        Observed = new() { Source = new() { Id = "bush" }, Token = "bush", Resource = Food.Value, Yield = 10, Designation = new RimGovernor.Brain.GameFacts.Observations.AcquisitionDesignation.Open(), Taken = false, Kind = new RimGovernor.Brain.GameFacts.Observations.AcquisitionKind.Plant(true, 1, false) },
        Entity = new() { Id = "bush", DefName = "Plant_Berry", Label = "Plant_Berry", Place = new RimGovernor.Brain.GameFacts.Observations.EntityPlace.OnMap(1, new Cell(1, 1)) }, NutritionYield = .5,
    };

    [Fact]
    public void PendingYieldPreventsDuplicateAcquisitionAndStalledYieldReopensIt()
    {
        var designated = Bush with { Observed = Bush.Observed with { Designation = new RimGovernor.Brain.GameFacts.Observations.AcquisitionDesignation.Designated(new(0)) } };
        var pending = AcquisitionRules.WithoutStalled(.5, [designated], [Bush.Id], true);
        Assert.Equal(0, pending);
        Assert.Empty(AcquisitionRules.SelectAcquisition([Bush], .5, .5, new Dictionary<string, bool>()));
        Assert.Equal(Bush, Assert.Single(AcquisitionRules.SelectAcquisition([Bush], .5, pending, new Dictionary<string, bool>())));
        Assert.Throws<DomainException>(() => AcquisitionRules.SelectAcquisition([Bush], double.NaN, 0, new Dictionary<string, bool>()));
    }

    [Fact]
    public void TerminalFoodEvidenceDistinguishesNoUseFromZeroAndIneligibleStock()
    {
        ForecastTerminalLot Lot(FoodEligibility use) => new("lot", Food, 5, 10, use, .05, [], false);
        Assert.Equal(new EvidenceValue.Valued(5), ForecastStockScope.Count([Lot(FoodEligibility.Eligible)], Food));
        Assert.Equal(new EvidenceValue.Valued(0), ForecastStockScope.Count([Lot(FoodEligibility.Ineligible)], Food));
        Assert.Equal(new EvidenceValue.Valued(0), ForecastStockScope.Count([], Food));
        Assert.IsType<EvidenceValue.Unavailable>(ForecastStockScope.Count([Lot(FoodEligibility.NoObservedUse)], Food));
    }

    [Fact]
    public void RetainedFoodWorkMustBePricedAndIsChargedOnce()
    {
        var channel = FoodPlanRules.ForageChannels([Bush])[0];
        var retained = new FoodPlanEntry { Channel = channel, Decision = FoodPlanDecision.Open };
        Assert.Equal(17500, ResourceRules.ResidualSupplyLabor(1, new FoodPlan { Portfolio = [retained, retained] }));
        Assert.Throws<RequiredFactException>(() => ResourceRules.ResidualSupplyLabor(1,
            new FoodPlan { Portfolio = [retained with { Channel = channel with { Work = new CandidateWork.Unpriced() } }] }));
        Assert.Equal(20000, ResourceRules.ResidualSupplyLabor(1,
            new FoodPlan { Portfolio = [retained with { Decision = FoodPlanDecision.Close }] }));
    }

    [Fact]
    public void HumanButcherNeedsWorkDisposition()
    {
        Assert.False(HumanFoodRules.HumanButcherEligible([], true, false));
        Assert.False(HumanFoodRules.HumanButcherEligible([], false, true));
        Assert.True(HumanFoodRules.HumanButcherEligible([new PawnTrait { Observed = new() { DefName = "Cannibal" }, Effects = new TraitEffects { HumanButcher = true } }], false, true));
        Assert.True(HumanFoodRules.PreceptAcceptable(new RimGovernor.Brain.GameFacts.Observations.PawnIdeo.None(), new IdeologyMode.NotInstalled()));
    }
    [Fact]
    public void AnimalFeedHasAZeroRunwayForNoHerdAndRequiresTheKeptAnimalsFacts()
    {
        Assert.False(AnimalFeedRunwayRules.PlanAnimalFeedRunway(new()).Projection.IsShort());
        var animal = AnimalFixtures.Owned("animal", "Alpaca");
        Assert.Throws<RequiredFactException>(() => AnimalFeedRunwayRules.PlanAnimalFeedRunway(new() { Animals = [animal] }));
        Assert.Throws<RequiredFactException>(() => AnimalFeedRunwayRules.PlanAnimalFeedRunway(new()
        {
            Animals = [animal],
            Food = new FoodSupply { Larder = new() { RawMeatNutrition = 0, CookDemandNutrition = 0, Corpses = [] }, Consumers = [new FoodConsumer { Id = animal.Id, NutritionPerDay = 1,
        HumanMeatAcceptable = false }] },
        }));
        Assert.False(HusbandryUpkeepRules.AnimalHerdDeficit([], [], false, new()));
        Assert.True(new AnimalFeedProjection { ShortfallDays = 1 }.IsShort());
    }

    [Fact]
    public void HayDemandUsesTheObservedSeasonalGapAndRejectsMalformedPasture()
    {
        Assert.Equal(5, AnimalFoodRules.HayNutritionNeed([new PenGrazing
            { DemandPerDay = 2, PasturePerDay = 1, StoredNutrition = 5 }], 10));
        Assert.Throws<RequiredFactException>(() => AnimalFoodRules.HayNutritionNeed([new PenGrazing
            { DemandPerDay = double.NaN, PasturePerDay = 0, StoredNutrition = 0 }], 10));
    }
    [Fact]
    public void FoodChecksRequireRunwayAndButcherBuildDefinition()
    {
        Assert.Throws<RequiredFactException>(() => Rules.Reached(null, 3));
        Assert.True(Rules.Reached(new FoodRunway.NoEaters(), 3));
        Assert.False(Rules.Reached(new FoodRunway.Days(2), 3));
        Assert.Throws<RequiredFactException>(() => FacilityPlanningRules.ButcherTableAffordable(ColonyFactsCoreTests.Reading().Facts));
    }
}
