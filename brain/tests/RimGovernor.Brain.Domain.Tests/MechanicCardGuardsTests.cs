using RimGovernor.Brain.Policy;
using RimGovernor.Tools.Commands.ThoughtAudit;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

// #3281: the shipped DeadMansApparel count card priced for one pawn and one outfit, and the tool check of the guard tables.
public sealed class MechanicCardGuardsTests
{
    private static readonly MechanicCard Tainted = MechanicCards.ByDef["DeadMansApparel"];

    private static readonly CardPawnFacts Plain = new() { Traits = ["Kind"], Hediffs = [], IdeologyActive = false, Precepts = [] };

    private static GearOption[] Wearing(int tainted) =>
        [.. Enumerable.Range(0, tainted).Select(i => new GearOption { Origin = new GearOptionOrigin.Observed(GearFixtures.Option("t" + i, "", "", GearSource.Worn) with { Condition = 0, Quality = new RimGovernor.Brain.GameFacts.Observations.ItemQuality.Graded(RimGovernor.Brain.GameFacts.Defs.QualityCategory.Awful), Tainted = true }) }), new GearOption { Origin = new GearOptionOrigin.Observed(GearFixtures.Option("clean", "", "", GearSource.Worn) with { Condition = 0, Quality = new RimGovernor.Brain.GameFacts.Observations.ItemQuality.Graded(RimGovernor.Brain.GameFacts.Defs.QualityCategory.Awful) }) }];

    private static CardOffset Offset(int tainted, CardPawnFacts? pawn = null, IReadOnlyList<CardItemBinding>? bindings = null) =>
        MechanicCardGuards.CardStageOffset(Tainted, pawn ?? Plain, Wearing(tainted), bindings);

    private static CardOffset.Staged Staged(double offset) => new CardOffset.Staged(offset);

    // The hand curve #3285 deleted from GearLoadout: 5 + 3 * (min(n, 4) - 1) mood lost.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void StageOffsetMatchesTheHandCurve(int tainted) => Assert.Equal(Staged(-(5.0 + (3 * (tainted - 1)))), Offset(tainted));

    // Native returns stage 4 for five or more; ThoughtState.StageIndexFor clamps it to the last defined stage (observed by apparel/dead_mans, #3283).
    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    public void FiveOrMoreClampToTheLastStage(int tainted) => Assert.Equal(Staged(-14.0), Offset(tainted));

    [Fact]
    public void NoTaintedItemGivesZero() => Assert.Equal(Staged(0), Offset(0));

    [Fact]
    public void BloodlustPawnGivesZero() => Assert.Equal(Staged(0), Offset(2, Plain with { Traits = ["Bloodlust"] }));

    [Fact]
    public void InhumanizedPawnGivesZero() => Assert.Equal(Staged(0), Offset(2, Plain with { Hediffs = ["Inhumanized"] }));

    [Fact]
    public void ABindingTheTableLacksLeavesTheCardUnmodeled() => Assert.IsType<CardOffset.Unmodeled>(Offset(2, bindings: []));

    [Fact]
    public void ACardWithNoSiteIsUnmodeled()
    {
        var empty = Tainted with { Sites = [] };

        Assert.IsType<CardOffset.Unmodeled>(MechanicCardGuards.CardStageOffset(empty, Plain, Wearing(2)));
        Assert.IsType<CardOffset.Unmodeled>(MechanicCardGuards.CardStageOffset(empty, Plain, new(10, 30, 5)));
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
        Assert.Empty(GuardTableCheck.Problems(MechanicCards.ByDef, MechanicCardGuards.BoundDecls, MechanicCardGuards.SiteActions, null));

    [Fact]
    public void TableCheckFailsOnADeclOrSiteAbsentFromTheCards()
    {
        var problems = GuardTableCheck.Problems(
            MechanicCards.ByDef,
            [.. MechanicCardGuards.BoundDecls, "Corpse.everBuriedInGrave"],
            [.. MechanicCardGuards.SiteActions, new CardSiteAction("Building_Grave.Notify_HauledTo", "colonist")],
            null);

        Assert.Equal(["binding Corpse.everBuriedInGrave is not in the card file", "site Building_Grave.Notify_HauledTo is not in the card file"], problems);
    }

    [Fact]
    public void TableCheckFailsOnAPreventionRowAtASiteWithoutACount()
    {
        var problems = GuardTableCheck.Problems(MechanicCards.ByDef, [],
            [new CardSiteAction("Building_Sarcophagus.Notify_HauledTo", MechanicCardGuards.DisallowCounted)], null);

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

    // #3327: the extracted EnvironmentCold/Hot stage cards (bands 10/20/30, Temperature_Tough -2) priced at one moment.
    private static readonly CardPawnFacts NoIdeology = Plain;

    private static CardOffset Cold(double deficit, CardPawnFacts? pawn = null) =>
        MechanicCardGuards.CardStageOffset(MechanicCards.ByDef["EnvironmentCold"], pawn ?? NoIdeology, new(10, 30, 10 - deficit));

    [Theory]
    [InlineData(5, -4.0)]
    [InlineData(15, -8.0)]
    [InlineData(25, -12.0)]
    [InlineData(35, -16.0)]
    [InlineData(0, 0.0)]
    [InlineData(-5, 0.0)]
    public void ColdDeficitPicksTheBand(double deficit, double offset) => Assert.Equal(Staged(offset), Cold(deficit));

    [Fact]
    public void HotExcessPicksTheBand() =>
        Assert.Equal(Staged(-8.0), MechanicCardGuards.CardStageOffset(MechanicCards.ByDef["EnvironmentHot"], NoIdeology, new(10, 30, 45)));

    [Fact]
    public void TemperatureToughShiftsTwoStagesDown()
    {
        var tough = Plain with { IdeologyActive = true, Precepts = ["Temperature_Tough"] };

        Assert.Equal(Staged(0), Cold(15, tough));
        Assert.Equal(Staged(-4.0), Cold(25, tough));
        // Another ideo, or none, keeps the band.
        Assert.Equal(Staged(-8.0), Cold(15, tough with { Precepts = ["Temperature_Tender"] }));
        Assert.Equal(Staged(-8.0), Cold(15, tough with { Precepts = [] }));
        // The precept only counts while Ideology is active.
        Assert.Equal(Staged(-8.0), Cold(15, tough with { IdeologyActive = false }));
    }

    [Fact]
    public void AnUnboundStageInputLeavesTheCardUnmodeled()
    {
        var card = MechanicCards.ByDef["EnvironmentCold"];
        var site = card.Sites[0];
        var unbound = card with { Sites = [site with { Stage = site.Stage! with { Input = new(Kind: "prop", Decl: "Pawn.Wetness") } }] };

        Assert.IsType<CardOffset.Unmodeled>(MechanicCardGuards.CardStageOffset(unbound, NoIdeology, new(10, 30, 5)));
        Assert.False(MechanicCardGuards.TryStageInput(unbound.Sites[0], NoIdeology, new(10, 30, 5), out _));
    }

    // A guard that is false decides the site whatever an unmodeled guard beside it says; with none false, the unmodeled one leaves the card unmodeled.
    [Fact]
    public void AFalseGuardDecidesBesideAnUnmodeledOne()
    {
        var card = MechanicCards.ByDef["EnvironmentCold"];
        var site = card.Sites[0];
        MechanicCardGuard Guard(string lit) => new(new(Lit: lit));
        MechanicCardGuard unmodeled = new(new(Op: "unknown-op"));
        CardOffset At(params MechanicCardGuard[] guards) =>
            MechanicCardGuards.CardStageOffset(card with { Sites = [site with { Guards = [.. guards] }] }, NoIdeology, new(10, 30, -5));

        Assert.Equal(Staged(0), At(unmodeled, Guard("false")));
        Assert.IsType<CardOffset.Unmodeled>(At(unmodeled, Guard("true")));
    }
}
