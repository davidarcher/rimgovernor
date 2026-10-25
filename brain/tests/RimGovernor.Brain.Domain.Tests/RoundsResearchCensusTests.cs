using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.Bridge.Contracts;
using RimGovernor.Brain.BuildingRuntime;
using RimGovernor.Brain.Policy;
using Xunit;
using O = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class RoundsResearchCensusTests
{
    [Fact]
    public void RoutineFrameWithoutResearchRefusesTheRead()
    {
        var context = ColonyFactsCoreTests.Snapshot().Context;
        var frame = new O.BundleSnapshot
        {
            Context = context,
            Pawns = new() { Context = context, Pawns = [] },
        };

        var error = Assert.Throws<BridgeContractException>(() => FacadeNativeClient.DecodeRoundsFrame(frame, null!, FramePrepared.Of(frame)));

        Assert.Contains("research section", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SyntheticProjectionCannotReadAbsentResearchAsNoFinishedProjects()
    {
        Assert.Equal("research census", Assert.Throws<RequiredFactException>(() => new RoundsFacts().Research).Fact);
        var research = ResearchTestFacts.Read();
        var facts = new RoundsFacts { Research = research };

        Assert.Same(research, facts.Research);
        Assert.Empty(facts.Research.Finished);
    }
}
