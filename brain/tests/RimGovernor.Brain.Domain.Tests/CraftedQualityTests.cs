using RimGovernor.Brain.Policy;
using RimGovernor.Contracts.Wire.Observations;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// #3850: QualityUtility.GenerateQualityCreatedByPawn as a distribution over QualityCategory (Awful=0 .. Legendary=6).
public sealed class CraftedQualityTests
{
    [Fact]
    public void EverySkillLevelsOddsAreADistributionWithoutLegendary()
    {
        for (var skill = 0; skill <= 20; skill++)
        {
            var odds = CraftedQualityRules.Odds(new(skill, false, 0));
            Assert.Equal(1, odds.Sum(), 9);
            Assert.All(odds, p => Assert.InRange(p, 0, 1));
            // Only inspiration or a role lifts a draw past Masterwork.
            Assert.Equal(0, odds[6]);
        }
        Assert.Throws<DomainException>(() => CraftedQualityRules.Odds(new(21, false, 0)));
    }

    [Fact]
    public void ASkillTenCrafterMakesMostlyNormalAndGood()
    {
        // Centre 3.1: Normal is [2, 3) of the lower half-normal (width 0.6), Good [3, 4) across the centre, Excellent [4, 5).
        var odds = CraftedQualityRules.Odds(new(10, false, 0));
        Assert.Equal(0.400, odds[2], 2);
        Assert.Equal(0.436, odds[3], 2);
        Assert.Equal(0.122, odds[4], 2);
        // A Masterwork draw is redrawn half the time with the wider upper width 0.95.
        Assert.InRange(odds[5], 0.004, 0.009);
    }

    [Fact]
    public void InspirationAddsTwoLevelsAndARoleItsOffsetCappedAtLegendary()
    {
        var drawn = CraftedQualityRules.Odds(new(10, false, 0));
        var inspired = CraftedQualityRules.Odds(new(10, true, 0));
        for (var q = 0; q < 4; q++) Assert.Equal(drawn[q], inspired[q + 2]);
        Assert.Equal(drawn[4] + drawn[5], inspired[6], 12);
        var specialist = CraftedQualityRules.Odds(new(10, false, 1));
        Assert.Equal(drawn[5], specialist[6]);
        Assert.Equal(drawn[2], specialist[3]);
    }

    [Fact]
    public void TheBestCrafterIsTheOneWithTheHighestExpectedQuality()
    {
        GearCrafter novice = new(3, false, 0), master = new(15, false, 0), inspired = new(8, true, 0);
        Assert.Equal(CraftedQualityRules.Odds(master), CraftedQualityRules.BestOdds([novice, master]));
        Assert.Equal(CraftedQualityRules.Odds(inspired), CraftedQualityRules.BestOdds([master, inspired]));
        Assert.Throws<RequiredFactException>(() => CraftedQualityRules.BestOdds([]));
    }
}
