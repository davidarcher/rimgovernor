using RimGovernor.Brain.Policy;
namespace RimGovernor.Brain.Runtime;

internal static class Hay
{
    internal static (CropChoice Crop, bool Found) HayCrop(ColonyReading p)
    {
        foreach (var crop in FieldPlanner.CropChoices(p))
        {
            if (SocialCropsRules.IsHayCrop(crop)) return (crop with { Edible = false }, true);
        }

        return (default!, false);
    }

    internal static (string Crop, int Needed, bool Found) HayTarget(ColonyReading p)
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

        var planned = AnimalFoodRules.PlanHayField(need, crop, p.Facts.CropClimate, out var plan);
        return (crop.Name, plan.Needed, planned && plan.Needed > 0);
    }

    internal static (FieldBlockOption Option, bool Found) HayShortfall(ColonyReading p)
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

        need = AnimalFoodRules.OutstandingHay(need, crop, (p.Tables.Zones ?? throw new RequiredFactException("zones")).Zones.OfType<RimGovernor.Brain.GameFacts.Observations.ZoneState.Growing>());

        if (need <= 0)
        {
            return (default!, false);
        }

        var planned = AnimalFoodRules.PlanHayField(need, crop, p.Facts.CropClimate, out var plan);
        if (!planned || plan.Needed <= 0)
        {
            return (default!, false);
        }

        return (new FieldBlockOption(plan.Crop, plan.Needed), true);
    }
}
