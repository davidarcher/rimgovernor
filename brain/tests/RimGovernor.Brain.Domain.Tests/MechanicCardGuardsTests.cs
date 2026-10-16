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

    private static CardEvaluation Evaluate(MechanicCard card, CardGuardFacts facts, IReadOnlyList<CardSymbolBinding>? bindings = null) =>
        MechanicCardGuards.Evaluate(card, Assert.Single(card.Sites), facts, bindings);

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
        var bindings = MechanicCardGuards.Bindings.Where(b => b.Decl != "RaceProperties.Humanlike").ToList();
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

    [Fact]
    public void TablesMatchTheEmbeddedCards() =>
        Assert.Empty(GuardTableCheck.Problems(MechanicCards.ByDef, MechanicCardGuards.Bindings.Select(b => b.Decl), MechanicCardGuards.SiteActions, null));

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
    public void TableCheckFailsOnADeclAbsentFromTheSources()
    {
        var index = SourceIndex.Build([("s.cs", "class Corpse { public bool everBuriedInSarcophagus; }")]);

        var problems = GuardTableCheck.Problems(MechanicCards.ByDef, ["Corpse.everBuriedInSarcophagus", "RaceProperties.Humanlike"], [], index);

        Assert.Equal(["binding RaceProperties.Humanlike is not declared in the decompiled sources"], problems);
    }
}
