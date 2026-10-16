using RimGovernor.Brain.Policy;
using RimGovernor.Tools.Commands.ThoughtAudit;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

namespace RimGovernor.Brain.Domain.Tests;

// #3223: the shipped KnowBuriedInSarcophagus card's guards evaluated against observed facts, and the tool check of the two tables.
public sealed class MechanicCardGuardsTests
{
    private static readonly MechanicCard Shipped = MechanicCards.ByDef["KnowBuriedInSarcophagus"];

    private static readonly MechanicCard Verified = Shipped with { Status = MechanicCardStatus.Verified };

    private static readonly Fact<EquatableArray<MoodPawn>> Colonists = Known<EquatableArray<MoodPawn>>([new MoodPawn { Id = new PawnId("a"), Mood = Known(0.5) }]);

    private static WasteItem Corpse(CorpseOf of, bool everBuried = false) =>
        new() { Id = "c", Kind = "Corpse", State = WasteState.Exposed, Eligible = true, CorpseOf = of, EverBuriedInSarcophagus = everBuried };

    private static CardGuardFacts Fresh(WasteItem corpse) => new() { Target = corpse, Colonists = Colonists };

    // The sarcophagus rows left the shipped tables with the corpse-shaped search (#3284); the evaluator is still tested
    // against the shipped unreachable card through them as a fixture.
    private static readonly CardSymbolBinding[] Rows =
    [
        new("Corpse.everBuriedInSarcophagus", f => f.Target.CorpseOf == CorpseOf.None ? Unknown<bool>() : Known(f.Target.EverBuriedInSarcophagus)),
        new("RaceProperties.Humanlike", f => f.Target.CorpseOf == CorpseOf.None ? Unknown<bool>() : Known(f.Target.CorpseOf != CorpseOf.Animal)),
        new("MapPawns.FreeColonists", f => f.Colonists.TryGetValue(out var c) ? Known(c.Count > 0) : Unknown<bool>()),
        new("Pawn_NeedsTracker.mood", f => f.Colonists.TryGetValue(out var c) ? Known(c.Any(p => p.Mood.IsKnown)) : Unknown<bool>()),
        new("Building_Sarcophagus.everNonEmpty", f => f.Waste.Any(w => w.State == WasteState.Buried && w.Grave == f.Instance) ? Known(true) : Unknown<bool>()),
    ];

    private static readonly CardSiteAction[] Haul = [new("Building_Sarcophagus.Notify_HauledTo", ActionKind.Haul, "colonist", ["Pawn.IsColonist"])];

    private static CardEvaluation Evaluate(MechanicCard card, CardGuardFacts facts, IReadOnlyList<CardSymbolBinding>? bindings = null) =>
        MechanicCardGuards.Evaluate(card, Assert.Single(card.Sites), facts, bindings ?? Rows, Haul);

    private static CardGuardResult Guard(CardEvaluation e, string decl) =>
        e.Guards.Single(g => (g.Guard.Expr.Decl ?? g.Guard.Expr.Args![0].Decl) == decl);

    [Fact]
    public void StrangerCorpseAndFreshSarcophagusMakeTheVerifiedCardUsable()
    {
        var e = Evaluate(Verified, Fresh(Corpse(CorpseOf.Stranger)));

        Assert.True(e.Usable);
        Assert.True(e.Exhausted);
        Assert.All(e.Guards, g => Assert.Contains(g.Verdict, new[] { CardGuardVerdict.Holds, CardGuardVerdict.Achievable }));
        Assert.Equal(CardGuardVerdict.Holds, Guard(e, "Building_Sarcophagus.thisIsFirstBodyEver").Verdict);
        var hauler = Guard(e, "Pawn.IsColonist");
        Assert.Equal((CardGuardVerdict.Achievable, ActionKind.Haul, "colonist"), (hauler.Verdict, hauler.Action, hauler.Constraint));
    }

    [Fact]
    public void ExtractedOrUnreachableCardIsNeverUsableEvenWhenEveryGuardHolds()
    {
        Assert.Equal(MechanicCardStatus.Unreachable, Shipped.Status);
        foreach (var card in new[] { Shipped, Shipped with { Status = MechanicCardStatus.Extracted } })
        {
            var e = Evaluate(card, Fresh(Corpse(CorpseOf.Stranger)));

            Assert.DoesNotContain(e.Guards, g => g.Verdict is CardGuardVerdict.Fails or CardGuardVerdict.Unknown);
            Assert.False(e.Usable);
        }
    }

    [Fact]
    public void FlaggedCorpseFails()
    {
        var e = Evaluate(Verified, Fresh(Corpse(CorpseOf.Stranger, everBuried: true)));

        Assert.Equal(CardGuardVerdict.Fails, Guard(e, "Corpse.everBuriedInSarcophagus").Verdict);
        Assert.False(e.Usable);
    }

    [Fact]
    public void AnimalCorpseFails()
    {
        var e = Evaluate(Verified, Fresh(Corpse(CorpseOf.Animal)));

        Assert.Equal(CardGuardVerdict.Fails, Guard(e, "RaceProperties.Humanlike").Verdict);
        Assert.False(e.Usable);
    }

    [Fact]
    public void UsedSarcophagusFailsExhausted()
    {
        WasteItem[] waste = [new() { Id = "old", Kind = "Corpse", State = WasteState.Buried, CorpseOf = CorpseOf.Stranger, Grave = "s1" }];
        var e = Evaluate(Verified, Fresh(Corpse(CorpseOf.Stranger)) with { Instance = "s1", Waste = waste });

        Assert.Equal(CardGuardVerdict.Fails, Guard(e, "Building_Sarcophagus.thisIsFirstBodyEver").Verdict);
        Assert.True(e.Exhausted);
        Assert.False(e.Usable);
    }

    [Fact]
    public void EmptyExistingSarcophagusIsUnknownBecauseEverNonEmptyIsNotObserved()
    {
        var e = Evaluate(Verified, Fresh(Corpse(CorpseOf.Stranger)) with { Instance = "s1" });

        Assert.Equal(CardGuardVerdict.Unknown, Guard(e, "Building_Sarcophagus.thisIsFirstBodyEver").Verdict);
        Assert.False(e.Usable);
    }

    [Fact]
    public void RemovedBindingRowIsUnknownAndNotUsable()
    {
        var bindings = Rows.Where(b => b.Decl != "RaceProperties.Humanlike").ToList();
        var e = Evaluate(Verified, Fresh(Corpse(CorpseOf.Stranger)), bindings);

        var humanlike = Guard(e, "RaceProperties.Humanlike");
        Assert.Equal(CardGuardVerdict.Unknown, humanlike.Verdict);
        Assert.Contains("no binding", humanlike.Reason, StringComparison.Ordinal);
        Assert.False(e.Usable);
    }

    [Fact]
    public void UnreadColonistsAreUnknown()
    {
        var e = Evaluate(Verified, Fresh(Corpse(CorpseOf.Stranger)) with { Colonists = Unknown<EquatableArray<MoodPawn>>() });

        Assert.Equal(CardGuardVerdict.Unknown, Guard(e, "MapPawns.FreeColonists").Verdict);
        Assert.False(e.Usable);
    }

    // #3281: the shipped DeadMansApparel count card priced for one pawn and one outfit.
    private static readonly MechanicCard Tainted = MechanicCards.ByDef["DeadMansApparel"];

    private static readonly CardPawnFacts Plain = new() { Traits = Known<EquatableArray<string>>(["Kind"]), Hediffs = Known<EquatableArray<string>>([]), AnomalyActive = Known(true) };

    private static GearOption[] Wearing(int tainted) =>
        [.. Enumerable.Range(0, tainted).Select(i => new GearOption { ID = "t" + i, Tainted = true }), new GearOption { ID = "clean" }];

    private static Fact<double> Offset(int tainted, CardPawnFacts? pawn = null, IReadOnlyList<CardItemBinding>? bindings = null) =>
        MechanicCardGuards.CardStageOffset(Tainted, pawn ?? Plain, Wearing(tainted), bindings);

    // GearLoadout's hand curve: 5 + 3 * (min(n, 4) - 1) mood lost.
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
            [.. MechanicCardGuards.Bindings.Select(b => b.Decl), "Corpse.everBuriedInGrave"],
            [.. MechanicCardGuards.SiteActions, new CardSiteAction("Building_Grave.Notify_HauledTo", ActionKind.Haul, "colonist", [])],
            null);

        Assert.Equal(["binding Corpse.everBuriedInGrave is not in the card file", "site Building_Grave.Notify_HauledTo is not in the card file"], problems);
    }

    [Fact]
    public void TableCheckFailsOnAPreventionRowAtASiteWithoutACount()
    {
        var problems = GuardTableCheck.Problems(MechanicCards.ByDef, [],
            [new CardSiteAction("Building_Sarcophagus.Notify_HauledTo", ActionKind.ApparelPolicy, MechanicCardGuards.DisallowCounted, [])], null);

        Assert.Equal(["site Building_Sarcophagus.Notify_HauledTo has a guard that is not a count, so disallow-counted cannot falsify it"], problems);
    }

    [Fact]
    public void TableCheckFailsOnADeclAbsentFromTheSources()
    {
        var index = SourceIndex.Build([("s.cs", "class Corpse { public bool everBuriedInSarcophagus; }")]);

        var problems = GuardTableCheck.Problems(MechanicCards.ByDef, ["Corpse.everBuriedInSarcophagus", "RaceProperties.Humanlike"], [], index);

        Assert.Equal(["binding RaceProperties.Humanlike is not declared in the decompiled sources"], problems);
    }
}
