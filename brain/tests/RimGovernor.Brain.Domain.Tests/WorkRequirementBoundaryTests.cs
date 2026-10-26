using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class WorkRequirementBoundaryTests
{
    [Fact]
    public void ProjectRequirementNamesMissingDefinitionInsteadOfDereferencingIt()
    {
        Assert.Throws<RequiredFactException>(() => WorkDemandRules.ProjectWork(["missing-building"], []));
        Assert.Empty(WorkDemandRules.ProjectWork([], []));
        var rows = WorkDemandRules.ProjectWork(["first", "second"],
            [new PlanningDefinition { Name = "first", ConstructionSkill = 3 }, new PlanningDefinition { Name = "second", ConstructionSkill = 7 }]);
        Assert.Equal((WorkType.Construction, 7), (Assert.Single(rows).Work, Assert.Single(rows).Minimum));
    }

    [Fact]
    public void OpenBillRequiresItsBenchAndRecipeButNoBillsRequireNoBenchRead()
    {
        Assert.Empty(WorkDemandRules.BillWork([], []));
        Assert.Throws<RequiredFactException>(() => WorkDemandRules.BillWork([new("absent", "recipe")], []));
        var bench = new GearBenchRead { Bench = new GearBench { Observed = ProductionBenchFixtures.Observed("bench", "WorkTable", "bench-token", true), Bills = [], Recipes = [] } };
        Assert.Throws<RequiredFactException>(() => WorkDemandRules.BillWork([new("bench", "recipe")], [bench.Bench]));
    }

    [Fact]
    public async Task MissingBenchSourceCannotEraseOutstandingResourceWork()
    {
        await Assert.ThrowsAsync<RequiredFactException>(() => ProjectBillReads.BenchWorkAsync(null, default, [],
            new Dictionary<Resource, long> { [new("target")] = 1 }, true, new("wort"), CancellationToken.None));
        Assert.Empty(await ProjectBillReads.BenchWorkAsync(null, default, [], new Dictionary<Resource, long>(), false, new("wort"), CancellationToken.None));
    }
}
