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
        Assert.Throws<RequiredFactException>(() => ProjectBill.ProjectWork(["missing-building"], []));
        Assert.Empty(ProjectBill.ProjectWork([], []));
        var rows = ProjectBill.ProjectWork(["first", "second"],
            [new PlanningDefinition { Name = "first", ConstructionSkill = 3 }, new PlanningDefinition { Name = "second", ConstructionSkill = 7 }]);
        Assert.Equal((WorkType.Construction, 7), (Assert.Single(rows).Work, Assert.Single(rows).Minimum));
    }

    [Fact]
    public void OpenBillRequiresItsBenchAndRecipeButNoBillsRequireNoBenchRead()
    {
        Assert.Empty(ProjectBill.BillWork([], []));
        Assert.Throws<RequiredFactException>(() => ProjectBill.BillWork([new("absent", "recipe")], []));
        var bench = new GearBenchRead { Bench = new GearBench { Observed = ProductionBenchFixtures.Observed("bench", "WorkTable", "bench-token", true), Bills = [], Recipes = [] } };
        Assert.Throws<RequiredFactException>(() => ProjectBill.BillWork([new("bench", "recipe")], [bench]));
    }

    [Fact]
    public async Task MissingBenchSourceCannotEraseOutstandingResourceWork()
    {
        await Assert.ThrowsAsync<RequiredFactException>(() => ProjectBill.BenchWorkAsync(null, default, [],
            new Dictionary<Resource, long> { [new("target")] = 1 }, true, new("wort"), CancellationToken.None));
        Assert.Empty(await ProjectBill.BenchWorkAsync(null, default, [], new Dictionary<Resource, long>(), false, new("wort"), CancellationToken.None));
    }
}
