using RimGovernor.Brain.Policy.Methods.Generation;

namespace RimGovernor.Brain.Policy;

public static class EffectSourcesRules
{
    /// <summary>
    /// The effect-source family's entry (#3224, prevention branch #3284, live since #3285, gear warmth #3329): for each pawn,
    /// the worn outfit and the gear planner's predicted outfit for each (tainted filter, insulation floor) pair, with each
    /// verified prevention card's offset; then the pure search over them. <paramref name="cards"/> defaults to the shipped cards.
    /// <list type="bullet">
    /// <item>Filters: tainted disallowed (the default), and allowed when a prevention card is Live and a physical option or
    /// worn item is tainted.</item>
    /// <item>Floors, per filter, over physical options only (worn, loose, stored; bill-sourced warmth is the clothing family's):
    /// the worn insulation always, and, when a temperature card is Live, the distinct insulation levels of the warmest, the
    /// coolest and the planner's unfloored outfits, capped at 3 with a truncation finding.</item>
    /// </list>
    /// </summary>
    internal static EffectSourceResult GenerateEffectSources(EffectSourceFacts facts, IReadOnlyDictionary<string, MechanicCard>? cards = null)
    {
        var all = (cards ?? MechanicCards.ByDef).Values.ToArray();
        var prevention = all.Where(c => c.Live && c.Sites.All(s => s.Stage is null) && EffectSourceCandidates.Prevention(c, new HashSet<string>())).ToArray();
        var warmth = all.Any(ForecastMood.TemperatureCard);
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var outfits = new Dictionary<string, IReadOnlyList<EffectSourceOutfit>>(StringComparer.Ordinal);
        foreach (var pawn in facts.Pawns)
        {
            EffectSourceOutfit Outfit(bool allow, (double Cold, double Heat) floor, IEnumerable<GearOption> items)
            {
                var array = EquatableArray.From(items);
                var (cold, heat) = GearLoadoutRules.GearInsulation(array);
                return new(allow, floor.Cold, floor.Heat, array,
                    prevention.ToDictionary(c => c.Def, c => MechanicCardGuards.CardStageOffset(c, pawn.Card, EffectSourceCandidates.Apparel(array))), cold, heat);
            }
            var worn = GearLoadoutRules.GearInsulation(pawn.Gear.Worn);
            var options = new List<EffectSourceOutfit> { Outfit(false, worn, pawn.Gear.Worn) };
            var physical = pawn.Gear with { Options = EquatableArray.From(pawn.Gear.Options.Where(o => o.Source != GearSource.Bill)) };
            var held = physical.Worn.Concat(physical.Options).ToArray();
            // A floor no outfit reaches: the search returns the most insulation the options allow.
            var most = (Cold: held.Sum(o => o.Cold) * 2 + 1, Heat: held.Sum(o => o.Heat) * 2 + 1);
            foreach (var allow in prevention.Length > 0 && held.Any(o => o.Tainted) ? [false, true] : new[] { false })
            {
                Func<GearOption, bool>? filter = allow ? null : o => o.Tainted;
                var planned = new List<EffectSourceOutfit> { Outfit(allow, worn, GearLoadoutRules.PlanGearLoadout(physical, filter, worn).Target) };
                foreach (var probe in warmth ? new (double, double)?[] { (most.Cold, 0), (0, most.Heat), null } : [])
                {
                    var target = GearLoadoutRules.PlanGearLoadout(physical, filter, probe).Target;
                    var (cold, heat) = GearLoadoutRules.GearInsulation(target);
                    if (planned.Any(o => Math.Round(o.Cold - cold, 6) == 0 && Math.Round(o.Heat - heat, 6) == 0))
                    {
                        continue;
                    }
                    if (planned.Count == 3)
                    {
                        findings.Add("effect_source_floors_truncated:" + pawn.Pawn);
                        break;
                    }
                    planned.Add(Outfit(allow, (cold, heat), target));
                }
                options.AddRange(planned);
            }
            outfits[pawn.Pawn] = options;
        }
        var result = EffectSourceCandidates.Generate(facts, all, outfits,
            all.Any(c => ForecastMood.TemperatureCardReads(c, "StatDefOf.ComfyTemperatureMin")), all.Any(c => ForecastMood.TemperatureCardReads(c, "StatDefOf.ComfyTemperatureMax")));
        return findings.Count == 0 ? result : result with { Findings = EquatableArray.From(result.Findings.Concat(findings).Distinct().Order(StringComparer.Ordinal)) };
    }

    /// <summary>
    /// The effect-source facts of the gear census (#3285): every unblocked pawn with a loadout model, with its observed trait
    /// names, its hediff names from the medical read, whether Ideology is active and the precepts of its ideo. A pawn the
    /// medical read holds no row for is listed in <see cref="EffectSourceFacts.Unread"/>: no card prices it, and the generator
    /// records a finding.
    /// </summary>
    internal static EffectSourceFacts EffectSourceFactsOf(GearObservation gear, EquatableArray<CarePawn> medical, bool ideologyActive = false)
    {
        var rows = medical;
        var pawns = new List<EffectSourcePawn>();
        var unread = new List<string>();
        foreach (var pawn in gear.Pawns)
        {
            if (pawn.Blocked || pawn.Model is not { } input)
            {
                continue;
            }
            if (rows.FirstOrDefault(r => r.Id == pawn.Pawn) is not { } care)
            {
                unread.Add(pawn.Pawn.Value);
                continue;
            }
            pawns.Add(new(pawn.Pawn.Value, input, CardPawnFactsOf(input, care, ideologyActive)));
        }
        return new() { Pawns = pawns, Unread = EquatableArray.From(unread) };
    }

    /// <summary>
    /// A pawn's card facts from its loadout model's work row and its medical row: trait names, hediff names, whether Ideology
    /// is active and its ideo's precepts (empty for a pawn without an ideo, #3327).
    /// </summary>
    internal static CardPawnFacts CardPawnFactsOf(GearLoadoutInput input, CarePawn care, bool ideologyActive) => new()
    {
        Traits = EquatableArray.From(input.Role.Traits.Select(t => t.Name)),
        Hediffs = EquatableArray.From(care.Conditions.Select(c => c.DefName).Distinct().Order(StringComparer.Ordinal)),
        IdeologyActive = ideologyActive,
        Precepts = input.Role.Precepts,
    };

    /// <summary>
    /// The comparator's per-pawn gear outcome (#3285, #3329, decided 2026-10-16), for the pawns that differ from the default
    /// (tainted disallowed, the worn insulation floor):
    /// <list type="bullet">
    /// <item>a selected gear/&lt;pawn&gt; candidate gives its (filter, floor) pair;</item>
    /// <item>otherwise, when the review compared removing items a card counts from the worn outfit (an assessed portfolio
    /// holding a candidate that raises a situational offset was rejected), the pawn keeps its worn outfit: tainted allowed,
    /// the worn floor.</item>
    /// </list>
    /// A candidate never assessed (truncated, infeasible or unassessed) and a pawn without a candidate keep the default.
    /// </summary>
    public static EquatableArray<GearChoice> GearChoices(ForecastCoupledCandidates? candidates, ForecastCoupledSelection? selection)
    {
        if (candidates is null || selection is null)
        {
            return [];
        }
        var assessed = selection.Rejected.Where(r => r.Assessed).SelectMany(r => r.Commitments).ToHashSet(StringComparer.Ordinal);
        var choices = new List<GearChoice>();
        foreach (var pawn in candidates.Alternatives.Where(c => c.ExclusiveOutcome.StartsWith(EffectSourceCandidates.GearOutcome, StringComparison.Ordinal))
            .GroupBy(c => c.ExclusiveOutcome[EffectSourceCandidates.GearOutcome.Length..], StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (pawn.FirstOrDefault(c => selection.Selection.Selected.Contains(c.Id)) is { } selected)
            {
                choices.Add(EffectSourceCandidates.Choice(pawn.Key, selected.Id));
            }
            else if (pawn.Any(c => assessed.Contains(c.Id) && c.SituationalChanges.Any(s => s.OffsetDelta > 0)))
            {
                choices.Add(GearChoice.KeepFloors(pawn.Key, true));
            }
        }
        return EquatableArray.From(choices);
    }
}

/// <summary>A pawn's gear outcome (#3329): whether its outfit allows tainted apparel, and its cold and heat insulation floors.</summary>
public sealed record GearChoice(string Pawn, bool AllowTainted, InsulationFloor ColdFloor, InsulationFloor HeatFloor)
{
    /// <summary>The outcome that leaves both floors at the pawn's worn insulation.</summary>
    public static GearChoice KeepFloors(string pawn, bool allowTainted) => new(pawn, allowTainted, new InsulationFloor.Worn(), new InsulationFloor.Worn());
}
