namespace RimGovernor.Brain.Policy;

public static class CropResilienceRules
{
    /// <summary>
    /// Counts observed enabled growers and capable cooks. Unknown pawn availability or work rows cannot
    /// establish that the colony has no cook.
    /// </summary>
    public static (int Growers, int Cooks) CropWorkers(WorkPawnsRead fact)
    {
        var pawns = fact.Pawns;
        var g = 0;
        var c = 0;
        foreach (var pawn in pawns)
        {
            if (!pawn.Available || pawn.Work is not PawnWorkTable.Works { Rows: var work })
            {
                continue;
            }
            var profile = PawnRules.BuildProfile(pawn);
            foreach (var row in work)
            {
                if (row.Disabled || !profile.Capable(row.Work, 0))
                {
                    continue;
                }
                if (row.Work == WorkType.Growing && row.Priority > 0)
                {
                    g++;
                }
                if (row.Work == WorkType.Cooking)
                {
                    c++;
                }
            }
        }
        return (g, c);
    }

    /// <summary>
    /// Harvest work is priced against the observed grower share. With spare growers, the time before the first
    /// harvest matters more than work saved per nutrition. Both charges are fractions of daily output, so they
    /// compare across crops.
    /// </summary>
    internal static List<FarmSiteTerm> CropChoiceTerms(FieldRequest r, CropChoice crop, int cells)
    {
        var unit = SiteRules.CropRate(crop, 1) * cells;
        var terms = new List<FarmSiteTerm>();
        var work = crop.HarvestWork;
        var yield = crop.HarvestNutrition;
        var days = crop.GrowDays;
        var growers = r.Growers;
        var people = r.Colonists;
        if (FoodForecastRules.FoodNumber(work) && work >= 0 && FieldCapacityRules.FieldPositive(yield) && FieldCapacityRules.FieldPositive(days) && growers >= 0 && people > 0)
        {
            terms.Add(new FarmSiteTerm("labor", -unit * .0002 * work / yield * people / Math.Max(1, growers)));
            terms.Add(new FarmSiteTerm("harvest-delay", -unit * .03 * days));
        }
        var raw = crop.RawPreferred;
        var cooks = r.Cooks;
        if (cooks == 0 && raw)
        {
            terms.Add(new FarmSiteTerm("cook", unit));
        }
        return terms;
    }

    internal static bool CropSoilCompatible(CropChoice crop, SiteCell cell)
    {
        if (ControlledEnvironmentRules.GrowsInDark(crop))
        {
            // Dark is a roofed cell with no lamp: an open cell is lit by the sky for part of every day,
            // whatever hour the cells were read.
            if (cell.Glow != 0 || !cell.Roofed)
            {
                return false;
            }
        }
        if (crop.RequiresPollution && !cell.Polluted)
        {
            return false;
        }
        if (crop.RequiresCleanSoil && cell.Polluted)
        {
            return false;
        }
        return true;
    }

    /// <summary>Indoor resilience is credited only for an observed threat. Feasibility (power, heat, crop and cells) is established before these terms are added.</summary>
    internal static List<FarmSiteTerm> SiteRiskTerms(FieldRequest r, CropChoice crop, int cells)
    {
        var unit = SiteRules.CropRate(crop, 1) * cells;
        var terms = new List<FarmSiteTerm>();
        if (CropRiskRules.ToxicFalloutActive(r.Conditions))
        {
            terms.Add(new FarmSiteTerm("risk-fallout", 2 * unit));
        }
        var gap = CalendarRules.HarvestGapDays(r.Calendar, []);
        if (gap > 0)
        {
            terms.Add(new FarmSiteTerm("risk-frost", 2 * unit * Math.Min(1, gap / Math.Max(1, r.ReserveDays))));
        }
        return terms;
    }
}
