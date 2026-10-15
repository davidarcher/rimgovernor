namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    private static List<ForecastRequirementRelief>? ClothingRelief(GearPawn pawn, GearLoadoutInput input, GearOption option)
    {
        var result = new List<ForecastRequirementRelief>();
        var remaining = input.Worn.Where(w => !GearConflicts(w, option)).ToArray();
        var after = remaining.Append(option).ToArray();
        var prefix = "clothing/" + pawn.Pawn.Value + "/";
        if (!input.Nudist && GearCoreGroups.Any(g => input.Worn.Any(w => w.Groups.Contains(g)) && !after.Any(w => w.Groups.Contains(g)))) return null;
        if (!input.Nudist)
            foreach (var group in GearCoreGroups)
                if (!input.Worn.Any(w => w.Groups.Contains(group)) && after.Any(w => w.Groups.Contains(group)))
                    result.Add(new(prefix + "coverage/" + group, ProductionNeedPriority.Required, 1));
        foreach (var worn in input.Worn.Where(w => GearConflicts(w, option) && w.Condition <= GearTatteredCondition))
            result.Add(new(prefix + "condition/" + worn.ID, ProductionNeedPriority.Maintenance, 1));
        if (pawn.Policy.TryGetValue(out var policy) && policy.Required.Contains(option.Definition.Value)
            && !input.Worn.Any(w => w.Definition == option.Definition))
            result.Add(new(prefix + "required/" + option.Definition.Value, ProductionNeedPriority.Required, 1));
        var (low, high) = input.Climate.TemperatureRange(input.Ambient);
        double Insulation(IEnumerable<GearOption> items, bool cold) => items.Sum(o => (cold ? o.Cold : o.Heat) * GearQualityMultipliers(o.Quality).Insulation);
        foreach (var cold in new[] { true, false })
        {
            var needed = Math.Max(0, cold ? input.ComfortableMin - low : high - input.ComfortableMax);
            var before = Math.Max(0, needed - Insulation(input.Worn, cold));
            var later = Math.Max(0, needed - Insulation(after, cold));
            if (later > before) return null;
            if (before > later)
                result.Add(new(prefix + (cold ? "cold" : "heat"), ProductionNeedPriority.Safety, (before - later) / before));
        }
        return result;
    }
}
