using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>A wearer's garment to craft: the bill option (product, stuff, layers, groups and armor) and what it relieves.</summary>
public sealed record ClothingActivityRequirement(string Wearer, GearOption Garment, ForecastRequirementAssessment Assessment)
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
        if (!census.Stored.IsKnown) return [];
        var result = new List<ClothingActivityRequirement>();
        foreach (var pawn in census.Pawns.OrderBy(p => p.Pawn.Value, StringComparer.Ordinal))
        {
            if (pawn.Blocked) continue;
            if (!pawn.LoadoutModel.TryGetValue(out var input)) continue;
            foreach (var trait in input.Role.Work.Traits.ValueOrDefault)
                input = input with { Nudist = input.Nudist || trait.Effects.Nudist };
            foreach (var option in input.Options.Where(o => o.Source == GearSource.Bill && !GearArmorRules.ArmoryArmor(o.Definition)
                && GearLoadoutRules.GearEligible(input, o)))
            {
                var effects = ClothingRequirementsRules.ClothingRelief(pawn, input, option);
                if (effects is null) continue;
                // Warmth is a comfort shift from the craft's completion, priced by the forecast's temperature cards as break
                // risk (#3330); armor is the outfit change the harm term prices from the delivery (#3621). A garment changing
                // neither and relieving nothing is not a candidate.
                var (cold, heat) = GearLoadoutRules.GearInsulation([option]);
                var replaced = input.Worn.Where(w => GearLoadoutRules.GearConflicts(w, option)).ToArray();
                var (wornCold, wornHeat) = GearLoadoutRules.GearInsulation(replaced);
                var comfort = new ForecastComfortShift(pawn.Pawn.Value, wornCold - cold, heat - wornHeat);
                if (effects.Count == 0 && comfort.MinDelta >= 0 && comfort.MaxDelta <= 0
                    && !Methods.Generation.EffectSourceCandidates.ArmorDiffers(replaced, [option])) continue;
                result.Add(new(pawn.Pawn.Value, option, new(EquatableArray.From(effects), Known(0.0), Known(0.0)))
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
