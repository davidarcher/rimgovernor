using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class TradeReviewLifecycleTests
{
    [Fact]
    public void NoSurgeryCandidateDoesNotReadTradeOrGearInputs()
    {
        var facts = new ReviewFacts { Census = ColonyFactsCoreTests.Snapshot(), Population = PopulationFixtures.Census() };
        Assert.True(MedicalRules.SurgeryNeedRecovered(facts, Rules.DefaultPlannerSettings(), false));
    }

    [Fact]
    public void TradePreservesTheSharedPlansNoEatersOutcome()
    {
        var facts = new ReviewFacts { FoodPlan = new FoodPlan(), FoodDays = new FoodRunway.Days(1) };
        var food = TradePolicyRules.TradeFood(facts, Rules.DefaultPlannerSettings());
        Assert.IsType<FoodRunway.NoEaters>(food.Runway);
    }
}
