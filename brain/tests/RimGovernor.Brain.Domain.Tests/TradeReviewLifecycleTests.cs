using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class TradeReviewLifecycleTests
{
    [Fact]
    public void NoSurgeryCandidateDoesNotReadTradeOrGearInputs()
    {
        var facts = new RoundsFacts { Population = PopulationFixtures.Census() };
        Assert.True(MedicalRules.SurgeryNeedRecovered(facts, RoundsRules.DefaultRoundsPolicy(), false));
    }

    [Fact]
    public void TradePreservesTheSharedPlansNoEatersOutcome()
    {
        var facts = new RoundsFacts { FoodPlan = new FoodPlan(), FoodDays = new FoodRunway.Days(1) };
        var food = TradePolicyRules.RoundsTradeFood(facts, RoundsRules.DefaultRoundsPolicy());
        Assert.IsType<FoodRunway.NoEaters>(food.Runway);
    }
}
