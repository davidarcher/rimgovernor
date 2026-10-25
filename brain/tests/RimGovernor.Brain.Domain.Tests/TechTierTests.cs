using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using Xunit;
using TechLevel = RimGovernor.Brain.GameFacts.Defs.TechLevel;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class TechTierTests
{
    [Theory]
    [InlineData(TechLevel.Neolithic, TechTier.Camp)]
    [InlineData(TechLevel.Medieval, TechTier.Masonry)]
    [InlineData(TechLevel.Industrial, TechTier.Industrial)]
    [InlineData(TechLevel.Spacer, TechTier.Spacer)]
    public void ObservedEmptyResearchUsesFactionFloor(TechLevel faction, TechTier expected) =>
        Assert.Equal(expected, TechTierRules.SelectTechTier([], faction));

    [Fact]
    public void ResearchRaisesTribalColonyThroughUnlockChain()
    {
        ResearchProjectId[] projects = [new("Stonecutting"), new("Electricity"), new("Machining")];
        Assert.Equal(TechTier.Industrial, TechTierRules.SelectTechTier(projects, TechLevel.Neolithic));
        Assert.Equal("Machining", TechTierRules.TechTierEvidence(projects, TechLevel.Neolithic));
    }

    [Fact]
    public void MissingFactionDoesNotBecomeCamp() =>
        Assert.Throws<RequiredFactException>(() => TechTierRules.SelectTechTier([], TechLevel.Undefined));

    [Fact]
    public void MissingResearchIsRejectedAtResearchBoundary() =>
        Assert.Throws<RequiredFactException>(() => new ReviewFacts().Research);
}
