using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class WorkRequirementBoundaryTests
{
    [Fact]
    public void ProjectRequirementNamesMissingDefinitionInsteadOfDereferencingIt()
    {
        Assert.Throws<RequiredFactException>(() => RoundsProjectBill.RoundsProjectWork(["missing-building"], []));
        Assert.Empty(RoundsProjectBill.RoundsProjectWork([], []));
        var rows = RoundsProjectBill.RoundsProjectWork(["first", "second"],
            [new PlanningDefinition { Name = "first", ConstructionSkill = 3 }, new PlanningDefinition { Name = "second", ConstructionSkill = 7 }]);
        Assert.Equal((WorkType.Construction, 7), (Assert.Single(rows).Work, Assert.Single(rows).Minimum));
    }

    [Fact]
    public void OpenBillRequiresItsBenchAndRecipeButNoBillsRequireNoBenchRead()
    {
        Assert.Empty(RoundsProjectBill.RoundsBillWork([], []));
        Assert.Throws<RequiredFactException>(() => RoundsProjectBill.RoundsBillWork([new("absent", "recipe")], []));
        var bench = new GearBenchRead { Bench = new GearBench { Id = "bench", Usable = true, WorkSpeed = 1, Bills = [], Recipes = [] } };
        Assert.Throws<RequiredFactException>(() => RoundsProjectBill.RoundsBillWork([new("bench", "recipe")], [bench]));
    }

    [Fact]
    public async Task MissingBenchSourceCannotEraseOutstandingResourceWork()
    {
        await Assert.ThrowsAsync<RequiredFactException>(() => RoundsProjectBill.RoundsBenchWorkAsync(null, default, [],
            new Dictionary<Resource, long> { [new("target")] = 1 }, true, new("wort"), CancellationToken.None));
        Assert.Empty(await RoundsProjectBill.RoundsBenchWorkAsync(null, default, [], new Dictionary<Resource, long>(), false, new("wort"), CancellationToken.None));
    }
}
