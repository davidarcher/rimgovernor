namespace RimGovernor.Brain.Policy.Methods.Generation;

// The effect-source generator (#3224, #3217 Gate 1), prevention branch (#3284): from a verified negative count card that
// is active for a pawn, to the existing action that makes its guard false, to one labor-free coupled candidate per pawn.
// - Match: the card is Live; MechanicCardGuards.CardStageOffset for the pawn's worn apparel is known and negative; every
//   site maps, in MechanicCardGuards.SiteActions, to a DisallowCounted row (the apparel filter disallows the counted items).
// - Variants: the pawn's apparel filter allows or disallows the items the card counts. The current one is read from the
//   pawn's filter; the other is the candidate. A pawn has at most one candidate per card and every candidate of a pawn
//   shares one ExclusiveOutcome, because two changes to one situational thought leave the pawn unassessed (#3282).
// - Consequences, relative to the worn outfit: the card's offset for the gear planner's predicted outfit minus the worn
//   one (ForecastSituationalChange), and the predicted insulation change as a comfort shift (ForecastComfortShift).
// The comparator decides whether the change matters now; nothing here values mood against warmth. No def is named here.

/// <summary>One pawn's facts: its gear planner input, its pawn facts for the cards and its current apparel filter.</summary>
internal sealed record EffectSourcePawn(string Pawn, GearLoadoutInput Gear, CardPawnFacts Card)
{
    /// <summary>The item decls the pawn's current apparel filter disallows (a native special filter that is off); unknown when unread.</summary>
    public Fact<EquatableArray<string>> Disallowed { get; init; }
}

/// <summary>The colony facts the generator reads.</summary>
internal sealed record EffectSourceFacts
{
    public IReadOnlyList<EffectSourcePawn> Pawns { get; init; } = [];

    /// <summary>The candidate budget: candidates emitted before the search reports truncation.</summary>
    public int Budget { get; init; } = 32;
}

/// <summary>What the pawn wears now, or its apparel filter allowing or disallowing the items a card counts.</summary>
internal enum EffectSourceVariant
{
    Worn,
    Allow,
    Disallow,
}

/// <summary>An outfit (worn, or the gear planner's prediction under a variant), the card's offset for it, and its summed quality-scaled insulation minus the worn outfit's.</summary>
internal sealed record EffectSourceOutfit(EquatableArray<GearOption> Items, Fact<double> Offset, double ColdDelta, double HeatDelta);

/// <summary>How a candidate executes: the existing action kind on the pawn, the card and whether its counted items are disallowed.</summary>
internal sealed record EffectSourceStep(string Candidate, ActionKind Action, string Pawn, string Card, bool Disallow);

/// <summary>The labor-free root commitments, each candidate's step, the findings and whether the budget truncated the search.</summary>
internal sealed record EffectSourceResult(EquatableArray<ForecastCommitment> Roots, EquatableArray<EffectSourceStep> Steps, EquatableArray<string> Findings, bool Truncated);

internal static class EffectSourceCandidates
{
    /// <summary>
    /// The search over precomputed facts (the Methods stage calls no policy function): <paramref name="outfits"/> holds the
    /// worn outfit and the gear planner's predicted outfits, with the card's offset for each, keyed by (card, pawn, variant).
    /// PolicyFunctions.GenerateEffectSources computes it.
    /// </summary>
    internal static EffectSourceResult Generate(EffectSourceFacts facts, IEnumerable<MechanicCard> cards,
        IReadOnlyDictionary<(string Card, string Pawn, EffectSourceVariant Variant), EffectSourceOutfit> outfits)
    {
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var usable = new List<(MechanicCard Card, CardSiteAction Action, string Where)>();
        foreach (var card in cards.OrderBy(c => c.Def, StringComparer.Ordinal))
        {
            if (!card.Live)
            {
                findings.Add((card.Status == MechanicCardStatus.Unreachable ? "effect_source_card_unreachable:" : "effect_source_card_unverified:") + card.Def);
            }
            else if (Prevention(card, findings) is { } prevention)
            {
                usable.Add((card, prevention.Action, prevention.Where));
            }
        }
        var roots = new List<ForecastCommitment>();
        var steps = new List<EffectSourceStep>();
        var truncated = false;
        var budget = facts.Budget;
        foreach (var pawn in facts.Pawns.OrderBy(p => p.Pawn, StringComparer.Ordinal))
        foreach (var (card, action, where) in usable)
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
            if (!pawn.Disallowed.TryGetValue(out var disallowed))
            {
                findings.Add("effect_source_filter_unknown:" + key);
                continue;
            }
            var disallow = !disallowed.Contains(where);
            if (!outfits.TryGetValue((card.Def, pawn.Pawn, disallow ? EffectSourceVariant.Disallow : EffectSourceVariant.Allow), out var outfit)
                || !outfit.Offset.TryGetValue(out var then))
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
            var id = "gear-filter/" + pawn.Pawn + "/" + (disallow ? "disallow-" : "allow-") + card.Def;
            roots.Add(new(id, [])
            {
                ExclusiveOutcome = "gear-filter/" + pawn.Pawn,
                SituationalChanges = [new(pawn.Pawn, card.Def, then - now)],
                // More cold insulation lowers the comfortable minimum; more heat insulation raises the maximum.
                ComfortShifts = [new(pawn.Pawn, -outfit.ColdDelta, outfit.HeatDelta)],
            });
            steps.Add(new(id, action.Action, pawn.Pawn, card.Def, disallow));
        }
    done:
        return new(EquatableArray.From(roots), EquatableArray.From(steps), EquatableArray.From(findings), truncated);
    }

    /// <summary>
    /// A card's prevention row and the item decl it counts: every site maps to a DisallowCounted row, and every count guard
    /// counts one bound decl (the native filter is per decl). Null, with a finding, otherwise.
    /// </summary>
    internal static (CardSiteAction Action, string Where)? Prevention(MechanicCard card, ISet<string> findings)
    {
        CardSiteAction? action = null;
        foreach (var site in card.Sites)
        {
            if (MechanicCardGuards.SiteActions.FirstOrDefault(a => a.Site == site.Site && a.Constraint == MechanicCardGuards.DisallowCounted) is not { } row)
            {
                findings.Add("effect_source_site_unreached:" + site.Site);
                return null;
            }
            action = row;
        }
        var wheres = card.Sites.SelectMany(s => s.Guards).Select(g => g.Count?.Where).Distinct().ToArray();
        if (action is null || wheres is not [{ Op: null, Decl: { Length: > 0 } where }])
        {
            findings.Add("effect_source_counted_unknown:" + card.Def);
            return null;
        }
        return (action, where);
    }

    /// <summary>The apparel among gear options (a count over worn apparel excludes the primary weapon).</summary>
    internal static GearOption[] Apparel(IEnumerable<GearOption> items) => [.. items.Where(o => o.Slot != GearSlot.Primary)];
}
