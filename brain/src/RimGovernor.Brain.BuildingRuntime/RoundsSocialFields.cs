namespace RimGovernor.Brain.BuildingRuntime;

internal static partial class BuildingRuntimePackage
{
    /// <summary>The catalog's crops that harvest a social-drug ingredient, ordered by name.</summary>
    internal static IReadOnlyList<Pol.CropChoice> SocialCrops(Observation.ColonyProjection projection)
    {
        var output = new List<Pol.CropChoice>();
        foreach (var d in projection.Definitions)
        {
            var crop = WithHarvestFacts(
                new Pol.CropChoice
                {
                    Name = d.Name,
                    Available = d.Available,
                    Edible = d.Edible,
                    GrowDays = d.GrowDays,
                    FertilityMin = d.FertilityMin,
                    FertilitySensitivity = d.FertilitySensitivity,
                    SowTags = d.SowTags,
                    MinGlow = d.GrowMinGlow,
                    RequiresPollution = d.RequiresPollution,
                    RequiresCleanSoil = d.RequiresCleanSoil,
                },
                d);
            if (PolicyFunctions.IsSocialCrop(crop))
            {
                output.Add(crop);
            }
        }
        return [.. output.OrderBy(c => c.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Each social crop's missing cells under the fixed nine-cell ceiling, counting existing player fields
    /// without changing them: pure demand; RoundsFieldPlanner places the blocks. Known is false while a social
    /// field's size is unobserved.
    /// </summary>
    internal static (IReadOnlyList<Pol.FieldBlockOption> Options, bool Known) SocialShortfalls(Observation.ColonyProjection projection)
    {
        var output = new List<Pol.FieldBlockOption>();
        foreach (var crop in SocialCrops(projection))
        {
            var cells = 0;
            foreach (var farm in projection.Farms)
            {
                if (farm.Crop == crop.Name)
                {
                    if (!farm.UsableCells.TryGetValue(out var n))
                    {
                        return ([], false);
                    }
                    cells += (int)n;
                }
            }
            var needed = PolicyFunctions.PlanSocialCrop(crop, projection.CropClimate, cells);
            if (needed > 0)
            {
                output.Add(new Pol.FieldBlockOption(crop, needed));
            }
        }
        return (output, true);
    }
}
