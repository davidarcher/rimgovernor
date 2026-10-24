using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class FoodRunwayReachTests
{
    [Fact]
    public void ARunwayWithNoEatersReachesAnyTargetAndADaysRunwayReachesOnlyItsOwn()
    {
        Assert.True(new FoodRunway.NoEaters().Reaches(1e9));
        Assert.True(new FoodRunway.Days(5).Reaches(5));
        Assert.False(new FoodRunway.Days(4.9).Reaches(5));
    }

    [Fact]
    public void AnAbsentFoodPlanOrRunwayRaisesAtTheReaderThatNeedsIt()
    {
        var facts = new RoundsFacts();
        Assert.Equal("food plan", Assert.Throws<RequiredFactException>(() => facts.RequireFoodPlan()).Fact);
        Assert.Equal("food runway", Assert.Throws<RequiredFactException>(() => facts.RequireFoodRunway()).Fact);
        var read = facts with { FoodPlan = new FoodPlan(), FoodDays = new FoodRunway.NoEaters() };
        Assert.NotNull(read.RequireFoodPlan());
        Assert.IsType<FoodRunway.NoEaters>(read.RequireFoodRunway());
    }
}
