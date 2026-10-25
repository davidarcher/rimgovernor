using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>The steady service assumption shared by the colony cycle and room-use accounting; detailed activities remain selected by the shared portfolio.</summary>
public static class NeedServiceRules
{
    public static NeedTarget? Meal(NeedsForecastPawn pawn) => pawn.Targets
        .Where(t => t.Kind == "food" && t.Recovery is NeedRecovery.Ingest { GainPerUnit: > 0 } && t.AvailableCount > 0)
        .OrderByDescending(t => ((NeedRecovery.Ingest)t.Recovery).GainPerUnit).ThenBy(t => t.Id, StringComparer.Ordinal).FirstOrDefault();

    public static double MealGain(NeedTarget target, NeedRate need)
    {
        var ingest = (NeedRecovery.Ingest)target.Recovery;
        DomainException.Require(double.IsFinite(ingest.GainPerUnit) && ingest.GainPerUnit > 0
            && need.Threshold is >= 0 and < 1 && target.AvailableCount > 0, "meal refill mechanics");
        var wanted = 1 - need.Threshold;
        var count = Math.Min(target.AvailableCount, Math.Ceiling(wanted / ingest.GainPerUnit));
        return Math.Min(wanted, count * ingest.GainPerUnit);
    }

    public static ModelNeed Steady(NeedsForecastPawn pawn, NeedRate need)
    {
        if (need.Kind == "joy" && need.OutdoorShareByTwelfth.Count != 12)
            throw new RequiredFactException("outdoor recreation share of " + pawn.Pawn);
        var targets = pawn.Targets.Where(t => t.Kind == need.Kind).ToArray();
        var sheltered = targets.Select(t => t.Recovery is NeedRecovery.Rate r ? r.GainPerTick : 0).DefaultIfEmpty(0).Max();
        var meal = Meal(pawn);
        return new()
        {
            Kind = need.Kind, Fall = need.FallPerTick, Gain = Math.Max(need.UnservedGainPerTick, sheltered),
            OutdoorShares = need.OutdoorShareByTwelfth, ShelteredGain = sheltered, Threshold = need.Threshold,
            Service = need.Kind == "food" && meal?.Recovery is NeedRecovery.Ingest ingest
                ? new ModelService.Observed(MealGain(meal, need), ingest.ServiceTicks, targets.Average(t => t.TravelTicks))
                : new ModelService.NoneObserved(),
        };
    }
}
