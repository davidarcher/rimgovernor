using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy.Methods.Generation;

internal static class ConstructionMethodCandidates
{
    internal static CoordinatedMethodCandidates Generate(ConstructionActivityRead read, long deadline, WorkResourceView resources)
    {
        var bindings = new Dictionary<(string, string), ActivityTaskCandidate>();
        if (!read.Known || read.Context?.Tick is not { } tick || deadline <= tick || resources.Claims.Truncated)
            return new(null, [], bindings, read.Truncated || resources.Claims.Truncated);
        var horizon = deadline - tick;
        var groups = read.Options.Where(o => !resources.Claims.Pawns.Contains(o.Pawn) && !resources.Claims.Targets.Contains(o.Site)
            && (o.Input.Length == 0 || !resources.Claims.Targets.Contains(o.Input))).GroupBy(o => (o.Site, o.Input))
            .OrderBy(g => g.Key.Site, StringComparer.Ordinal).ThenBy(g => g.Key.Input, StringComparer.Ordinal).Take(17).ToArray();
        var activities = new List<ForecastActivity>(); var options = new List<ForecastActivityOption>();
        var routes = new List<ForecastActivityRoute>(); var outcomes = new List<ForecastActivityOutcome>();
        var workers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups.Take(16))
        {
            var id = "site-" + outcomes.Count;
            var finish = id + "-finish";
            var delivery = id + "-delivery";
            var supplied = group.All(o => o.Count == 0);
            // Every option describes the same single-resource site demand; unobserved/mixed demand cannot be guessed.
            if (!supplied && group.Where(o => o.Count > 0).Select(o => (o.Resource, o.Count, o.Origin)).Distinct().Count() != 1) continue;
            if (!supplied) activities.Add(new(delivery, id + "-source", id, []));
            activities.Add(new(finish, id, id, supplied ? [] : [delivery]));
            foreach (var row in group.GroupBy(o => o.Pawn).Select(g => g.First()))
            {
                workers.Add(row.Pawn);
                if (row.Count > 0 && row.Capacity > 0)
                {
                    options.Add(new(delivery, row.Pawn, row.DeliveryTicks, Known(0.0)));
                    routes.Add(new(row.Pawn, "start", id + "-source", row.ApproachTicks));
                    bindings[(delivery, row.Pawn)] = new(delivery, row.Pawn, [], PawnActivityKind.Supply,
                        row.Input, "", "", row.Count, deadline, row.Location)
                        { Resource = row.Resource, Source = row.Origin, Container = row.Site, SiteDefinition = row.Definition, Capacity = row.Capacity };
                }
                if (row.FinishTicks.IsKnown)
                {
                    options.Add(new(finish, row.Pawn, row.FinishTicks, Known(0.0)));
                    routes.Add(new(row.Pawn, "start", id, row.SiteApproachTicks));
                    bindings[(finish, row.Pawn)] = new(finish, row.Pawn, supplied ? [] : [delivery], PawnActivityKind.Work,
                        row.Site, row.WorkGiver, "FinishFrame", 0, deadline, row.Location) { SiteDefinition = row.Definition, CompletionMustBeObserved = true };
                }
            }
            outcomes.Add(new(finish, "construction-method", finish, Known((double)horizon), default) { ProgressUnits = Known(1.0) });
        }
        var capacities = workers.Order(StringComparer.Ordinal).Select(pawn => new ForecastPawnCapacity(pawn, "start",
            resources.Capacity.TryGetValue(pawn, out var capacity) && capacity.Capacity.Windows.TryGetValue(out var windows)
                ? Known(EquatableArray.From(windows.Select(w => w with { Start = Math.Max(0, w.Start - (tick - resources.Evidence.Tick.Value)),
                    End = w.End - (tick - resources.Evidence.Tick.Value) }).Where(w => w.End > w.Start))) : default, Known(1.0))).ToArray();
        return new(activities.Count == 0 ? null : new() { Activities = activities.ToArray(), Options = options.ToArray(), Routes = routes.ToArray(),
            Pawns = capacities, Deadline = Known((double)horizon), AlternativeLaborCost = Known((double)horizon) }, outcomes.ToArray(), bindings,
            read.Truncated || groups.Length > 16);
    }
}
