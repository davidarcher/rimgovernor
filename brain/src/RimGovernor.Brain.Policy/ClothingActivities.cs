using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>A wearer's garment to craft: the bill option (product, stuff, layers, groups and armor).</summary>
public sealed record ClothingActivityRequirement(string Wearer, GearOption Garment)
{
    public string Product => Garment.Definition.Value;
    public string Stuff => Garment.Stuff.Value;

    /// <summary>The crafted garment's insulation, quality scaled, less that of the worn items it replaces (#3330).</summary>
    public ForecastComfortShift? Comfort { get; init; }

    /// <summary>The garment's <c>GearItemScore</c> for the wearer's role (#3621): generation offers the highest first.</summary>
    public double Score { get; init; }
}

public static class ClothingActivitiesRules
{
    /// <summary>
    /// The loadout model owns needs; production retains every eligible shape and every stuff <see cref="StuffArrival"/> admits
    /// until comparison, which weighs crafting against swaps to physical items (#3621). Highest <c>GearItemScore</c> first.
    /// </summary>
    public static EquatableArray<ClothingActivityRequirement> ClothingActivityRequirements(GearObservation census)
    {
        var result = new List<ClothingActivityRequirement>();
        foreach (var pawn in census.Pawns.OrderBy(p => p.Pawn.Value, StringComparer.Ordinal))
        {
            if (pawn.Blocked) continue;
            if (pawn.Model is not { } input) continue;
            foreach (var trait in input.Role.Traits)
                input = input with { Nudist = input.Nudist || trait.Effects.Nudist };
            var required = pawn.Policy is { } policy ? policy.Required : [];
            foreach (var option in input.Options.Where(o => o.Source == GearSource.Bill && !GearArmorRules.ArmoryArmor(o.Definition)
                && GearLoadoutRules.GearEligible(input, o)))
            {
                // Warmth is a comfort shift from the craft's completion, priced by the forecast's temperature cards as break
                // risk (#3330); armor is the outfit change the harm term prices from the delivery (#3621); the apparel thoughts it
                // changes are its investment credit (#3816). A garment the policy requires and the wearer lacks (its title's or
                // role's, whose unmet-requirement thought it lifts, #3836, or a worn-count goal's, stamped into the policy) is a
                // candidate too, so the requirement or goal can be met. Any other is not.
                var (cold, heat) = GearLoadoutRules.GearInsulation([option]);
                var replaced = input.Worn.Where(w => GearLoadoutRules.GearConflicts(w, option)).ToArray();
                var (wornCold, wornHeat) = GearLoadoutRules.GearInsulation(replaced);
                var comfort = new ForecastComfortShift(pawn.Pawn.Value, wornCold - cold, heat - wornHeat);
                // A garment that changes what the apparel workers read is kept even when it alone changes no thought: with
                // another (a female pawn's shirt and pants) it may.
                var thoughts = ForecastApparel.ChangesThoughts(input.Worn, [option]);
                var wanted = required.Contains(option.Definition.Value) && !input.Worn.Any(w => w.Definition == option.Definition);
                if (!thoughts && !wanted && comfort.MinDelta >= 0 && comfort.MaxDelta <= 0
                    && !Methods.Generation.EffectSourceCandidates.ArmorDiffers(replaced, [option])) continue;
                result.Add(new(pawn.Pawn.Value, option)
                { Comfort = comfort.MinDelta == 0 && comfort.MaxDelta == 0 ? null : comfort, Score = GearLoadoutRules.GearItemScore(input, option) });
            }
        }
        return EquatableArray.From(result.Distinct().OrderByDescending(r => r.Score).ThenBy(r => r.Wearer, StringComparer.Ordinal)
            .ThenBy(r => r.Product, StringComparer.Ordinal).ThenBy(r => r.Stuff, StringComparer.Ordinal));
    }

    /// <summary>
    /// The effect-source swaps that change <paramref name="need"/>'s slot for its wearer (#3621): the swap's outfit holds other
    /// items conflicting with the garment than <paramref name="worn"/> does. A crafted garment overlays the outfit then worn, so a
    /// swap of the wearer's other slots composes with it, and one of the same slot cannot (both would dress one body slot).
    /// Every swap of the wearer when its worn outfit is unobserved.
    /// </summary>
    internal static IEnumerable<string> SwapsInSlot(ClothingActivityRequirement need, EquatableArray<GearOption>? worn, IEnumerable<ForecastCommitment> swaps)
    {
        string Slot(IEnumerable<GearOption> items) => string.Join('|', items.Where(i => GearLoadoutRules.GearConflicts(i, need.Garment))
            .Select(i => i.ID).Order(StringComparer.Ordinal));
        var wornSlot = worn is { } items ? Slot(items) : null;
        foreach (var swap in swaps)
            foreach (var change in swap.OutfitChanges)
                if (change.Pawn == need.Wearer && (wornSlot is null || Slot(change.Items) != wornSlot))
                {
                    yield return swap.Id;
                    break;
                }
    }

}
