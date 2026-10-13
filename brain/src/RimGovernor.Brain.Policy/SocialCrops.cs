namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    public static bool BrewingFinished(Fact<ResearchFacts> research)
    {
        if (research.TryGetValue(out var facts))
        {
            foreach (var project in facts.Finished)
            {
                if (project.Value == "Brewing")
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Each social crop's fixed field ceiling.</summary>
    public const int SocialCropCells = 9;

    /// <summary>The harvests the social drugs brew or roll from; a crop is social by what it harvests, not by name.</summary>
    private static readonly HashSet<string> SocialCropResources = new(StringComparer.Ordinal) { "Hops", "SmokeleafLeaves" };

    /// <summary>The thing animal-feed crops harvest.</summary>
    public static readonly Resource HayResource = new("Hay");

    /// <summary>Whether crop harvests a social-drug ingredient.</summary>
    public static bool IsSocialCrop(CropChoice crop) =>
        crop.Harvests.TryGetValue(out var harvests) && SocialCropResources.Contains(harvests.Value);

    /// <summary>Whether crop harvests hay.</summary>
    public static bool IsHayCrop(CropChoice crop) => crop.Harvests.TryGetValue(out var harvests) && harvests == HayResource;

    /// <summary>
    /// The cells a social crop still needs under its nine-cell ceiling, zero when none or out of season; the
    /// caller sites them in the layout plan's field blocks.
    /// </summary>
    public static int PlanSocialCrop(CropChoice crop, CropClimate climate, int existing)
    {
        if (!IsSocialCrop(crop) || existing < 0 || existing >= SocialCropCells)
        {
            return 0;
        }
        var known = crop.Available.TryGetValue(out var available);
        var sk = climate.SowingOutdoors().TryGetValue(out var sowing);
        var dk = crop.GrowDays.TryGetValue(out var days);
        var remaining = climate.DaysRemaining.TryGetValue(out var season);
        if (!known || !available || !sk || !sowing || !dk || !FieldPositive(days) || !remaining || season < days * 2.5)
        {
            return 0;
        }
        return SocialCropCells - existing;
    }
}
