using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy.Methods.Generation;

internal static partial class FoodMethodCandidates
{
    internal static CoordinatedMethodCandidates GenerateObserved(FoodActivityRead read, long deadline, WorkResourceView? resources)
    {
        var bindings = new Dictionary<(string, string), ActivityTaskCandidate>();
        if (!read.Known || read.Context?.Tick is not { } tick || deadline <= tick) return new(null, [], bindings, read.Truncated);
        var horizon = deadline - tick;
        var rows = read.Options.Where(o => o.Count > 0 && double.IsFinite(o.TravelTicks) && o.TravelTicks >= 0)
            .Where(o => resources is null || !resources.Claims.Pawns.Contains(o.Pawn) && !resources.Claims.Targets.Contains(o.Target)
                && (o.Cooking is null || !resources.Claims.Targets.Contains(o.Cooking.Continuation.Bench)
                    && !resources.Claims.Targets.Contains(o.Cooking.Continuation.Bill))
                && resources.Materials.TryGetValue(out var budget) && budget.GetValueOrDefault(new Resource(o.Resource), Known(0.0)).TryGetValue(out var free)
                    && free >= (o.Cooking?.IngredientCount ?? o.Count))
            .Where(o => o.Cooking is { } cook ? cook.IngredientCount > 0 && cook.IngredientCount <= o.Count
                && double.IsFinite(cook.CookTicks) && cook.CookTicks >= 0 && double.IsFinite(cook.OutputTravelTicks)
                && cook.OutputTravelTicks >= 0 && cook.ProductNutrition.TryGetValue(out var nutrition) && nutrition > 0
                : o.NutritionPerUnit.TryGetValue(out var raw) && raw > 0)
            .OrderBy(o => o.Pawn, StringComparer.Ordinal).ThenBy(o => o.Target, StringComparer.Ordinal)
            .ThenBy(o => o.Cooking?.Continuation.Recipe ?? "", StringComparer.Ordinal).Take(17).ToArray();
        var activities = new List<ForecastActivity>(); var options = new List<ForecastActivityOption>();
        var routes = new List<ForecastActivityRoute>(); var outcomes = new List<ForecastActivityOutcome>();
        var truncated = read.Truncated || rows.Length > 16;
        foreach (var row in rows.Take(16))
        {
            if (activities.Count + (row.Cooking is null ? 1 : 3) > 32) { truncated = true; break; }
            var id = "observed-food-" + outcomes.Count;
            var location = id + "-route";
            routes.Add(new(row.Pawn, "start", location, Known(0.0)));
            if (row.Cooking is { } cooking)
            {
                var supply = id + "-supply"; var cook = id + "-cook"; var c = cooking.Continuation;
                activities.Add(new(supply, location, location, []));
                activities.Add(FoodRecipeActivity.Create(cook, location, [supply], new(row.Resource), cooking.IngredientCount,
                    new(c.Product), c.ProductCount));
                activities.Add(new(id, location, location, [cook]));
                options.Add(new(supply, row.Pawn, Known(row.TravelTicks), Known(0.0)));
                options.Add(new(cook, row.Pawn, Known(cooking.CookTicks), Known(0.0)));
                options.Add(new(id, row.Pawn, Known(cooking.OutputTravelTicks), Known(0.0)));
                bindings[(supply, row.Pawn)] = new(supply, row.Pawn, [], PawnActivityKind.Deliver, row.Target, "", "",
                    cooking.IngredientCount, deadline, row.Origin) { Resource = row.Resource, Source = row.Origin, Destination = row.Destination };
                bindings[(cook, row.Pawn)] = new(cook, row.Pawn, [supply], PawnActivityKind.Recipe, c.Bench, c.WorkGiver, "", 0, deadline, c.Origin)
                    { Resource = row.Resource, Source = row.Destination, Cooking = cooking, Ingredient = row.Target };
                bindings[(id, row.Pawn)] = new(id, row.Pawn, [cook], PawnActivityKind.Deliver, "observed-output", "", "", c.ProductCount, deadline, c.Origin)
                    { Resource = c.Product, Source = c.Origin, Destination = c.Destination };
                outcomes.Add(new(id, "food-method", id, Known((double)horizon), default)
                    { ProgressUnits = Known(c.ProductCount * cooking.ProductNutrition.Value) });
            }
            else
            {
                activities.Add(new(id, location, location, [])); options.Add(new(id, row.Pawn, Known(row.TravelTicks), Known(0.0)));
                bindings[(id, row.Pawn)] = new(id, row.Pawn, [], PawnActivityKind.Deliver, row.Target, "", "", row.Count, deadline, row.Origin)
                    { Resource = row.Resource, Source = row.Origin, Destination = row.Destination };
                outcomes.Add(new(id, "food-method", id, Known((double)horizon), default) { ProgressUnits = Known(row.Count * row.NutritionPerUnit.Value) });
            }
        }
        return new(activities.Count == 0 ? null : new() { Activities = activities.ToArray(), Options = options.ToArray(), Routes = routes.ToArray(),
            Pawns = rows.Select(o => o.Pawn).Distinct().Select(p => new ForecastPawnCapacity(p, "start",
                resources is null ? Known<EquatableArray<ForecastTimeWindow>>([new(0, horizon)])
                    : resources.Capacity.TryGetValue(p, out var capacity) && capacity.Capacity.Windows.TryGetValue(out var windows)
                        ? Known(EquatableArray.From(windows.Select(w => w with { Start = Math.Max(0, w.Start - (tick - resources.Evidence.Tick.Value)),
                            End = w.End - (tick - resources.Evidence.Tick.Value) }).Where(w => w.End > w.Start))) : default, Known(1.0))).ToArray(),
            Deadline = Known((double)horizon), AlternativeLaborCost = Known((double)horizon) }, outcomes.ToArray(), bindings, truncated);
    }
}
