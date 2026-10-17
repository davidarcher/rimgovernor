using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Policy.Methods.Generation;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;
using static RimGovernor.Brain.Policy.PolicyFunctions;

namespace RimGovernor.Brain.Domain.Tests;

// #3284: the effect-source generator's prevention branch. A verified negative count card active for a pawn gives one
// labor-free apparel-filter candidate carrying the card's offset change and the predicted outfit's insulation change.
// The card is a verified copy of the shipped DeadMansApparel card (so the tests do not depend on its shipped status);
// the generator itself names no def.
public sealed class EffectSourceCandidatesTests
{
    private const string Def = "DeadMansApparel";
    private static readonly MechanicCard Shipped = MechanicCards.ByDef[Def];
    private static readonly MechanicCard Verified = Shipped with { Status = MechanicCardStatus.Verified };

    private static GearOption Garment(string id, GearSlot slot, string layer, string group, double cold = 0, bool tainted = false,
        GearSource source = GearSource.Worn) => new()
    {
        ID = id, Definition = new("Apparel_" + id), Quality = 2, Slot = slot, Layers = [layer], Groups = [group], Source = source,
        Condition = 1, Cold = cold, Tainted = tainted,
    };

    // A pawn in a dead man's parka (cold insulation 30) over a clean shirt and pants; the only other parka is worse (10).
    internal static EffectSourcePawn Pawn(string id = "pawn", bool locked = false, string[]? traits = null) =>
        new(id, new GearLoadoutInput
        {
            Ambient = -10, ComfortableMin = 10, ComfortableMax = 30,
            Worn = [Garment("shirt", GearSlot.SkinTorso, "OnSkin", "Torso"), Garment("pants", GearSlot.SkinLegs, "OnSkin", "Legs"),
                Garment("parka", GearSlot.Outer, "Shell", "Torso", 30, tainted: true) with { Locked = locked }],
            Options = [Garment("thin-parka", GearSlot.Outer, "Shell", "Torso", 10, source: GearSource.Stored)],
        }, new CardPawnFacts
        {
            Traits = Known<EquatableArray<string>>([.. traits ?? []]), Hediffs = Known<EquatableArray<string>>([]), AnomalyActive = Known(false),
        });

    internal static EffectSourceFacts Facts(int budget = 32, params EffectSourcePawn[] pawns) =>
        new() { Pawns = pawns.Length == 0 ? [Pawn()] : pawns, Budget = budget };

    internal static EffectSourceResult Generate(EffectSourceFacts facts, MechanicCard? card = null) =>
        GenerateEffectSources(facts, new Dictionary<string, MechanicCard> { [Def] = card ?? Verified });

    [Fact]
    public void TaintedParkaGivesOneLaborFreeDisallowCandidateWithItsMoodAndInsulationChange()
    {
        var result = Generate(Facts());

        var root = Assert.Single(result.Roots);
        Assert.Equal("gear-filter/pawn/disallow-" + Def, root.Id);
        Assert.Empty(root.ActivityCompletions);
        Assert.Equal("gear-filter/pawn", root.ExclusiveOutcome);
        // The -5 stage goes away; the thin parka insulates 20 less, so the comfortable minimum rises by 20.
        Assert.Equal([new ForecastSituationalChange("pawn", Def, 5)], root.SituationalChanges);
        Assert.Equal([new ForecastComfortShift("pawn", 20, 0)], root.ComfortShifts);
        Assert.Empty(result.Findings);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void ALockedTaintedParkaStaysUnderTheDisallowVariant()
    {
        var gear = Pawn(locked: true).Gear;

        Assert.Contains(PlanGearLoadout(gear, o => o.Tainted).Target, o => o.ID == "parka");
        Assert.DoesNotContain(PlanGearLoadout(Pawn().Gear, o => o.Tainted).Target, o => o.ID == "parka");
    }

    [Fact]
    public void BloodlustPawnGetsNoCandidate()
    {
        var result = Generate(Facts(pawns: Pawn(traits: ["Bloodlust"])));

        Assert.Empty(result.Roots);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void ACardThatIsNotLiveGetsNoCandidate()
    {
        var extracted = Generate(Facts(), Shipped with { Status = MechanicCardStatus.Extracted });
        Assert.Empty(extracted.Roots);
        Assert.Equal(["effect_source_card_unverified:" + Def], extracted.Findings);

        // The shipped unreachable sarcophagus card is the negative benchmark.
        var unreachable = GenerateEffectSources(Facts(), new Dictionary<string, MechanicCard> { ["KnowBuriedInSarcophagus"] = MechanicCards.ByDef["KnowBuriedInSarcophagus"] });
        Assert.Empty(unreachable.Roots);
        Assert.Equal(["effect_source_card_unreachable:KnowBuriedInSarcophagus"], unreachable.Findings);
    }

    [Fact]
    public void AMissingBindingGivesNoCandidateAndAnUnknownFinding()
    {
        var unbound = Verified with
        {
            Sites = [.. Verified.Sites.Select(s => s with
            {
                Guards = [.. s.Guards.Select(g => g with { Count = g.Count! with { Where = g.Count.Where with { Decl = "Apparel.Unbound" } } })],
            })],
        };

        var result = Generate(Facts(), unbound);

        Assert.Empty(result.Roots);
        Assert.Equal(["effect_source_offset_unknown:" + Def + "/pawn"], result.Findings);
    }

    [Fact]
    public void IdenticalInputsInAnyOrderGiveIdenticalOutputInPawnOrder()
    {
        var facts = Facts(pawns: [Pawn("b"), Pawn("a"), Pawn("c", traits: ["Bloodlust"])]);
        var shuffled = facts with { Pawns = [.. facts.Pawns.Reverse()] };

        var one = Signature(Generate(facts));
        Assert.Equal(one, Signature(Generate(facts)));
        Assert.Equal(one, Signature(Generate(shuffled)));
        Assert.Equal(["gear-filter/a/disallow-" + Def, "gear-filter/b/disallow-" + Def], Generate(facts).Roots.Select(r => r.Id));
    }

    [Fact]
    public void ExhaustedBudgetIsReported()
    {
        var result = Generate(Facts(1, Pawn("b"), Pawn("a")));

        Assert.True(result.Truncated);
        Assert.Equal(["effect_source_generation_truncated"], result.Findings);
        Assert.Equal("gear-filter/a/disallow-" + Def, Assert.Single(result.Roots).Id);
    }

    // #3285: the census facts the coupled generator builds. Hediffs come from the medical read; unread, the card's offset is
    // unknown, so the pawn gets a finding and no candidate (its filter keeps the default).
    [Fact]
    public void CensusFactsReadHediffsFromTheMedicalRead()
    {
        var model = Pawn().Gear;
        model = model with { Role = model.Role with { Work = model.Role.Work with { Traits = Known<EquatableArray<PawnTrait>>([new() { Name = "Kind" }]) } } };
        var gear = new GearObservation { Pawns = [new GearPawn { Pawn = new("pawn"), LoadoutModel = Known(model) }, new GearPawn { Pawn = new("blocked"), Blocked = true }] };
        var care = new CarePawn { Id = new("pawn"), Conditions = Known<EquatableArray<CareCondition>>([new() { DefName = Known("Flu") }]) };

        var facts = EffectSourceFactsOf(gear, Known<EquatableArray<CarePawn>>([care]));
        var read = Assert.Single(facts.Pawns);
        Assert.Equal(Known<EquatableArray<string>>(["Flu"]), read.Card.Hediffs);
        Assert.Equal("gear-filter/pawn/disallow-" + Def, Assert.Single(Generate(facts).Roots).Id);

        var unread = Generate(EffectSourceFactsOf(gear, Unknown<EquatableArray<CarePawn>>()));
        Assert.Empty(unread.Roots);
        Assert.Equal(["effect_source_offset_unknown:" + Def + "/pawn"], unread.Findings);
    }

    // #3285 parity, the default: a pawn the comparator did not leave allowed plans and dresses with tainted apparel out.
    [Fact]
    public void ByDefaultNoPawnIsAllowedTaintedApparel()
    {
        var model = Pawn().Gear;
        var state = new ApparelPolicyState
        {
            Token = "t", PawnName = "pawn", PolicyID = "outfit-1",
            Definitions = [new() { Name = "Apparel_parka", Adult = true }, new() { Name = "Apparel_shirt", Adult = true }, new() { Name = "Apparel_pants", Adult = true }],
            Current = new(new("pawn"), "t", "pawn", ["Apparel_pants", "Apparel_parka", "Apparel_shirt"], .51, 1, 2, 6),
            ExcludesTainted = true,
        };
        var pawn = new GearPawn { Pawn = new("pawn"), Policy = Known(state), LoadoutModel = Known(model) };

        Assert.False(DesiredApparelPolicy(pawn, out _));
        Assert.DoesNotContain(PlanColonyGear([pawn]).Loadouts[0].Target, o => o.ID == "parka");

        Assert.True(DesiredApparelPolicy(pawn with { AllowTainted = true }, out var allowed));
        Assert.True(allowed!.Spec.AllowTainted);
        Assert.Contains(PlanColonyGear([pawn with { AllowTainted = true }]).Loadouts[0].Target, o => o.ID == "parka");
    }

    // #3325: the one stamp on the rounds census reaches every gear reader. A pawn the comparator left allowed, in a tainted
    // parka a bill could replace, is owed no replacement: no bench demand and no gear bill is wanted for it.
    [Fact]
    public void AnAllowedPawnInATaintedParkaGetsNoReplacementBillDemandAndNoOrder()
    {
        var model = Pawn().Gear with { Options = [Garment("new-parka", GearSlot.Outer, "Shell", "Torso", 30, source: GearSource.Bill)] };
        var gear = new GearObservation
        {
            Stored = Known<EquatableArray<GearStock>>([]),
            Pawns = [new GearPawn { Pawn = new("pawn"), Loadout = "worker", LoadoutModel = Known(model), Candidates = Known<EquatableArray<GearCandidate>>([]) }],
        };
        var facts = new RoundsFacts { Gear = Known(gear) };

        var standing = RimGovernor.Brain.BuildingRuntime.Rounder.WithGearFilters(facts, []).Gear;
        Assert.NotEmpty(GearReplacementNeeds(standing));

        var allowed = RimGovernor.Brain.BuildingRuntime.Rounder.WithGearFilters(facts, ["pawn"]).Gear;
        Assert.True(Assert.Single(allowed.Value.Pawns).AllowTainted);
        Assert.Equal(Known(true), ReviewGear(allowed).Recovered);
        Assert.Empty(GearReplacementNeeds(allowed));
        var (wanted, known) = GearBillsWanted(allowed);
        Assert.True(known);
        Assert.Empty(wanted!);
    }

    private static string Signature(EffectSourceResult result) => string.Join("\n",
        result.Roots.Select(r => r.Id + "|" + r.ExclusiveOutcome + "|" + string.Join(",", r.SituationalChanges) + "|" + string.Join(",", r.ComfortShifts))
            .Concat(result.Findings));
}
