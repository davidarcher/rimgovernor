using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Policy.Methods.Generation;
using Xunit;
using static RimGovernor.Brain.Domain.Fact;

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

    internal static GearOption Garment(string id, GearSlot slot, string layer, string group, double cold = 0, bool tainted = false,
        GearSource source = GearSource.Worn) => new()
    {
        ID = id, Definition = new("Apparel_" + id), Quality = 2, Slot = slot, Layers = [layer], Groups = [group], Source = source,
        Condition = 1, Cold = cold, Tainted = tainted,
    };

    // A pawn in a dead man's parka (cold insulation 30) over a clean shirt and pants; the only other parka is worse (10).
    internal static EffectSourcePawn Pawn(string id = "pawn", bool locked = false, string[]? traits = null) =>
        new(id, new GearLoadoutInput
        { Threat = ColonyModelFixtures.NoThreat,
            Ambient = -10, ComfortableMin = 10, ComfortableMax = 30,
            Worn = [Garment("shirt", GearSlot.SkinTorso, "OnSkin", "Torso"), Garment("pants", GearSlot.SkinLegs, "OnSkin", "Legs"),
                Garment("parka", GearSlot.Outer, "Shell", "Torso", 30, tainted: true) with { Locked = locked }],
            Options = [Garment("thin-parka", GearSlot.Outer, "Shell", "Torso", 10, source: GearSource.Stored)],
        }, new CardPawnFacts
        {
            Traits = Known<EquatableArray<string>>([.. traits ?? []]), Hediffs = Known<EquatableArray<string>>([]), AnomalyActive = Known(false),
        });

    // #3329: a pawn in a shirt and pants (no insulation) with a parka (cold insulation 30, tainted or not) loose.
    internal static EffectSourcePawn WarmPawn(bool tainted) => Pawn() with
    {
        Gear = Pawn().Gear with
        {
            Worn = [Garment("shirt", GearSlot.SkinTorso, "OnSkin", "Torso"), Garment("pants", GearSlot.SkinLegs, "OnSkin", "Legs")],
            Options = [Garment("parka", GearSlot.Outer, "Shell", "Torso", 30, tainted, GearSource.Loose)],
        },
    };

    /// <summary>The gear census of one pawn, as the rounds read it.</summary>
    internal static GearObservation Census(EffectSourcePawn pawn) => ColonyModelFixtures.NoGear() with
    {
        Stored = [],
        Pawns = [new GearPawn { Pawn = new(pawn.Pawn), LoadoutModel = new GearModel.Modeled(pawn.Gear), Candidates = [] }],
    };

    internal static EffectSourceFacts Facts(int budget = 32, params EffectSourcePawn[] pawns) =>
        new() { Pawns = pawns.Length == 0 ? [Pawn()] : pawns, Budget = budget };

    /// <summary>Generates with the DeadMansApparel card and, when <paramref name="temperature"/> is set, the shipped EnvironmentCold and EnvironmentHot cards at that status.</summary>
    internal static EffectSourceResult Generate(EffectSourceFacts facts, MechanicCard? card = null, MechanicCardStatus? temperature = null)
    {
        var cards = new Dictionary<string, MechanicCard> { [Def] = card ?? Verified };
        if (temperature is { } status)
        {
            foreach (var def in new[] { "EnvironmentCold", "EnvironmentHot" })
            {
                cards[def] = MechanicCards.ByDef[def] with { Status = status };
            }
        }
        return EffectSourcesRules.GenerateEffectSources(facts, cards);
    }

    [Fact]
    public void TaintedParkaGivesOneLaborFreeDisallowCandidateWithItsMoodAndInsulationChange()
    {
        var result = Generate(Facts());

        var root = Assert.Single(result.Roots);
        Assert.Equal("gear/pawn/disallow-tainted/cold30-heat0", root.Id);
        Assert.Empty(root.ActivityCompletions);
        Assert.Equal("gear/pawn", root.ExclusiveOutcome);
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

        Assert.Contains(GearLoadoutRules.PlanGearLoadout(gear, o => o.Tainted).Target, o => o.ID == "parka");
        Assert.DoesNotContain(GearLoadoutRules.PlanGearLoadout(Pawn().Gear, o => o.Tainted).Target, o => o.ID == "parka");
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
        var unreachable = EffectSourcesRules.GenerateEffectSources(Facts(), new Dictionary<string, MechanicCard> { ["KnowBuriedInSarcophagus"] = MechanicCards.ByDef["KnowBuriedInSarcophagus"] });
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
        Assert.Equal(["gear/a/disallow-tainted/cold30-heat0", "gear/b/disallow-tainted/cold30-heat0"], Generate(facts).Roots.Select(r => r.Id));
    }

    [Fact]
    public void ExhaustedBudgetIsReported()
    {
        var result = Generate(Facts(1, Pawn("b"), Pawn("a")));

        Assert.True(result.Truncated);
        Assert.Equal(["effect_source_generation_truncated"], result.Findings);
        Assert.Equal("gear/a/disallow-tainted/cold30-heat0", Assert.Single(result.Roots).Id);
    }

    // #3285: the census facts the coupled generator builds. Hediffs come from the medical read; unread, the card's offset is
    // unknown, so the pawn gets a finding and no candidate (its filter keeps the default).
    [Fact]
    public void CensusFactsReadHediffsFromTheMedicalRead()
    {
        var model = Pawn().Gear;
        model = model with { Role = model.Role with { Work = model.Role.Work with { Traits = Known<EquatableArray<PawnTrait>>([new() { Name = "Kind" }]) } } };
        var gear = ColonyModelFixtures.NoGear() with { Pawns = [new GearPawn { Pawn = new("pawn"), LoadoutModel = new GearModel.Modeled(model) }, new GearPawn { Pawn = new("blocked"), Blocked = true, LoadoutModel = new GearModel.NoOutfit() }] };
        var care = CareFixtures.Patient("pawn", [CareFixtures.Condition("Flu")]);

        var facts = EffectSourcesRules.EffectSourceFactsOf(gear, Known<EquatableArray<CarePawn>>([care]));
        var read = Assert.Single(facts.Pawns);
        Assert.Equal(Known<EquatableArray<string>>(["Flu"]), read.Card.Hediffs);
        Assert.Equal("gear/pawn/disallow-tainted/cold30-heat0", Assert.Single(Generate(facts).Roots).Id);

        var unread = Generate(EffectSourcesRules.EffectSourceFactsOf(gear, Unknown<EquatableArray<CarePawn>>()));
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
        var pawn = new GearPawn { Pawn = new("pawn"), Policy = state, LoadoutModel = new GearModel.Modeled(model) };

        Assert.False(ApparelPolicyRules.DesiredApparelPolicy(pawn, out _));
        Assert.DoesNotContain(GearLoadoutRules.PlanColonyGear([pawn])[0].Target, o => o.ID == "parka");

        Assert.True(ApparelPolicyRules.DesiredApparelPolicy(pawn with { AllowTainted = true }, out var allowed));
        Assert.True(allowed!.Spec.AllowTainted);
        Assert.Contains(GearLoadoutRules.PlanColonyGear([pawn with { AllowTainted = true }])[0].Target, o => o.ID == "parka");
    }

    // #3325: the one stamp on the rounds census reaches every gear reader. A pawn the comparator left allowed, in a tainted
    // parka a bill could replace, is owed no replacement: no bench demand and no gear bill is wanted for it.
    [Fact]
    public void AnAllowedPawnInATaintedParkaGetsNoReplacementBillDemandAndNoOrder()
    {
        var model = Pawn().Gear with { Options = [Garment("new-parka", GearSlot.Outer, "Shell", "Torso", 30, source: GearSource.Bill)] };
        var gear = ColonyModelFixtures.NoGear() with
        {
            Stored = [],
            Pawns = [new GearPawn { Pawn = new("pawn"), Loadout = "worker", LoadoutModel = new GearModel.Modeled(model), Candidates = [] }],
        };
        var facts = new RoundsFacts { Gear = Known(gear) };

        var standing = RimGovernor.Brain.BuildingRuntime.Rounder.WithGearChoices(facts, []).Gear;
        Assert.NotEmpty(GearRules.GearReplacementNeeds(standing));

        var allowed = RimGovernor.Brain.BuildingRuntime.Rounder.WithGearChoices(facts, [new GearChoice("pawn", true)]).Gear;
        Assert.True(Assert.Single(allowed.Value.Pawns).AllowTainted);
        Assert.Equal(Known(true), GearRules.ReviewGear(allowed).Recovered);
        Assert.Empty(GearRules.GearReplacementNeeds(allowed));
        var (wanted, known) = BillWantedRules.GearBillsWanted(allowed);
        Assert.True(known);
        Assert.Empty(wanted!);
    }

    // #3329: with Live temperature cards each filter offers the distinct insulation levels of the planner's predicted outfits
    // over physical options: the dead man's parka pawn, tainted disallowed, gets the worn floor (the thin parka is the
    // closest, 20 less) and the unfloored outfit (no parka, 30 less); allowing tainted predicts the worn outfit, so nothing.
    [Fact]
    public void LiveTemperatureCardsOfferTheDistinctFloorsOfEachFilter()
    {
        var result = Generate(Facts(), temperature: MechanicCardStatus.Verified);

        Assert.Equal(["gear/pawn/disallow-tainted/cold30-heat0", "gear/pawn/disallow-tainted/cold0-heat0"], result.Roots.Select(r => r.Id));
        Assert.All(result.Roots, r => Assert.Equal("gear/pawn", r.ExclusiveOutcome));
        Assert.Equal([new ForecastComfortShift("pawn", 30, 0)], result.Roots[1].ComfortShifts);
        Assert.Equal(new GearChoice("pawn", false, 30, 0), EffectSourceCandidates.Choice("pawn", result.Roots[0].Id));
        Assert.DoesNotContain(result.Findings, f => f.StartsWith("effect_source_site_unreached:", StringComparison.Ordinal));
    }

    // #3329 acceptance: Cold and Hot not Live give no warmth candidate, and the worn floor applies.
    [Fact]
    public void WithoutLiveTemperatureCardsThereIsNoWarmthCandidateAndTheWornFloorApplies()
    {
        var warm = WarmPawn(tainted: false);
        Assert.NotEmpty(Generate(Facts(pawns: warm), temperature: MechanicCardStatus.Verified).Roots);
        Assert.Empty(Generate(Facts(pawns: warm), temperature: MechanicCardStatus.Extracted).Roots);
        Assert.Equal("gear/pawn/disallow-tainted/cold30-heat0", Assert.Single(Generate(Facts(), temperature: MechanicCardStatus.Extracted).Roots).Id);
        Assert.DoesNotContain(GearLoadoutRules.PlanColonyGear(Census(warm).Pawns)[0].Target, o => o.ID == "parka");
    }

    // #3329 acceptance: with no candidate the floor is the worn insulation and tainted stays disallowed. The pawn in the dead
    // man's parka (30) takes the clean parka (30) over the armored thin one (10) the unfloored planner prefers; with no option
    // at the floor it takes the closest and records the finding.
    [Fact]
    public void WithNoCandidateTheWornInsulationFloorAppliesAndTaintedStaysDisallowed()
    {
        var thin = Garment("thin-parka", GearSlot.Outer, "Shell", "Torso", 10, source: GearSource.Stored) with { Sharp = .5 };
        var model = Pawn().Gear with { Options = [Garment("clean-parka", GearSlot.Outer, "Shell", "Torso", 30, source: GearSource.Stored), thin] };
        var pawn = new GearPawn { Pawn = new("pawn"), LoadoutModel = new GearModel.Modeled(model) };

        var floored = GearLoadoutRules.PlanColonyGear([pawn])[0];
        Assert.Contains(floored.Target, o => o.ID == "clean-parka");
        Assert.DoesNotContain(floored.Target, o => o.ID == "parka");
        Assert.True(Assert.Single(floored.Gaps).Floor);
        Assert.Contains(GearLoadoutRules.PlanGearLoadout(model, o => o.Tainted).Target, o => o.ID == "thin-parka");

        var closest = GearLoadoutRules.PlanColonyGear([pawn with { LoadoutModel = new GearModel.Modeled(model with { Options = [thin] }) }])[0];
        Assert.Contains(closest.Target, o => o.ID == "thin-parka");
    }

    // #3569: a swap that moves no card and no insulation is a candidate when the armor differs, and none when it does not.
    [Theory]
    [InlineData(0.1, 0.3, true)]
    [InlineData(0.3, 0.3, false)]
    public void AnArmorOnlySwapIsACandidateOnlyWhenTheArmorDiffers(double wornSharp, double plannedSharp, bool candidate)
    {
        var pawn = Pawn() with
        {
            Gear = Pawn().Gear with
            {
                Worn = [Garment("shirt", GearSlot.SkinTorso, "OnSkin", "Torso"), Garment("duster", GearSlot.Outer, "Shell", "Torso") with { Sharp = wornSharp }],
                Options = [Garment("devilstrand", GearSlot.Outer, "Shell", "Torso", source: GearSource.Stored) with { Sharp = plannedSharp }],
            },
        };

        var result = Generate(Facts(pawns: pawn));

        Assert.Equal(candidate, result.Roots.Count > 0);
        foreach (var root in result.Roots)
        {
            Assert.Empty(root.SituationalChanges);
            Assert.Empty(root.ComfortShifts);
            Assert.Contains(Assert.Single(root.OutfitChanges).Items, o => o.ID == "devilstrand");
        }
    }

    private static string Signature(EffectSourceResult result) => string.Join("\n",
        result.Roots.Select(r => r.Id + "|" + r.ExclusiveOutcome + "|" + string.Join(",", r.SituationalChanges) + "|" + string.Join(",", r.ComfortShifts))
            .Concat(result.Findings));
}
