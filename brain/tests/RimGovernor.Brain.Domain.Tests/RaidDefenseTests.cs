using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// #3900: the raid threat the defense and armory planners read is plain, the armory tier has no unknown rung, and a recipe is
// laddered or unladdered.
public sealed class RaidDefenseTests
{
    [Fact]
    public void TheShortfallIsTheRaidPointsTheCapacityDoesNotCover()
    {
        Assert.Equal(300, new RaidDefense(500, 200).Shortfall);
        Assert.Equal(0, new RaidDefense(200, 500).Shortfall);
        Assert.True(DefenseTier.DefensePromoted(new RaidDefense(500, 200)));
        Assert.False(DefenseTier.DefensePromoted(new RaidDefense(200, 200)));
    }

    [Fact]
    public void ATotalDefenseCapacityCanBeZeroOrPositive()
    {
        Assert.Equal(new RaidDefense(500, 0), RaidDefense.Of(new ReviewFacts { Census = ColonyFactsCoreTests.Snapshot() with { Threat = new() { Observed = ColonyFactsCoreTests.QuietThreat with { RaidPoints = 500.0 } } }, DefenseCapacity = 0 }));
        Assert.Equal(new RaidDefense(500, 120), RaidDefense.Of(new ReviewFacts { Census = ColonyFactsCoreTests.Snapshot() with { Threat = new() { Observed = ColonyFactsCoreTests.QuietThreat with { RaidPoints = 500.0 } } }, DefenseCapacity = 120.0 }));
    }

    [Fact]
    public void TheArmoryTierIsTheThreatCappedByResearch()
    {
        var smithing = ResearchTestFacts.Read(finished: ["Smithing"]);

        Assert.Equal(ArmoryTier.Smithing, ArmoryRules.AssessArmory(new RaidDefense(500, 200), smithing).Tier);
        Assert.Equal(ArmoryTier.Neolithic, ArmoryRules.AssessArmory(new RaidDefense(200, 500), smithing).Tier);
        Assert.Equal("research census", Assert.Throws<RequiredFactException>(() =>
            ArmoryRules.AssessArmory(new ReviewFacts { Census = ColonyFactsCoreTests.Snapshot() with { Threat = new() { Observed = ColonyFactsCoreTests.QuietThreat with { RaidPoints = 500.0 } } }, DefenseCapacity = 120.0 })).Fact);
    }

    private static GearRecipe Recipe() => new() { Access = ProductionBenchFixtures.Recipe("defense-recipe"), Ingredients = [], RequiredWork = [], WorkAmount = 100 };

    [Fact]
    public void OnlyALadderedRecipeIsAWeaponRecipe()
    {
        Assert.True(WeaponAssignmentRules.WeaponRecipe(Recipe() with { Armory = new ArmoryRung.Laddered(ArmoryTier.Neolithic) }));
        Assert.False(WeaponAssignmentRules.WeaponRecipe(Recipe()));
    }
}
