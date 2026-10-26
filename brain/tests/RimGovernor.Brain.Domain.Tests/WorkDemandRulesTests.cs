using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class WorkDemandRulesTests
{
    [Fact]
    public void WorkRequirementsMergeHighestFloorInFirstSeenOrderAndKeepFirstNamedSkill()
    {
        var merged = WorkDemandRules.MergeWorkRequirements(
            [new() { Work = WorkType.Crafting, Minimum = 2 }, new() { Work = WorkType.Construction, Minimum = 3, Skill = "Construction" }],
            [new() { Work = WorkType.Crafting, Minimum = 7, Skill = "Crafting" }, new() { Work = WorkType.Construction, Minimum = 1, Skill = "other" }]);
        Assert.Equal([WorkType.Crafting, WorkType.Construction], merged.Select(x => x.Work));
        Assert.Equal([7, 3], merged.Select(x => x.Minimum));
        Assert.Equal(["Crafting", "Construction"], merged.Select(x => x.Skill));
    }

    [Fact]
    public void PendingProductionEnablesWorkBeforeBillExistsIncludingWortForBeer()
    {
        var recipe = new GearRecipe
        {
            Access = ProductionBenchFixtures.Recipe("brew"), Products = [new("wort")], Ingredients = [], WorkAmount = 10,
            RequiredWork = [new() { Work = WorkType.Cooking, Minimum = 4, Skill = "Cooking" }],
        };
        var bench = new GearBench { Observed = ProductionBenchFixtures.Observed("bench", "brewery", "token", true), Recipes = [recipe], Bills = [] };
        var targets = new Dictionary<Resource, long> { [new("beer")] = 1 };
        Assert.Equal(WorkType.Cooking, Assert.Single(WorkDemandRules.DeficitWork(targets, [bench], new("wort"), new("beer"))).Work);
        Assert.Empty(WorkDemandRules.DeficitWork(new Dictionary<Resource, long>(), [bench], new("wort"), new("beer")));
        var unavailable = bench with { Recipes = [recipe with { Access = ProductionBenchFixtures.Recipe("brew", onBench: false) }] };
        Assert.Empty(WorkDemandRules.DeficitWork(targets, [unavailable], new("wort"), new("beer")));
        Assert.Equal(4, Assert.Single(WorkDemandRules.BillWork([new("bench", "brew")], [bench])).Minimum);
    }

    [Fact]
    public void ResourceAndResearchRequirementsRetainTheirOriginalNeedGates()
    {
        var targets = new Dictionary<Resource, long> { [new("stone")] = 12 };
        var gear = ColonyFactsCoreTests.Reading().Facts.Gear;
        Assert.Empty(WorkDemandRules.DeficitTargets(targets, false, gear));
        Assert.Equal(12, WorkDemandRules.DeficitTargets(targets, true, gear)[new("stone")]);
        Assert.Empty(WorkDemandRules.ResearchNeeds(MilitaryFamily.Equipment.FilingKey, ["armor"], false));
        Assert.Equal(["armor"], WorkDemandRules.ResearchNeeds(MilitaryFamily.Equipment.FilingKey, ["armor"], true));
        Assert.Equal(["power"], WorkDemandRules.ResearchNeeds(EconomyFamily.Resource.FilingKey, ["power"], false));
        Assert.Empty(WorkDemandRules.ResearchWork([], null));
        Assert.Equal(WorkType.Research, Assert.Single(WorkDemandRules.ResearchWork(["power"], null)).Work);
    }
}
