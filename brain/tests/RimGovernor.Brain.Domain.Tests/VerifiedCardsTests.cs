using RimGovernor.Tools.Commands.ThoughtAudit;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// The thoughtaudit verified-cards table (#3222), compiled in from tools/rg: a changed method drops a card back to extracted.
public sealed class VerifiedCardsTests
{
    private static readonly HashSet<string> Cards = new(StringComparer.Ordinal) { "KnowBuriedInSarcophagus" };

    private static string Table(string drift) =>
        VerifiedCards.Header + "\nKnowBuriedInSarcophagus\t" + drift + "\tburial/stranger_sarcophagus\t1.7.4979 rev109\trun pass\n";

    [Fact]
    public void ADriftHashMismatchIsExtractedAndNamesTheStaleRow()
    {
        var probed = VerifiedCards.DriftHash(["91b24c626f95fc53", "aaaa000000000000"]);
        var changed = VerifiedCards.DriftHash(["91b24c626f95fc53", "bbbb000000000000"]);
        var rows = VerifiedCards.Parse(Table(probed), Cards);

        var current = VerifiedCards.Match("KnowBuriedInSarcophagus", probed, rows);
        Assert.True(current.Verified);
        Assert.Empty(current.Stale);
        var (verified, stale) = VerifiedCards.Match("KnowBuriedInSarcophagus", changed, rows);
        Assert.False(verified);
        var row = Assert.Single(stale);
        Assert.Equal((2, probed, "burial/stranger_sarcophagus"), (row.Line, row.DriftHash, row.Probe));
    }

    [Fact]
    public void TheDriftHashIgnoresMemberOrderAndDuplicates()
    {
        Assert.Equal(VerifiedCards.DriftHash(["b", "a", "a"]), VerifiedCards.DriftHash(["a", "b"]));
        Assert.NotEqual(VerifiedCards.DriftHash(["a", "b"]), VerifiedCards.DriftHash(["a", "c"]));
    }

    [Fact]
    public void ARowForAnUnknownDefOrAMalformedTableIsRejected()
    {
        var unknown = Assert.Throws<InvalidDataException>(() => VerifiedCards.Parse(Table("x").Replace("KnowBuriedInSarcophagus", "AteWithoutTable", StringComparison.Ordinal), Cards));
        Assert.Contains("AteWithoutTable is not a card", unknown.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => VerifiedCards.Parse("def\thash\n", Cards));
        Assert.Throws<InvalidDataException>(() => VerifiedCards.Parse(VerifiedCards.Header + "\nKnowBuriedInSarcophagus\tx\n", Cards));
        Assert.Empty(VerifiedCards.Parse("", Cards));
    }

    [Fact]
    public void ARowForAnUnreachableCardIsRejected()
    {
        var e = Assert.Throws<InvalidDataException>(() => VerifiedCards.Parse(Table("x"), Cards, Cards));
        Assert.Contains("KnowBuriedInSarcophagus is unreachable", e.Message, StringComparison.Ordinal);
    }
}
