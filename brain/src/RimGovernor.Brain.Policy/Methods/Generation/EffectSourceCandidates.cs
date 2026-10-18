using System.Globalization;

namespace RimGovernor.Brain.Policy.Methods.Generation;

// The effect-source generator (#3224, #3217 Gate 1), prevention branch (#3284, live since #3285), gear warmth (#3329): per
// pawn, labor-free coupled candidates that change how the pawn dresses, priced by verified cards.
// - Candidate: a (tainted filter, insulation floor) pair, under one exclusive outcome per pawn, gear/<pawn>, because two
//   changes to one pawn's outfit cannot both hold (#3282). The comparator selects one pair, or none and the pawn keeps its
//   worn outfit (GearChoices).
// - Prevention: a verified negative count card's offset changes when the filter disallows the items it counts; every site
//   maps, in MechanicCardGuards.SiteActions, to a DisallowCounted row.
// - Consequences, relative to the worn outfit: each prevention card's offset for the gear planner's predicted outfit minus
//   the worn one (ForecastSituationalChange), and the predicted insulation change as a comfort shift (ForecastComfortShift),
//   which Live temperature cards price along the forecast's exposure (#3327).
// - Gating: a pair is offered only when a Live card prices it: a prevention card's offset changes, or a Live stage card reads
//   the comfort bound the insulation change moves.
// - Armor: a pair whose outfit differs from the worn in armor is also a candidate when no card or insulation moves (#3569);
//   the value comparison (harm avoided against cost) decides whether it is chosen, not generation.
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

/// <summary>
/// An outfit under a tainted filter and an insulation floor: the gear planner's predicted items (or the worn ones), each
/// prevention card's offset for them, and their summed quality-scaled insulation.
/// </summary>
internal sealed record EffectSourceOutfit(bool AllowTainted, double ColdFloor, double HeatFloor, EquatableArray<GearOption> Items,
    IReadOnlyDictionary<string, Fact<double>> Offsets, double Cold, double Heat);

/// <summary>The labor-free root commitments, the findings and whether the budget truncated the search.</summary>
internal sealed record EffectSourceResult(EquatableArray<ForecastCommitment> Roots, EquatableArray<string> Findings, bool Truncated);

internal static class EffectSourceCandidates
{
    /// <summary>The ExclusiveOutcome prefix of a pawn's gear candidates: <c>gear/&lt;pawn&gt;</c>.</summary>
    internal const string GearOutcome = "gear/";

    private const string Allow = "allow-tainted", Disallow = "disallow-tainted";

    /// <summary>
    /// The search over precomputed facts (the Methods stage calls no planner): <paramref name="outfits"/> holds, per pawn, its
    /// worn outfit first and then the gear planner's predicted outfit for each (filter, floor) pair, with each prevention
    /// card's offset. EffectSourcesRules.GenerateEffectSources computes it, and whether a Live temperature card prices a shift of
    /// the comfortable minimum (<paramref name="cold"/>) or maximum (<paramref name="heat"/>).
    /// </summary>
    internal static EffectSourceResult Generate(EffectSourceFacts facts, IEnumerable<MechanicCard> cards,
        IReadOnlyDictionary<string, IReadOnlyList<EffectSourceOutfit>> outfits, bool cold = false, bool heat = false)
    {
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var usable = new List<MechanicCard>();
        foreach (var card in cards.OrderBy(c => c.Def, StringComparer.Ordinal))
        {
            if (!card.Live)
            {
                findings.Add((card.Status == MechanicCardStatus.Unreachable ? "effect_source_card_unreachable:" : "effect_source_card_unverified:") + card.Def);
            }
            // A stage card prices comfort shifts in the forecast (#3327); it has no prevention row.
            else if (!(card.Sites.Count > 0 && card.Sites.All(s => s.Stage is not null)) && Prevention(card, findings))
            {
                usable.Add(card);
            }
        }
        var roots = new List<ForecastCommitment>();
        var truncated = false;
        var budget = facts.Budget;
        foreach (var pawn in facts.Pawns.OrderBy(p => p.Pawn, StringComparer.Ordinal))
        {
            if (!outfits.TryGetValue(pawn.Pawn, out var options) || options.Count == 0)
            {
                continue;
            }
            var worn = options[0];
            var unknown = usable.Where(c => !worn.Offsets.GetValueOrDefault(c.Def).IsKnown).ToArray();
            foreach (var card in unknown)
            {
                findings.Add("effect_source_offset_unknown:" + card.Def + "/" + pawn.Pawn);
            }
            if (unknown.Length > 0)
            {
                continue;
            }
            foreach (var outfit in options.Skip(1))
            {
                var id = GearOutcome + pawn.Pawn + "/" + (outfit.AllowTainted ? Allow : Disallow) + "/" + FloorId(outfit.ColdFloor, outfit.HeatFloor);
                var changes = new List<ForecastSituationalChange>();
                foreach (var card in usable)
                {
                    if (!outfit.Offsets.GetValueOrDefault(card.Def).TryGetValue(out var then))
                    {
                        findings.Add("effect_source_variant_unknown:" + id);
                        goto next;
                    }
                    if (then != worn.Offsets[card.Def].Value)
                    {
                        changes.Add(new(pawn.Pawn, card.Def, then - worn.Offsets[card.Def].Value));
                    }
                }
                var (dc, dh) = (Math.Round(outfit.Cold - worn.Cold, 6), Math.Round(outfit.Heat - worn.Heat, 6));
                if (changes.Count == 0 && !(cold && dc != 0) && !(heat && dh != 0) && !ArmorDiffers(worn.Items, outfit.Items))
                {
                    continue;
                }
                if (budget-- == 0)
                {
                    truncated = true;
                    findings.Add("effect_source_generation_truncated");
                    goto done;
                }
                roots.Add(new(id, [])
                {
                    ExclusiveOutcome = GearOutcome + pawn.Pawn,
                    SituationalChanges = EquatableArray.From(changes),
                    // More cold insulation lowers the comfortable minimum; more heat insulation raises the maximum.
                    ComfortShifts = dc == 0 && dh == 0 ? [] : [new(pawn.Pawn, -dc, dh)],
                    OutfitChanges = [new(pawn.Pawn, outfit.Items)],
                });
            next:;
            }
        }
    done:
        return new(EquatableArray.From(roots), EquatableArray.From(findings), truncated);
    }

    /// <summary>
    /// Whether the planned apparel differs from the worn in armor: the armor response reads each garment's body groups, layers,
    /// sharp, blunt and heat ratings, quality and condition (<see cref="GearArmorTerm"/>), so a different multiset of those is
    /// an armor change. Generation is structural; the value comparison prices it against its cost (#3569).
    /// </summary>
    internal static bool ArmorDiffers(IEnumerable<GearOption> worn, IEnumerable<GearOption> planned) =>
        !ArmorKey(worn).SequenceEqual(ArmorKey(planned), StringComparer.Ordinal);

    private static IEnumerable<string> ArmorKey(IEnumerable<GearOption> items) =>
        Apparel(items).Where(o => o.Sharp > 0 || o.Blunt > 0 || o.ArmorHeat > 0).Select(o => string.Join('|', string.Join(',', o.Groups),
            string.Join(',', o.Layers), o.Sharp.ToString("R", CultureInfo.InvariantCulture), o.Blunt.ToString("R", CultureInfo.InvariantCulture),
            o.ArmorHeat.ToString("R", CultureInfo.InvariantCulture), o.Quality, o.Condition.ToString("R", CultureInfo.InvariantCulture))).Order(StringComparer.Ordinal);

    /// <summary>The floor segment of a candidate id, <c>cold&lt;c&gt;-heat&lt;h&gt;</c>, rounded to 6 decimals; <see cref="Choice"/> reads it back.</summary>
    internal static string FloorId(double cold, double heat) =>
        "cold" + Math.Round(cold, 6).ToString("0.######", CultureInfo.InvariantCulture) + "-heat" + Math.Round(heat, 6).ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>The (filter, floor) pair a selected candidate of <paramref name="pawn"/> stands for.</summary>
    internal static GearChoice Choice(string pawn, string id)
    {
        var parts = id[(GearOutcome.Length + pawn.Length + 1)..].Split('/');
        var floor = parts[1]["cold".Length..].Split("-heat");
        return new(pawn, parts[0] == Allow, double.Parse(floor[0], CultureInfo.InvariantCulture), double.Parse(floor[1], CultureInfo.InvariantCulture));
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
