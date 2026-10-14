using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy.Activities.Generation;

internal static class FoodActivityCandidates
{
    internal static ActivityCandidate<FoodActivityOption>[] Generate(FoodActivityRead read, long deadline,
        string? pawn = null, string? resource = null)
    {
        if (!read.Known || read.Context?.Tick is not { } tick || deadline <= tick) return [];
        var horizon = deadline - tick;
        var candidates = new List<ActivityCandidate<FoodActivityOption>>();
        foreach (var option in read.Options.Where(o => (pawn is null || o.Pawn == pawn) && (resource is null || o.Resource == resource))
            .OrderBy(o => o.Pawn, StringComparer.Ordinal).ThenBy(o => o.Target, StringComparer.Ordinal)
            .ThenBy(o => o.Destination.X).ThenBy(o => o.Destination.Z).Take(128))
        {
            if (option.Count <= 0 || !double.IsFinite(option.TravelTicks) || option.TravelTicks < 0
                || !double.IsFinite(option.WorkTicks) || option.WorkTicks < 0) continue;
            // Local bridge assumptions: equal labor rates, native Danger.None routes, no injury
            // estimate beyond that restriction. These are not calibrated colony opportunity costs.
            candidates.Add(new(option, new()
            {
                Activities = [new("trip", "service", "done", [])],
                Pawns = [new("worker", "start", Known<EquatableArray<ForecastTimeWindow>>([new(0, horizon)]), Known(1.0))],
                Options = [new("trip", "worker", Known(option.WorkTicks), Known(0.0))],
                Routes = [new("worker", "start", "service", Known(option.TravelTicks))],
                Deadline = Known((double)horizon), AlternativeLaborCost = Known((double)horizon), ExpansionBudget = 16,
            }, option.Count));
        }
        return candidates.ToArray();
    }
}
