using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

internal static class RoundsHay
{
    internal static (CropChoice Crop, bool Found) HayCrop(ColonyProjection p)
    {
        foreach (var d in p.Definitions)
        {
            var crop = RoundsFieldPlanner.WithHarvestFacts(
                new CropChoice
                {
                    Name = d.Name,
                    Available = d.Available,
                    Edible = Known(false),
                    GrowDays = d.GrowDays,
                    HarvestNutrition = d.HarvestNutrition,
                    FertilityMin = d.FertilityMin,
                    FertilitySensitivity = d.FertilitySensitivity,
                    SowTags = d.SowTags,
                    MinGlow = d.GrowMinGlow,
                },
                d);
            if (SocialCropsRules.IsHayCrop(crop))
            {
                return (crop, true);
            }
        }

        return (default!, false);
    }

    internal static (string Crop, int Needed, bool Found) HayTarget(ColonyProjection p)
    {
        if (!AnimalFoodRules.HayNutritionNeed(p.Facts.PenGrazing, CalendarRules.HarvestGapDays(p.Facts.Calendar, p.Facts.DisasterConditions)).TryGetValue(out var need) || need <= 0)
        {
            return ("", 0, false);
        }

        var (crop, ok) = HayCrop(p);
        if (!ok)
        {
            return ("", 0, false);
        }

        var planned = AnimalFoodRules.PlanHayField(Known(need), crop, p.CropClimate, out var plan);
        return (crop.Name, plan.Needed, planned && plan.Needed > 0);
    }

    internal static (FieldBlockOption Option, bool Found) HayShortfall(ColonyProjection p)
    {
        if (!AnimalFoodRules.HayNutritionNeed(p.Facts.PenGrazing, CalendarRules.HarvestGapDays(p.Facts.Calendar, p.Facts.DisasterConditions)).TryGetValue(out var need) || need <= 0)
        {
            return (default!, false);
        }

        var (crop, ok) = HayCrop(p);
        if (!ok)
        {
            return (default!, false);
        }

        if (!crop.HarvestNutrition.TryGetValue(out var yield))
        {
            return (default!, false);
        }

        foreach (var zone in p.Farms)
        {
            if (zone.Crop == crop.Name)
            {
                need = Math.Max(0, need - (zone.UsableCells * yield));
            }
        }

        if (need <= 0)
        {
            return (default!, false);
        }

        var planned = AnimalFoodRules.PlanHayField(Known(need), crop, p.CropClimate, out var plan);
        if (!planned || plan.Needed <= 0)
        {
            return (default!, false);
        }

        return (new FieldBlockOption(plan.Crop, plan.Needed), true);
    }
}
