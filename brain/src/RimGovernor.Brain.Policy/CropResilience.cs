namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>
    /// Counts observed enabled growers and capable cooks. Unknown pawn availability or work rows cannot
    /// establish that the colony has no cook.
    /// </summary>
    public static (Fact<int> Growers, Fact<int> Cooks) CropWorkers(Fact<EquatableArray<WorkPawn>> fact)
    {
        if (!fact.TryGetValue(out var pawns))
        {
            return default;
        }
        var g = 0;
        var c = 0;
        foreach (var pawn in pawns)
        {
            if (!pawn.Available.TryGetValue(out var available))
            {
                return default;
            }
            var wk = pawn.Work.TryGetValue(out var work);
            if (!available)
            {
                continue;
            }
            var sk = pawn.Skills.IsKnown;
            if (!wk || !sk)
            {
                return default;
            }
            var profile = BuildProfile(pawn);
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
        return (Known(g), Known(c));
    }

    /// <summary>
    /// Harvest work is priced against the observed grower share. With spare growers, the time before the first
    /// harvest matters more than work saved per nutrition. Both charges are fractions of daily output, so they
    /// compare across crops.
    /// </summary>
    internal static List<FarmSiteTerm> CropChoiceTerms(FieldRequest r, CropChoice crop, int cells)
    {
        var unit = CropRate(crop, 1) * cells;
        var terms = new List<FarmSiteTerm>();
        var wk = crop.HarvestWork.TryGetValue(out var work);
        var yk = crop.HarvestNutrition.TryGetValue(out var yield);
        var dk = crop.GrowDays.TryGetValue(out var days);
        var gk = r.Growers.TryGetValue(out var growers);
        var pk = r.Colonists.TryGetValue(out var people);
        if (wk && yk && dk && gk && pk && FoodNumber(work) && work >= 0 && FieldPositive(yield) && FieldPositive(days) && growers >= 0 && people > 0)
        {
            terms.Add(new FarmSiteTerm("labor", -unit * .0002 * work / yield * people / Math.Max(1, growers)));
            terms.Add(new FarmSiteTerm("harvest-delay", -unit * .03 * days));
        }
        if (r.Cooks.TryGetValue(out var cooks) && cooks == 0 && crop.RawPreferred.TryGetValue(out var raw) && raw)
        {
            terms.Add(new FarmSiteTerm("cook", unit));
        }
        return terms;
    }

    internal static bool CropSoilCompatible(CropChoice crop, SiteCell cell)
    {
        if (GrowsInDark(crop))
        {
            // Dark is a roofed cell with no lamp: an open cell is lit by the sky for part of every day,
            // whatever hour the cells were read.
            var known = cell.Glow.TryGetValue(out var glow);
            var roofKnown = cell.Roofed.TryGetValue(out var roofed);
            if (!known || glow != 0 || !roofKnown || !roofed)
            {
                return false;
            }
        }
        var pk = cell.Polluted.TryGetValue(out var pollution);
        if (Positive(crop.RequiresPollution) && (!pk || !pollution))
        {
            return false;
        }
        if (Positive(crop.RequiresCleanSoil) && (!pk || pollution))
        {
            return false;
        }
        return true;
    }

    /// <summary>Indoor resilience is credited only for an observed threat. Feasibility (power, heat, crop and cells) is established before these terms are added.</summary>
    internal static List<FarmSiteTerm> SiteRiskTerms(FieldRequest r, CropChoice crop, int cells)
    {
        var unit = CropRate(crop, 1) * cells;
        var terms = new List<FarmSiteTerm>();
        if (ToxicFalloutActive(r.Conditions).TryGetValue(out var fallout) && fallout)
        {
            terms.Add(new FarmSiteTerm("risk-fallout", 2 * unit));
        }
        if (HarvestGapDays(r.Calendar, Known(EquatableArray.From<DisasterCondition>([]))).TryGetValue(out var gap) && gap > 0)
        {
            terms.Add(new FarmSiteTerm("risk-frost", 2 * unit * Math.Min(1, gap / Math.Max(1, r.ReserveDays))));
        }
        return terms;
    }
}
