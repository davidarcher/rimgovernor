using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy.Methods.Generation;

internal static partial class FoodMethodCandidates
{
    internal static CoordinatedMethodCandidates GenerateStart(FoodActivityRead read, long deadline, WorkResourceView? resources)
    {
        var bindings = new Dictionary<(string, string), ActivityTaskCandidate>();
        if (!read.Known || read.Context?.Tick is not { } tick || deadline <= tick || resources?.Claims.Truncated == true)
            return new(null, [], bindings, read.Truncated || resources?.Claims.Truncated == true);
        var horizon = deadline - tick;
        var groups = read.Options.Where(o => o.Cooking is null && o.Count > 0 && o.NutritionPerUnit.TryGetValue(out var nutrition) && nutrition > 0)
            .Where(o => resources is null || !resources.Claims.Pawns.Contains(o.Pawn) && !resources.Claims.Targets.Contains(o.Target))
            .GroupBy(o => (o.Target, o.Resource, o.Origin, o.Destination)).OrderBy(g => g.Key.Target, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Destination.X).ThenBy(g => g.Key.Destination.Z).Take(17).ToArray();
        var activities = new List<ForecastActivity>();
        var options = new List<ForecastActivityOption>();
        var routes = new List<ForecastActivityRoute>();
        var outcomes = new List<ForecastActivityOutcome>();
        var workers = new HashSet<string>(StringComparer.Ordinal);
        var truncated = read.Truncated || groups.Length > 16;
        foreach (var group in groups.Take(16))
        foreach (var count in group.Select(o => o.Count).Distinct().OrderDescending())
        {
            if (outcomes.Count == 16) { truncated = true; break; }
            var id = "food-" + outcomes.Count;
            var harvest = id + "-harvest";
            var location = id + "-source";
            activities.Add(new(harvest, location, location, []));
            activities.Add(new(id, location, id + "-destination", [harvest]));
            foreach (var row in group.GroupBy(o => o.Pawn).Select(g => g.First()))
            {
                workers.Add(row.Pawn);
                routes.Add(new(row.Pawn, "start", location, row.ApproachTicks));
                // The delivery service includes its selected loaded walk; approach remains separate.
                if (row.Count >= count)
                {
                    options.Add(new(id, row.Pawn, row.DeliveryTicks, Known(0.0)));
                    bindings[(id, row.Pawn)] = new(id, row.Pawn, [harvest],
                        PawnActivityKind.Deliver, row.Target, "", "", count, deadline,
                        row.Origin) { Resource = row.Resource, Source = row.Origin, Destination = row.Destination };
                }
                if (!row.HarvestAllowed.TryGetValue(out var allowed) || !allowed || !double.IsFinite(row.WorkTicks) || row.WorkTicks < 0) continue;
                options.Add(new(harvest, row.Pawn, Known(row.WorkTicks), Known(0.0)));
                bindings[(harvest, row.Pawn)] = new(harvest, row.Pawn, [],
                    PawnActivityKind.Work, row.Target, row.WorkGiver, "Harvest", 0, deadline, row.Origin);
            }
            outcomes.Add(new(id, "food-method", id, Known((double)horizon), default)
                { ProgressUnits = Known(count * group.First().NutritionPerUnit.Value) });
        }
        var capacities = workers.Order(StringComparer.Ordinal).Select(pawn => new ForecastPawnCapacity(pawn, "start",
            resources is null ? Known<EquatableArray<ForecastTimeWindow>>([new(0, horizon)])
                : resources.Capacity.TryGetValue(pawn, out var capacity) && capacity.Capacity.Windows.TryGetValue(out var windows)
                    ? Known(EquatableArray.From(windows.Select(w => w with { Start = Math.Max(0, w.Start - (tick - resources.Evidence.Tick.Value)),
                        End = w.End - (tick - resources.Evidence.Tick.Value) }).Where(w => w.End > w.Start))) : default,
            Known(1.0))).ToArray();
        return new(activities.Count == 0 ? null : new() { Activities = activities.ToArray(), Pawns = capacities,
            Options = options.ToArray(), Routes = routes.ToArray(), Deadline = Known((double)horizon),
            AlternativeLaborCost = Known((double)horizon), ExpansionBudget = 512 }, outcomes.ToArray(), bindings,
            truncated);
    }
}
