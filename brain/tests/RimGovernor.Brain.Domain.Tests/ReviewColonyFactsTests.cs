using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Observation;
using RimGovernor.Brain.Policy;
using Xunit;
using PbObservations = RimGovernor.Contracts.Wire.Observations;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// A planner's colony facts read inside the reuse window is served the review frame's colony facts by reference
/// (#3760): no copy is split, stored or joined back.
/// </summary>
public sealed class ReviewColonyFactsTests
{
    private static RimGovernor.Brain.Observation.Identity At(long tick) => new()
    {
        Colony = new ColonyId("colony"),
        Load = new LoadId("load"),
        Map = new MapId(1),
        Tick = new Tick(tick),
        NativeGeneration = Known(new NativeGeneration(7)),
    };

    private static RoundsCensusStore Retained(PbObservations.ColonyFactsSnapshot colony)
    {
        var census = new RoundsCensusStore();
        census.RememberColony(colony);
        var reading = new RoundsReading { Emergency = new(), Frame = new(), Reading = new ColonyReading { Projection = ColonyFactsCoreTests.Projection() with { Identity = At(100) } } };
        census.Retain(reading, false);
        return census;
    }

    [Fact]
    public void InsideTheWindowServesTheFrameInstance()
    {
        var colony = ColonyFactsCoreTests.Snapshot();
        var census = Retained(colony);

        Assert.Same(colony, census.ServedColony(At(130)));
    }

    [Fact]
    public void AfterInvalidationGoesToNative()
    {
        var census = Retained(ColonyFactsCoreTests.Snapshot());
        census.Invalidate();

        Assert.Null(census.ServedColony(At(130)));
    }

    [Fact]
    public void AnotherGenerationGoesToNative()
    {
        var census = Retained(ColonyFactsCoreTests.Snapshot());

        Assert.Null(census.ServedColony(At(130) with { NativeGeneration = Known(new NativeGeneration(8)) }));
    }
}
