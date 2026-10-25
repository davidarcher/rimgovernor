using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ConstructionHelpReadyTests
{
    private static readonly GenerationSnapshot World = new(new("colony"), new(1), new("load"), new("root"), new(1), new(1));

    private static readonly ConstructionSite FilledWall = ConstructionObservationFixtures.Site(new Building("Wall", new Cell(1, 1), Rotation.North, ""), id: "site", stage: "frame", resourcesComplete: true, qualitySensitive: false, nativeFinishingSkill: 0);

    private static ConstructionHelp Demand(ReadyWorkReport? report, CurrentConstruction? census) => ConstructionHelpersRules.ConstructionHelpDemand(
        report, World, new Tick(10), null, census);

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
    [Fact]
    public void QualityFreeHelpersUseTheObservedNativeSkill()
    {
        var census = CurrentConstruction.Empty with { Sites = [FilledWall with { Observed = FilledWall.Observed with { Construction = FilledWall.Construction with { NativeFinishingSkill = 3 } } }] };
        Assert.Equal(3, Demand(null, census).Floor);
    }}
