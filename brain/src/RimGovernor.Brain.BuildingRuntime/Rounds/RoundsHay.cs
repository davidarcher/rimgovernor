using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.BuildingRuntime;

internal static class RoundsHay
{
    internal static (CropChoice Crop, bool Found) HayCrop(ColonyProjection p)
    {
        foreach (var crop in RoundsFieldPlanner.CropChoices(p))
        {
            if (SocialCropsRules.IsHayCrop(crop)) return (crop with { Edible = false }, true);
        }

        return (default!, false);
    }

    internal static (string Crop, int Needed, bool Found) HayTarget(ColonyProjection p)
    {
        var need = AnimalFoodRules.HayNutritionNeed(p.Facts.PenGrazing, CalendarRules.HarvestGapDays(p.Facts.Calendar, p.Facts.DisasterConditions));
        if (need <= 0)
        {
            return ("", 0, false);
        }

        var (crop, ok) = HayCrop(p);
        if (!ok)
        {
            return ("", 0, false);
        }

        var planned = AnimalFoodRules.PlanHayField(need, crop, p.CropClimate, out var plan);
        return (crop.Name, plan.Needed, planned && plan.Needed > 0);
    }

    internal static (FieldBlockOption Option, bool Found) HayShortfall(ColonyProjection p)
    {
        var need = AnimalFoodRules.HayNutritionNeed(p.Facts.PenGrazing, CalendarRules.HarvestGapDays(p.Facts.Calendar, p.Facts.DisasterConditions));
        if (need <= 0)
        {
            return (default!, false);
        }

        var (crop, ok) = HayCrop(p);
        if (!ok)
        {
            return (default!, false);
        }

        var yield = crop.HarvestNutrition;

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

        var planned = AnimalFoodRules.PlanHayField(need, crop, p.CropClimate, out var plan);
        if (!planned || plan.Needed <= 0)
        {
            return (default!, false);
        }

        return (new FieldBlockOption(plan.Crop, plan.Needed), true);
    }
}
