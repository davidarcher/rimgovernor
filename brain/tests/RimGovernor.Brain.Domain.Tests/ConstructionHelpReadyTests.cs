using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ConstructionHelpReadyTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("root"), new(1), new(1));

    private static readonly ConstructionSite FilledWall = new()
    {
        Id = "site", Building = new Building("Wall", new Cell(1, 1), Rotation.North, ""), Stage = "frame",
        ResourcesComplete = true, QualitySensitive = false, NativeFinishingSkill = 0,
    };

    private static ConstructionHelp Demand(ReadyWorkReport? report, CurrentConstruction? census) => ConstructionHelpersRules.ConstructionHelpDemand(
        report, World, new Tick(10), [new HelpDefinition { Name = "Wall", Skill = Known(0) }], null, census);

    [Fact]
    public void AMissingCensusRefusesHelperPlanning()
    {
        Assert.Throws<RequiredFactException>(() => Demand(null, null));
    }

    [Fact]
    public void ACompleteEmptyCensusCountsNoReadyWork()
    {
        Assert.Equal(0, Demand(null, CurrentConstruction.Empty).Ready);
    }

    [Fact]
    public void TheCensusCountsTheRunnableHelperWork()
    {
        var census = CurrentConstruction.Empty with { Sites = EquatableArray.From(new[] { FilledWall }) };
        Assert.Equal(1, Demand(null, census).Ready);
    }
}
