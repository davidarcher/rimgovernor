using RimGovernor.Brain.Policy.Methods.Generation;

namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>
    /// The effect-source family's entry (#3224, prevention branch #3284, live since #3285): for each verified prevention card
    /// and each pawn, the card's offset for the worn apparel and, when it is known and negative, the gear planner's predicted
    /// outfit with the counted items disallowed, its offset and its insulation change from the worn outfit; then the pure
    /// search over them. <paramref name="cards"/> defaults to the shipped cards.
    /// </summary>
    internal static EffectSourceResult GenerateEffectSources(EffectSourceFacts facts, IReadOnlyDictionary<string, MechanicCard>? cards = null)
    {
        var all = (cards ?? MechanicCards.ByDef).Values.ToArray();
        var outfits = new Dictionary<(string Card, string Pawn, EffectSourceVariant Variant), EffectSourceOutfit>();
        foreach (var card in all.Where(c => c.Live && EffectSourceCandidates.Prevention(c, new HashSet<string>())))
        foreach (var pawn in facts.Pawns)
        {
            var now = MechanicCardGuards.CardStageOffset(card, pawn.Card, EffectSourceCandidates.Apparel(pawn.Gear.Worn));
            outfits[(card.Def, pawn.Pawn, EffectSourceVariant.Worn)] = new(pawn.Gear.Worn, now, 0, 0);
            if (!now.TryGetValue(out var offset) || offset >= 0)
            {
                continue;
            }
            var counted = pawn.Gear.Worn.Concat(pawn.Gear.Options).ToDictionary(o => o.ID, o => MechanicCardGuards.Counts(card, o), StringComparer.Ordinal);
            if (counted.Values.Any(c => !c.IsKnown))
            {
                continue;
            }
            var (wornCold, wornHeat) = GearInsulation(pawn.Gear.Worn);
            var target = PlanGearLoadout(pawn.Gear, o => counted[o.ID].Value).Target;
            var (cold, heat) = GearInsulation(target);
            outfits[(card.Def, pawn.Pawn, EffectSourceVariant.Disallow)] = new(target,
                MechanicCardGuards.CardStageOffset(card, pawn.Card, EffectSourceCandidates.Apparel(target)), cold - wornCold, heat - wornHeat);
        }
        return EffectSourceCandidates.Generate(facts, all, outfits);
    }

    /// <summary>
    /// The effect-source facts of the gear census (#3285): every unblocked pawn with a loadout model, under its climate, with
    /// its observed trait names and its hediff names from the medical read. Hediffs are unknown when the medical read, the
    /// pawn's row or a condition's def is; the card's offset is then unknown and the generator records a finding.
    /// </summary>
    internal static EffectSourceFacts EffectSourceFactsOf(GearObservation gear, Fact<EquatableArray<CarePawn>> medical, Fact<bool> ideologyActive = default)
    {
        var pawns = new List<EffectSourcePawn>();
        foreach (var pawn in gear.Pawns)
        {
            if (pawn.Blocked || !pawn.LoadoutModel.TryGetValue(out var input))
            {
                continue;
            }
            pawns.Add(new(pawn.Pawn.Value, input with { Climate = pawn.Climate ?? input.Climate }, CardPawnFactsOf(pawn.Pawn, input, medical, ideologyActive)));
        }
        return new() { Pawns = pawns };
    }

    /// <summary>
    /// A pawn's card facts from its loadout model's work row and the medical read: trait names, hediff names (unknown when
    /// the medical read, the pawn's row or a condition's def is), whether Ideology is active and its ideo's precepts
    /// (Known(empty) when its policy inputs name no ideo, #3327).
    /// </summary>
    internal static CardPawnFacts CardPawnFactsOf(PawnId pawn, GearLoadoutInput input, Fact<EquatableArray<CarePawn>> medical, Fact<bool> ideologyActive)
    {
        var hediffs = Unknown<EquatableArray<string>>();
        if (medical.TryGetValue(out var rows) && rows.FirstOrDefault(r => r.Id == pawn) is { } care
            && care.Conditions.TryGetValue(out var conditions) && conditions.All(c => c.DefName.IsKnown))
        {
            hediffs = Known(EquatableArray.From(conditions.Select(c => c.DefName.Value).Distinct().Order(StringComparer.Ordinal)));
        }
        var traits = input.Role.Work.Traits.TryGetValue(out var observed)
            ? Known(EquatableArray.From(observed.Select(t => t.Name))) : Unknown<EquatableArray<string>>();
        var precepts = input.Role.Work.PolicyInputs.TryGetValue(out var policy)
            ? Known<EquatableArray<string>>(policy.Ideo.Length == 0 ? [] : EquatableArray.From(policy.Precepts)) : Unknown<EquatableArray<string>>();
        return new CardPawnFacts { Traits = traits, Hediffs = hediffs, AnomalyActive = Unknown<bool>(), IdeologyActive = ideologyActive, Precepts = precepts };
    }

    /// <summary>
    /// The pawns whose outfit filter allows the items a card counts (#3285, decided 2026-10-16): the review compared the
    /// pawn's disallow candidate (an assessed portfolio holding it was rejected) and did not select it. A selected candidate,
    /// one never assessed (truncated, infeasible or unassessed) and a pawn without a candidate keep the default: disallowed.
    /// </summary>
    public static EquatableArray<string> GearFilterChoices(ForecastCoupledCandidates? candidates, ForecastCoupledSelection? selection)
    {
        if (candidates is null || selection is null)
        {
            return [];
        }
        var assessed = selection.Rejected.Where(r => r.Consequences is not null).SelectMany(r => r.Commitments).ToHashSet(StringComparer.Ordinal);
        return EquatableArray.From(candidates.Alternatives
            .Where(c => c.ExclusiveOutcome.StartsWith(EffectSourceCandidates.FilterOutcome, StringComparison.Ordinal)
                && c.SituationalChanges.Count > 0 && assessed.Contains(c.Id) && !selection.Selection.Selected.Contains(c.Id))
            .Select(c => c.SituationalChanges[0].Pawn).Distinct().Order(StringComparer.Ordinal));
    }
}
