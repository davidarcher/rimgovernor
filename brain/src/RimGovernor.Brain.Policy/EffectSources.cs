using RimGovernor.Brain.Policy.Methods.Generation;

namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>
    /// The effect-source family's entry (#3224): computes the mood shortfall and every card site's guard evaluation the
    /// search can ask for (each unburied eligible target, against a fresh container and each occupant of a slot of the
    /// site's class), then runs the pure search over them. <paramref name="cards"/> defaults to the shipped cards.
    /// </summary>
    internal static EffectSourceResult GenerateEffectSources(EffectSourceFacts facts, IReadOnlyDictionary<string, MechanicCard>? cards = null)
    {
        var shortfall = facts.Needs.Pawns.Where(p => ForecastMoodTimeline(p, facts.Horizon, []) is var r
                && (r.BreakRisk.TryGetValue(out var risk) && risk > 0 || r.TargetExposureTicks.TryGetValue(out var exposure) && exposure > 0))
            .Select(p => p.Pawn).ToHashSet(StringComparer.Ordinal);
        var all = (cards ?? MechanicCards.ByDef).Values.ToArray();
        var evaluations = new Dictionary<(string Def, string Site, string Target, string Instance), CardEvaluation>();
        foreach (var card in all.Where(c => c.Live && shortfall.Count > 0))
        foreach (var site in card.Sites)
        {
            var owner = site.Site[..Math.Max(site.Site.LastIndexOf('.'), 0)];
            var instances = facts.Slots.Where(s => s.Occupant.Length > 0 && (s.Class == owner || s.Class.EndsWith("." + owner, StringComparison.Ordinal)))
                .Select(s => s.Occupant).Prepend("").Distinct(StringComparer.Ordinal).ToArray();
            foreach (var target in facts.Waste.Where(w => w.State != WasteState.Buried && w.Eligible))
            foreach (var instance in instances)
                evaluations[(card.Def, site.Site, target.Id, instance)] = MechanicCardGuards.Evaluate(card, site,
                    new CardGuardFacts { Target = target, Instance = instance, Waste = facts.Waste, Colonists = facts.Colonists });
        }
        return EffectSourceCandidates.Generate(facts, all, shortfall, evaluations);
    }
}
