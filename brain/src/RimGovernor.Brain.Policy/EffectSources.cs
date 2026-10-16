using RimGovernor.Brain.Policy.Methods.Generation;

namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>
    /// The effect-source family's entry (#3224, prevention branch #3284): for each verified prevention card and each pawn,
    /// the card's offset for the worn apparel and, when it is known and negative, the gear planner's predicted outfit under
    /// both filter variants (the counted items allowed, and disallowed) with its offset and its insulation change from the
    /// worn outfit; then the pure search over them. <paramref name="cards"/> defaults to the shipped cards.
    /// </summary>
    internal static EffectSourceResult GenerateEffectSources(EffectSourceFacts facts, IReadOnlyDictionary<string, MechanicCard>? cards = null)
    {
        var all = (cards ?? MechanicCards.ByDef).Values.ToArray();
        var outfits = new Dictionary<(string Card, string Pawn, EffectSourceVariant Variant), EffectSourceOutfit>();
        foreach (var card in all.Where(c => c.Live && EffectSourceCandidates.Prevention(c, new HashSet<string>()) is not null))
        foreach (var pawn in facts.Pawns)
        {
            var now = MechanicCardGuards.CardStageOffset(card, pawn.Card, EffectSourceCandidates.Apparel(pawn.Gear.Worn));
            outfits[(card.Def, pawn.Pawn, EffectSourceVariant.Worn)] = new(pawn.Gear.Worn, now, 0, 0);
            if (!now.TryGetValue(out var offset) || offset >= 0)
            {
                continue;
            }
            var counted = pawn.Gear.Worn.Concat(pawn.Gear.Options).ToDictionary(o => o.ID, o => MechanicCardGuards.Counts(card, o), StringComparer.Ordinal);
            var (wornCold, wornHeat) = GearInsulation(pawn.Gear.Worn);
            foreach (var variant in new[] { EffectSourceVariant.Allow, EffectSourceVariant.Disallow })
            {
                var disallow = variant == EffectSourceVariant.Disallow;
                if (disallow && counted.Values.Any(c => !c.IsKnown))
                {
                    continue;
                }
                var target = PlanGearLoadout(pawn.Gear, o => disallow && counted[o.ID].Value).Target;
                var (cold, heat) = GearInsulation(target);
                outfits[(card.Def, pawn.Pawn, variant)] = new(target,
                    MechanicCardGuards.CardStageOffset(card, pawn.Card, EffectSourceCandidates.Apparel(target)), cold - wornCold, heat - wornHeat);
            }
        }
        return EffectSourceCandidates.Generate(facts, all, outfits);
    }
}
