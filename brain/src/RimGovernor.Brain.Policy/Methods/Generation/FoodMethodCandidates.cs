using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy.Methods.Generation;

/// <summary>Ephemeral alternatives for one food Method admission or continuation, not admitted Methods.</summary>
internal sealed record FoodMethodCandidates(ForecastActivityProblem? Problem, ForecastActivityOutcome[] Outcomes,
    IReadOnlyDictionary<string, FoodActivityOption> Bindings, bool Truncated)
{
    internal static FoodMethodCandidates Generate(FoodActivityRead read, long deadline,
        string? pawn = null, string? resource = null)
    {
        if (!read.Known || read.Context?.Tick is not { } tick || deadline <= tick) return new(null, [], new Dictionary<string, FoodActivityOption>(), read.Truncated);
        var horizon = deadline - tick;
        // The shared outcome comparator admits sixteen alternatives. Stable enumeration is not ranking.
        var rows = read.Options.Where(o => (pawn is null || o.Pawn == pawn) && (resource is null || o.Resource == resource))
            .Where(o => o.Count > 0 && double.IsFinite(o.TravelTicks) && o.TravelTicks >= 0 && double.IsFinite(o.WorkTicks) && o.WorkTicks >= 0)
            .OrderBy(o => o.Pawn, StringComparer.Ordinal).ThenBy(o => o.Target, StringComparer.Ordinal)
            .ThenBy(o => o.Destination.X).ThenBy(o => o.Destination.Z).Take(17).ToArray();
        var bindings = new Dictionary<string, FoodActivityOption>();
        var activities = new List<ForecastActivity>();
        var options = new List<ForecastActivityOption>();
        var routes = new List<ForecastActivityRoute>();
        var outcomes = new List<ForecastActivityOutcome>();
        var workers = rows.Take(16).Select(o => o.Pawn).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var option in rows.Take(16))
        {
            var id = "trip-" + bindings.Count.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
            var worker = "worker-" + Array.IndexOf(workers, option.Pawn).ToString(System.Globalization.CultureInfo.InvariantCulture);
            bindings.Add(id, option);
            activities.Add(new(id, id, id, []));
            options.Add(new(id, worker, Known(option.WorkTicks), Known(0.0)));
            routes.Add(new(worker, "start", id, Known(option.TravelTicks)));
            outcomes.Add(new(id, "food-method", id, Known((double)horizon), default) { ProgressUnits = Known((double)option.Count) });
        }
        // Same local assumptions as before: equal labor rates, native Danger.None routes, an
        // acquisition horizon as labor ceiling. No new colony demand or calibrated avoided loss.
        var problem = activities.Count == 0 ? null : new ForecastActivityProblem
        {
            Activities = activities.ToArray(), Options = options.ToArray(), Routes = routes.ToArray(),
            Pawns = workers.Select((_, index) => new ForecastPawnCapacity("worker-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), "start",
                Known<EquatableArray<ForecastTimeWindow>>([new(0, horizon)]), Known(1.0))).ToArray(),
            Deadline = Known((double)horizon), AlternativeLaborCost = Known((double)horizon), ExpansionBudget = 512,
        };
        return new(problem, outcomes.ToArray(), bindings, read.Truncated || rows.Length > 16);
    }
}
