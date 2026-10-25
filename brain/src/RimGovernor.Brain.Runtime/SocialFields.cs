namespace RimGovernor.Brain.Runtime;

internal static class SocialFields
{
    /// <summary>The catalog's crops that harvest a social-drug ingredient, ordered by name.</summary>
    internal static IReadOnlyList<Pol.CropChoice> SocialCrops(RimGovernor.Brain.GameAccess.ColonyReading projection)
    {
        var output = new List<Pol.CropChoice>();
        if (projection.Facts.Preferences.Forbids(Pol.ForbiddenMethod.Drugs))
        {
            return output;
        }
        output.AddRange(FieldPlanner.CropChoices(projection).Where(SocialCropsRules.IsSocialCrop));
        return [.. output.OrderBy(c => c.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Each social crop's missing cells under the fixed nine-cell ceiling, counting existing player fields
    /// without changing them: pure demand; FieldPlanner places the blocks. Known is false while a social
    /// field's size is unobserved.
    /// </summary>
    internal static (IReadOnlyList<Pol.FieldBlockOption> Options, bool Known) SocialShortfalls(RimGovernor.Brain.GameAccess.ColonyReading projection)
    {
        var output = new List<Pol.FieldBlockOption>();
        foreach (var crop in SocialCrops(projection))
        {
            var cells = 0;
            foreach (var farm in (projection.Tables.Zones ?? throw new BridgeContractException("missing zones census")).Zones.OfType<RimGovernor.Brain.GameFacts.Observations.ZoneState.Growing>())
            {
                if (farm.Crop == crop.Name)
                {
                    cells += (int)farm.UsableCells;
                }
            }
            var needed = SocialCropsRules.PlanSocialCrop(crop, projection.Facts.CropClimate, cells);
            if (needed > 0)
            {
                output.Add(new Pol.FieldBlockOption(crop, needed));
            }
        }
        return (output, true);
    }
}
