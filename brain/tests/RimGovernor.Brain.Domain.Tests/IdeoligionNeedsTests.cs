using RimGovernor.Brain.Policy;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>The believers' needs (#3953): the census is complete or raises, and an unread mood row is a named state.</summary>
public sealed class IdeoligionNeedsTests
{
    private const string Ours = "Ideo_ours";

    private static IdeoligionFacts Ideo(int believers) => new IdeoligionFacts { Observed = IdeologyReadsTests.NoPrecepts with { IdeoId = Ours, Believers = believers } };

    private static WorkPawn Believer(string id)
    {
        var row = PawnFixtures.Row(id);
        return PawnFixtures.Worker(id) with { Observed = row with { Settings = row.Settings with { PolicyInputs = PawnFixtures.Inputs(Ours, 0.5) } } };
    }

    private static MoodPawn Mood(string id) => MoodFixtures.Pawn(id);

    [Fact]
    public void AMoodRowForEveryBelieverGivesHeldThoughts()
    {
        var needs = IdeoligionDesignRules.IdeoligionNeeds(Ideo(2), EquatableArray.From(new[] { Believer("a"), Believer("b") }), EquatableArray.From(new[] { Mood("a"), Mood("b") }));

        Assert.Equal(2, needs.Believers);
        Assert.IsType<BelieverThoughts.Held>(needs.Thoughts);
    }

    [Fact]
    public void ABelieverWithoutAMoodRowLeavesTheThoughtsUnjudged()
    {
        var needs = IdeoligionDesignRules.IdeoligionNeeds(Ideo(2), EquatableArray.From(new[] { Believer("a"), Believer("b") }), EquatableArray.From(new[] { Mood("a") }));

        Assert.IsType<BelieverThoughts.Unjudged>(needs.Thoughts);
    }

    [Fact]
    public void ARowCensusThatMissesABelieverRaises()
    {
        var fact = Assert.Throws<RequiredFactException>(() => IdeoligionDesignRules.IdeoligionNeeds(Ideo(2), EquatableArray.From(new[] { Believer("a") }), default)).Fact;

        Assert.Equal("ideoligion believers", fact);
    }
}
