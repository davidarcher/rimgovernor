using RimGovernor.Brain.Policy;
using RimGovernor.Tools.Commands.ThoughtAudit;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

// #3281: the shipped DeadMansApparel count card priced for one pawn and one outfit, and the tool check of the guard tables.
public sealed class MechanicCardGuardsTests
{
    private static readonly MechanicCard Tainted = MechanicCards.ByDef["DeadMansApparel"];

    private static readonly CardPawnFacts Plain = new() { Traits = Known<EquatableArray<string>>(["Kind"]), Hediffs = Known<EquatableArray<string>>([]), AnomalyActive = Known(true) };

    private static GearOption[] Wearing(int tainted) =>
        [.. Enumerable.Range(0, tainted).Select(i => new GearOption { ID = "t" + i, Tainted = true }), new GearOption { ID = "clean" }];

    private static Fact<double> Offset(int tainted, CardPawnFacts? pawn = null, IReadOnlyList<CardItemBinding>? bindings = null) =>
        MechanicCardGuards.CardStageOffset(Tainted, pawn ?? Plain, Wearing(tainted), bindings);

    // The hand curve #3285 deleted from GearLoadout: 5 + 3 * (min(n, 4) - 1) mood lost.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void StageOffsetMatchesTheHandCurve(int tainted) => Assert.Equal(Known(-(5.0 + (3 * (tainted - 1)))), Offset(tainted));

    // Native returns stage 4 for five or more; ThoughtState.StageIndexFor clamps it to the last defined stage (observed by apparel/dead_mans, #3283).
    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    public void FiveOrMoreClampToTheLastStage(int tainted) => Assert.Equal(Known(-14.0), Offset(tainted));

    [Fact]
    public void NoTaintedItemGivesZero() => Assert.Equal(Known(0.0), Offset(0));

    [Fact]
    public void BloodlustPawnGivesZero() => Assert.Equal(Known(0.0), Offset(2, Plain with { Traits = Known<EquatableArray<string>>(["Bloodlust"]) }));

    [Fact]
    public void InhumanizedPawnGivesZero() => Assert.Equal(Known(0.0), Offset(2, Plain with { Hediffs = Known<EquatableArray<string>>(["Inhumanized"]) }));

    [Fact]
    public void UnreadHediffsAreAbsentOnlyWhileAnomalyIsInactive()
    {
        var unread = Plain with { Hediffs = Unknown<EquatableArray<string>>() };

        Assert.Equal(Known(-5.0), Offset(1, unread with { AnomalyActive = Known(false) }));
        Assert.False(Offset(1, unread).IsKnown);
        Assert.False(Offset(1, unread with { AnomalyActive = Unknown<bool>() }).IsKnown);
    }

    [Fact]
    public void MissingBindingOrObservationIsUnknown()
    {
        Assert.False(Offset(2, bindings: []).IsKnown);
        Assert.False(Offset(2, Plain with { Traits = Unknown<EquatableArray<string>>() }).IsKnown);
    }

    // #3283: the native probe apparel/dead_mans passed at the card's drift hash, so the card ships verified.
    [Fact]
    public void ProbedCountCardIsVerifiedAndLive()
    {
        Assert.Equal(MechanicCardStatus.Verified, Tainted.Status);
        Assert.True(Tainted.Live);
    }

    [Fact]
    public void TablesMatchTheEmbeddedCards() =>
        Assert.Empty(GuardTableCheck.Problems(MechanicCards.ByDef, MechanicCardGuards.BoundDecls, MechanicCardGuards.SiteActions, null, MechanicCardGuards.AnomalyHediffs));

    [Fact]
    public void TableCheckFailsOnADeclOrSiteAbsentFromTheCards()
    {
        var problems = GuardTableCheck.Problems(
            MechanicCards.ByDef,
            [.. MechanicCardGuards.BoundDecls, "Corpse.everBuriedInGrave"],
            [.. MechanicCardGuards.SiteActions, new CardSiteAction("Building_Grave.Notify_HauledTo", ActionKind.Haul, "colonist")],
            null);

        Assert.Equal(["binding Corpse.everBuriedInGrave is not in the card file", "site Building_Grave.Notify_HauledTo is not in the card file"], problems);
    }

    [Fact]
    public void TableCheckFailsOnAPreventionRowAtASiteWithoutACount()
    {
        var problems = GuardTableCheck.Problems(MechanicCards.ByDef, [],
            [new CardSiteAction("Building_Sarcophagus.Notify_HauledTo", ActionKind.ApparelPolicy, MechanicCardGuards.DisallowCounted)], null);

        Assert.Equal(["site Building_Sarcophagus.Notify_HauledTo has a guard that is not a count, so disallow-counted cannot falsify it"], problems);
    }

    [Fact]
    public void TableCheckFailsOnADeclAbsentFromTheSources()
    {
        var index = SourceIndex.Build([("s.cs", "class Corpse { public bool everBuriedInSarcophagus; }")]);

        var problems = GuardTableCheck.Problems(MechanicCards.ByDef, ["Corpse.everBuriedInSarcophagus", "RaceProperties.Humanlike"], [], index);

        Assert.Equal(["binding RaceProperties.Humanlike is not declared in the decompiled sources"], problems.Where(p => p.StartsWith("binding", StringComparison.Ordinal)));
        // #3324: a stage band's symbols are checked against the sources too.
        Assert.Contains("stage symbol Ideo.HasPrecept of site ThoughtWorker_Cold.CurrentStateInternal is not declared in the decompiled sources", problems);
    }
}
