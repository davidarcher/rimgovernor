namespace RimGovernor.Brain.Policy.Methods.Generation;

// The effect-source generator (#3224, #3217 Gate 1), prevention branch (#3284, live since #3285): from a verified negative
// count card that is active for a pawn, to the existing action that makes its guard false, to one labor-free coupled
// candidate per pawn.
// - Match: the card is Live; MechanicCardGuards.CardStageOffset for the pawn's worn apparel is known and negative; every
//   site maps, in MechanicCardGuards.SiteActions, to a DisallowCounted row (the apparel filter disallows the counted items).
// - Candidate: the pawn's apparel filter disallows the items the card counts, whatever the current filter (decided
//   2026-10-16): the comparator selects it, or rejects it and the pawn's filter allows them (GearFilterChoices). A pawn has
//   at most one candidate per card and every candidate of a pawn shares one ExclusiveOutcome, because two changes to one
//   situational thought leave the pawn unassessed (#3282).
// - Consequences, relative to the worn outfit: the card's offset for the gear planner's predicted outfit minus the worn
//   one (ForecastSituationalChange), and the predicted insulation change as a comfort shift (ForecastComfortShift).
// The comparator decides whether the change matters now; nothing here values mood against warmth. No def is named here.

/// <summary>One pawn's facts: its gear planner input and its pawn facts for the cards.</summary>
internal sealed record EffectSourcePawn(string Pawn, GearLoadoutInput Gear, CardPawnFacts Card);

/// <summary>The colony facts the generator reads.</summary>
internal sealed record EffectSourceFacts
{
    public IReadOnlyList<EffectSourcePawn> Pawns { get; init; } = [];

    /// <summary>The candidate budget: candidates emitted before the search reports truncation.</summary>
    public int Budget { get; init; } = 32;
}

/// <summary>What the pawn wears now, or its apparel filter disallowing the items a card counts.</summary>
internal enum EffectSourceVariant
{
    Worn,
    Disallow,
}

/// <summary>An outfit (worn, or the gear planner's prediction under a variant), the card's offset for it, and its summed quality-scaled insulation minus the worn outfit's.</summary>
internal sealed record EffectSourceOutfit(EquatableArray<GearOption> Items, Fact<double> Offset, double ColdDelta, double HeatDelta);

/// <summary>The labor-free root commitments, the findings and whether the budget truncated the search.</summary>
internal sealed record EffectSourceResult(EquatableArray<ForecastCommitment> Roots, EquatableArray<string> Findings, bool Truncated);

internal static class EffectSourceCandidates
{
    /// <summary>The ExclusiveOutcome prefix of a pawn's apparel-filter candidates: <c>gear-filter/&lt;pawn&gt;</c>.</summary>
    internal const string FilterOutcome = "gear-filter/";

    /// <summary>
    /// The search over precomputed facts (the Methods stage calls no policy function): <paramref name="outfits"/> holds the
    /// worn outfit and the gear planner's predicted outfit under the disallow variant, with the card's offset for each,
    /// keyed by (card, pawn, variant). PolicyFunctions.GenerateEffectSources computes it.
    /// </summary>
    internal static EffectSourceResult Generate(EffectSourceFacts facts, IEnumerable<MechanicCard> cards,
        IReadOnlyDictionary<(string Card, string Pawn, EffectSourceVariant Variant), EffectSourceOutfit> outfits)
    {
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var usable = new List<MechanicCard>();
        foreach (var card in cards.OrderBy(c => c.Def, StringComparer.Ordinal))
        {
            if (!card.Live)
            {
                findings.Add((card.Status == MechanicCardStatus.Unreachable ? "effect_source_card_unreachable:" : "effect_source_card_unverified:") + card.Def);
            }
            else if (Prevention(card, findings))
            {
                usable.Add(card);
            }
        }
        var roots = new List<ForecastCommitment>();
        var truncated = false;
        var budget = facts.Budget;
        foreach (var pawn in facts.Pawns.OrderBy(p => p.Pawn, StringComparer.Ordinal))
        foreach (var card in usable)
        {
            var key = card.Def + "/" + pawn.Pawn;
            if (!outfits.TryGetValue((card.Def, pawn.Pawn, EffectSourceVariant.Worn), out var worn) || !worn.Offset.TryGetValue(out var now))
            {
                findings.Add("effect_source_offset_unknown:" + key);
                continue;
            }
            if (now >= 0)
            {
                continue;
            }
            if (!outfits.TryGetValue((card.Def, pawn.Pawn, EffectSourceVariant.Disallow), out var outfit) || !outfit.Offset.TryGetValue(out var then))
            {
                findings.Add("effect_source_variant_unknown:" + key);
                continue;
            }
            if (budget-- == 0)
            {
                truncated = true;
                findings.Add("effect_source_generation_truncated");
                goto done;
            }
            roots.Add(new(FilterOutcome + pawn.Pawn + "/disallow-" + card.Def, [])
            {
                ExclusiveOutcome = FilterOutcome + pawn.Pawn,
                SituationalChanges = [new(pawn.Pawn, card.Def, then - now)],
                // More cold insulation lowers the comfortable minimum; more heat insulation raises the maximum.
                ComfortShifts = [new(pawn.Pawn, -outfit.ColdDelta, outfit.HeatDelta)],
            });
        }
    done:
        return new(EquatableArray.From(roots), EquatableArray.From(findings), truncated);
    }

    /// <summary>
    /// Whether a card has a prevention row: every site maps to a DisallowCounted row, and every count guard counts one
    /// bound decl (the native filter is per decl). False, with a finding, otherwise.
    /// </summary>
    internal static bool Prevention(MechanicCard card, ISet<string> findings)
    {
        foreach (var site in card.Sites)
        {
            if (!MechanicCardGuards.SiteActions.Any(a => a.Site == site.Site && a.Constraint == MechanicCardGuards.DisallowCounted))
            {
                findings.Add("effect_source_site_unreached:" + site.Site);
                return false;
            }
        }
        var wheres = card.Sites.SelectMany(s => s.Guards).Select(g => g.Count?.Where).Distinct().ToArray();
        if (card.Sites.Count == 0 || wheres is not [{ Op: null, Decl: { Length: > 0 } }])
        {
            findings.Add("effect_source_counted_unknown:" + card.Def);
            return false;
        }
        return true;
    }

    /// <summary>The apparel among gear options (a count over worn apparel excludes the primary weapon).</summary>
    internal static GearOption[] Apparel(IEnumerable<GearOption> items) => [.. items.Where(o => o.Slot != GearSlot.Primary)];
}
