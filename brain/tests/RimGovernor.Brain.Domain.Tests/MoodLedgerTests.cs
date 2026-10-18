using RimGovernor.Brain.Policy;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The mood ledger routes thoughts by owner (#2549, #2538, ported from the mood/ledger case in #3406): an owned thought
/// carries its audited owner and stays out of the unowned bucket, an unowned one lands there, and sources rank by
/// mood lost.
/// </summary>
public sealed class MoodLedgerTests
{
    // Thought def names as the audit table (thought_triggers.tsv) keys them; the brain has no DefOf route to them.
    private const string Owned = "SleptOutside";
    private const string Free = "MyOrganHarvested";

    [Fact]
    public void ThoughtsAreRoutedByOwnerAndRankedByMoodLost()
    {
        var pawn = new MoodLedgerPawn
        {
            Thoughts = Known(EquatableArray.From<MoodThought>([new(Owned, -4), new(Free, -6)])),
            Traits = Known(EquatableArray.From<string>([])),
            Precepts = Known(EquatableArray.From<string>([])),
            Expectation = Known("Moderate"),
        };

        var ledger = MoodRules.BuildMoodLedger([pawn], new Dictionary<string, ThoughtFacts>());

        Assert.Equal([Free, Owned], ledger.Sources.Select(s => s.Def));
        var free = ledger.Sources[0];
        var owned = ledger.Sources[1];
        Assert.Equal([ShelterFamily.Sleeping], owned.Owners);
        Assert.Empty(free.Owners);
        Assert.Equal((1, -4.0), (owned.Pawns, owned.Lost));
        Assert.Equal((1, -6.0), (free.Pawns, free.Lost));
        Assert.Equal([Free], ledger.Unowned.Select(s => s.Def));
    }
}
