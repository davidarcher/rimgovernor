using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy.Methods.Generation;

/// <summary>Ephemeral alternatives for one food Method admission or continuation, not admitted Methods.</summary>
internal sealed record FoodMethodCandidates(ForecastActivityProblem? Problem, ForecastActivityOutcome[] Outcomes,
    IReadOnlyDictionary<string, FoodActivityOption> Bindings, bool Truncated)
{
    internal static FoodMethodCandidates Generate(FoodActivityRead read, long deadline,
        string? pawn = null, string? resource = null, ActivityOutputDelivery? obligation = null, bool allowCooking = false, WorkResourceView? resources = null)
    {
        if (!read.Known || read.Context?.Tick is not { } tick || deadline <= tick || resources?.Claims.Truncated == true)
            return new(null, [], new Dictionary<string, FoodActivityOption>(), read.Truncated || resources?.Claims.Truncated == true);
        var horizon = deadline - tick;
        // The shared outcome comparator admits sixteen alternatives. Stable enumeration is not ranking.
        var rows = read.Options.Where(o => (pawn is null || o.Pawn == pawn) && (resource is null || o.Resource == resource))
            .Where(o => resources is null || !resources.Claims.Pawns.Contains(o.Pawn) && !resources.Claims.Targets.Contains(o.Target)
                && (o.Cooking is null || !resources.Claims.Targets.Contains(o.Cooking.Continuation.Bench)
                    && !resources.Claims.Targets.Contains(o.Cooking.Continuation.Bill))
                && (obligation is null || resources.Materials.TryGetValue(out var budget)
                    && budget.GetValueOrDefault(new Resource(o.Resource), Known(0.0)).TryGetValue(out var free)
                    && free >= (o.Cooking?.IngredientCount ?? o.Count)))
            .Where(o => obligation?.Cooking is { } pinned
                ? o.Cooking is { Supplied: true } c && c.Continuation.Bench == pinned.Bench && c.Continuation.Bill == pinned.Bill
                    && c.Continuation.Recipe == pinned.Recipe && c.IngredientCount == obligation.Count
                    && c.Continuation.Product == pinned.Product && c.Continuation.ProductCount == pinned.ProductCount
                : o.Cooking is null || allowCooking && !o.Cooking.Supplied)
            .Where(o => o.Count > 0 && double.IsFinite(o.TravelTicks) && o.TravelTicks >= 0 && double.IsFinite(o.WorkTicks) && o.WorkTicks >= 0)
            .Where(o => o.Cooking is { } c
                ? c.IngredientCount > 0 && c.IngredientCount <= o.Count && double.IsFinite(c.CookTicks) && c.CookTicks >= 0
                    && double.IsFinite(c.OutputTravelTicks) && c.OutputTravelTicks >= 0 && c.ProductNutrition.TryGetValue(out var nutrition) && nutrition > 0
                : o.NutritionPerUnit.TryGetValue(out var rawNutrition) && rawNutrition > 0)
            .OrderBy(o => o.Pawn, StringComparer.Ordinal).ThenBy(o => o.Target, StringComparer.Ordinal)
            .ThenBy(o => o.Cooking?.Continuation.Recipe ?? "", StringComparer.Ordinal)
            .ThenBy(o => o.Destination.X).ThenBy(o => o.Destination.Z).Take(17).ToArray();
        var bindings = new Dictionary<string, FoodActivityOption>();
        var activities = new List<ForecastActivity>();
        var options = new List<ForecastActivityOption>();
        var routes = new List<ForecastActivityRoute>();
        var outcomes = new List<ForecastActivityOutcome>();
        var truncated = read.Truncated || rows.Length > 16;
        var capacities = resources?.Capacity.ToDictionary(p => p.Key, p => p.Value.Capacity.Windows.TryGetValue(out var windows)
            ? Known(EquatableArray.From(windows.Where(w => w.End > tick - resources.Evidence.Tick.Value)
                .Select(w => w with { Start = Math.Max(0, w.Start - (tick - resources.Evidence.Tick.Value)),
                    End = w.End - (tick - resources.Evidence.Tick.Value) }))) : default, StringComparer.Ordinal);
        var workers = rows.Take(16).Select(o => o.Pawn).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var option in rows.Take(16))
        {
            if (activities.Count + (option.Cooking is { Supplied: false } ? 3 : 2) > 32) { truncated = true; break; }
            var id = "trip-" + bindings.Count.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
            var worker = "worker-" + Array.IndexOf(workers, option.Pawn).ToString(System.Globalization.CultureInfo.InvariantCulture);
            bindings.Add(id, option);
            var service = id + "-service";
            var cooking = option.Cooking;
            if (cooking is not null)
            {
                var supply = id + "-supply";
                if (!cooking.Supplied)
                {
                    activities.Add(new(supply, service, service, []));
                    options.Add(new(supply, worker, Known(0.0), Known(0.0)));
                }
                activities.Add(FoodRecipeActivity.Create(service, service, cooking.Supplied ? [] : [supply],
                    new(option.Resource), cooking.IngredientCount, new(cooking.Continuation.Product), cooking.Continuation.ProductCount));
            }
            else activities.Add(new(service, service, service, []));
            activities.Add(new(id, id, id, [service]));
            options.Add(new(service, worker, Known(option.WorkTicks + (cooking?.CookTicks ?? 0)), Known(0.0)));
            options.Add(new(id, worker, Known(0.0), Known(0.0)));
            routes.Add(new(worker, "start", service, Known(option.TravelTicks)));
            routes.Add(new(worker, service, id, Known(cooking?.OutputTravelTicks ?? 0)));
            var nutrition = cooking is null ? option.Count * option.NutritionPerUnit.Value : cooking.Continuation.ProductCount * cooking.ProductNutrition.Value;
            outcomes.Add(new(id, "food-method", id, Known((double)horizon), default) { ProgressUnits = Known(nutrition) });
        }
        // Same local assumptions as before: equal labor rates, native Danger.None routes, an
        // acquisition horizon as labor ceiling. No new colony demand or calibrated avoided loss.
        var problem = activities.Count == 0 ? null : new ForecastActivityProblem
        {
            Activities = activities.ToArray(), Options = options.ToArray(), Routes = routes.ToArray(),
            Pawns = workers.Select((pawnId, index) => new ForecastPawnCapacity("worker-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), "start",
                resources is null ? Known<EquatableArray<ForecastTimeWindow>>([new(0, horizon)])
                    : capacities!.GetValueOrDefault(pawnId), Known(1.0))).ToArray(),
            Deadline = Known((double)horizon), AlternativeLaborCost = Known((double)horizon), ExpansionBudget = 512,
        };
        return new(problem, outcomes.ToArray(), bindings, truncated);
    }
}
