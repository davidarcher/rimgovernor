using RimGovernor.Brain.Bridge;
using RimGovernor.Brain.GameAccess;
using RimGovernor.Brain.Runtime;
using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class ResearchCensusTests
{
    [Fact]
    public void RoutineFrameWithoutResearchRefusesTheRead()
    {
        var context = ColonyFactsCoreTests.Snapshot().Context;
        var frame = new RimGovernor.Brain.GameFacts.Observations.BundleSnapshot
        {
            Context = context,
            Pawns = new() { Context = context, Pawns = [] },
        };

        var error = Assert.Throws<BridgeContractException>(() => GameClient.DecodeReviewFrame(frame, null!, FramePrepared.Of(frame)));

        Assert.Contains("research section", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SyntheticProjectionCannotReadAbsentResearchAsNoFinishedProjects()
    {
        Assert.Equal("research census", Assert.Throws<RequiredFactException>(() => new ReviewFacts().Research).Fact);
        var research = ResearchTestFacts.Read();
        var facts = new ReviewFacts { Research = research };

        Assert.Same(research, facts.Research);
        Assert.Empty(facts.Research.Finished);
    }
}
