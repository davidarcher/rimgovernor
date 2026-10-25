using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// A planner's colony facts read inside the reuse window is served the review frame's colony facts by reference
/// (#3760): no copy is split, stored or joined back.
/// </summary>
public sealed class ReviewColonyFactsTests
{
    private static RimGovernor.Brain.GameAccess.ObservationIdentity At(long tick) => new()
    {
        Context = new() { Identity = new() { ColonyId = "colony", LoadToken = "load", MapId = 1 }, Tick = tick, NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.AtCase(7) },
    };

    private static CensusStore Retained(RimGovernor.Brain.GameFacts.Observations.ColonyFactsSnapshot colony)
    {
        var census = new CensusStore();
        census.RememberColony(colony);
        var reading = new ReviewReading { Emergency = new(), Frame = new(), Reading = ColonyFactsCoreTests.Reading(At(100)) };
        census.Retain(reading);
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

        var later = At(130);
        Assert.Null(census.ServedColony(later with { Context = later.Context with { NativeGeneration = new RimGovernor.Brain.GameFacts.Common.ObservedGeneration.AtCase(8) } }));
    }
}
