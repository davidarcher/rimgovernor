namespace RimGovernor.Brain.Policy;

public static class ClothingRequirementsRules
{
    /// <summary>Coverage, condition and required-apparel relief; null when the garment would strip a covered core group.
    /// Warmth is not a relief: the candidate's comfort shift is priced by the forecast (#3330).</summary>
    internal static List<ForecastRequirementRelief>? ClothingRelief(GearPawn pawn, GearLoadoutInput input, GearOption option)
    {
        var result = new List<ForecastRequirementRelief>();
        var remaining = input.Worn.Where(w => !GearLoadoutRules.GearConflicts(w, option)).ToArray();
        var after = remaining.Append(option).ToArray();
        var prefix = "clothing/" + pawn.Pawn.Value + "/";
        if (!input.Nudist && GearRules.GearCoreGroups.Any(g => input.Worn.Any(w => w.Groups.Contains(g)) && !after.Any(w => w.Groups.Contains(g)))) return null;
        if (!input.Nudist)
            foreach (var group in GearRules.GearCoreGroups)
                if (!input.Worn.Any(w => w.Groups.Contains(group)) && after.Any(w => w.Groups.Contains(group)))
                    result.Add(new(prefix + "coverage/" + group, ProductionNeedPriority.Required, 1));
        foreach (var worn in input.Worn.Where(w => GearLoadoutRules.GearConflicts(w, option) && w.Condition <= GearRules.GearTatteredCondition))
            result.Add(new(prefix + "condition/" + worn.ID, ProductionNeedPriority.Maintenance, 1));
        if (pawn.Policy.TryGetValue(out var policy) && policy.Required.Contains(option.Definition.Value)
            && !input.Worn.Any(w => w.Definition == option.Definition))
            result.Add(new(prefix + "required/" + option.Definition.Value, ProductionNeedPriority.Required, 1));
        return result;
    }
}
