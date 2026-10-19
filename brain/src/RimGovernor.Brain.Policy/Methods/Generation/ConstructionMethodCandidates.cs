using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy.Methods.Generation;

internal static class ConstructionMethodCandidates
{
    internal const string FlooringRelief = "flooring"; // one relief per floor site, so each laid floor is credited once
    internal const string RemovalRelief = "removal"; // one relief per standing Deconstruct designation, so each removal is credited once (#3557)

    internal static CoordinatedMethodCandidates Generate(ConstructionActivityRead read, long deadline, WorkResourceView resources, IReadOnlySet<string> floors)
    {
        var bindings = new Dictionary<(string, string), ActivityTaskCandidate>();
        if (!read.Known || read.Context?.Tick is not { } tick || deadline <= tick || resources.Claims.Truncated)
            return new(null, [], bindings, resources.Claims.Truncated);
        var horizon = deadline - tick;
        var groups = read.Options.Where(o => !resources.Claims.Pawns.Contains(o.Pawn) && !resources.Claims.Targets.Contains(o.Site)
            && (o.Input.Length == 0 || !resources.Claims.Targets.Contains(o.Input))).GroupBy(o => (o.Site, o.Input))
            .OrderBy(g => g.Key.Site, StringComparer.Ordinal).ThenBy(g => g.Key.Input, StringComparer.Ordinal).Take(17).ToArray();
        var activities = new List<ForecastActivity>(); var options = new List<ForecastActivityOption>();
        var routes = new List<ForecastActivityRoute>(); var outcomes = new List<ForecastActivityOutcome>();
        var workers = new HashSet<string>(StringComparer.Ordinal);
        var operations = new List<ForecastCompositionOperation>();
        var observed = new Dictionary<string, ForecastCompositionSupply>(StringComparer.Ordinal);
        foreach (var group in groups.Take(16))
        {
            var id = "site-" + outcomes.Count;
            var finish = id + "-finish";
            var delivery = id + "-delivery";
            var supplied = group.All(o => o.Count == 0);
            // Every option describes the same single-resource site demand; unobserved/mixed demand cannot be guessed.
            if (!supplied && group.Where(o => o.Count > 0).Select(o => (o.Resource, o.Count, o.Origin)).Distinct().Count() != 1) continue;
            var first = group.First();
            var sourceLocation = ForecastLocations.Of(first.Origin);
            var siteLocation = ForecastLocations.Of(first.Location);
            if (!supplied) activities.Add(new(delivery, sourceLocation, siteLocation, [])
            { Inputs = [new(new(first.Resource), Known((double)first.Count)) { Lot = first.Input }] });
            activities.Add(new(finish, siteLocation, siteLocation, supplied ? [] : [delivery]));
            var ready = "construction-ready/" + id;
            var capability = new Resource("ConstructionReady");
            if (supplied)
                observed[ready] = new(ready, capability, Known(1), siteLocation, Known(0.0), Known((double)horizon))
                { Kind = ForecastRequirementKind.Reusable, Provenance = "observed-site:" + first.Site };
            else
            {
                var source = "construction-source/" + first.Input;
                observed[source] = new(source, new(first.Resource), first.AvailableInput.TryGetValue(out var available)
                    && available >= 0 && available <= int.MaxValue && available == Math.Floor(available) ? Known((int)available) : default,
                    sourceLocation, Known(0.0), Known((double)horizon))
                { Lot = first.Input, Provenance = "native-construction-source:" + first.Site + ":" + first.Input };
                operations.Add(new(activities.Single(a => a.Id == delivery) with { Inputs = [], ChoiceIdentity = "construction-delivery/" + first.Site },
                    [new(delivery + "/material", new(first.Resource), Known(first.Count), sourceLocation,
                        ForecastRequirementKind.Consumed, Known<EquatableArray<string>>([source]), Known(0.0), Known((double)horizon))],
                    [new(ready, capability, Known(1), siteLocation, Known(0.0), Known((double)horizon))
                    { Producer = delivery, Kind = ForecastRequirementKind.Reusable, Provenance = "native-site-delivery:" + first.Site }]));
            }
            operations.Add(new(activities.Single(a => a.Id == finish) with { Prerequisites = [], ChoiceIdentity = "construction-finish/" + first.Site },
                [new(finish + "/ready", capability, Known(1), siteLocation, ForecastRequirementKind.Reusable,
                    Known<EquatableArray<string>>([ready]), Known(0.0), Known((double)horizon))], []));
            foreach (var row in group.GroupBy(o => o.Pawn).Select(g => g.First()))
            {
                workers.Add(row.Pawn);
                if (row.Count > 0 && row.Capacity > 0)
                {
                    options.Add(new(delivery, row.Pawn, row.DeliveryTicks, Known(0.0)));
                    routes.Add(new(row.Pawn, "start", sourceLocation, row.ApproachTicks));
                    bindings[(delivery, row.Pawn)] = new(delivery, row.Pawn, [], PawnActivityKind.Supply,
                        row.Input, "", "", row.Count, deadline, row.Location)
                    { Resource = row.Resource, Source = row.Origin, Container = row.Site, SiteDefinition = row.Definition, Capacity = row.Capacity };
                }
                if (row.FinishTicks.IsKnown)
                {
                    options.Add(new(finish, row.Pawn, row.FinishTicks, Known(0.0)));
                    routes.Add(new(row.Pawn, "start", siteLocation, row.SiteApproachTicks));
                    bindings[(finish, row.Pawn)] = new(finish, row.Pawn, supplied ? [] : [delivery], PawnActivityKind.Work,
                        row.Site, row.WorkGiver, "FinishFrame", 0, deadline, row.Location)
                    { SiteDefinition = row.Definition, CompletionMustBeObserved = true };
                }
            }
            // A floor is a declared service investment (#3047): it relieves kitchen cleanliness and room beauty, so the selector may spend labor on it.
            outcomes.Add(new(finish, "construction-method", finish, Known((double)horizon), default)
            {
                ProgressUnits = Known(1.0),
                Requirements = floors.Contains(first.Definition)
                    ? new([new(FlooringRelief + "/" + first.Site, ProductionNeedPriority.Maintenance, 1.0)], default, default) : null
            });
        }
        // A standing Deconstruct designation (#3557): the room reconcile stood it over an interior ruin or a stale piece, and
        // under voluntary control only an order works it. Each building is the pawn's Work/Deconstruct task on it, one unit
        // of requested development when it comes down (its relief, like a floor's, also exempts it from the thermal gate).
        var removals = read.Removals.Where(o => !resources.Claims.Pawns.Contains(o.Pawn) && !resources.Claims.Targets.Contains(o.Site) && o.FinishTicks.IsKnown)
            .GroupBy(o => o.Site).OrderBy(g => g.Key, StringComparer.Ordinal).Take(17).ToArray();
        foreach (var group in removals.Take(16))
        {
            var id = "removal-" + outcomes.Count;
            var first = group.First();
            var location = ForecastLocations.Of(first.Location);
            activities.Add(new(id, location, location, []));
            // The building stands: like a supplied site's finish, the op needs only its observed readiness to compose.
            var ready = "removal-ready/" + id;
            var capability = new Resource("ConstructionReady");
            observed[ready] = new(ready, capability, Known(1), location, Known(0.0), Known((double)horizon))
            { Kind = ForecastRequirementKind.Reusable, Provenance = "observed-designation:" + first.Site };
            operations.Add(new(activities[^1] with { ChoiceIdentity = "construction-removal/" + first.Site },
                [new(id + "/ready", capability, Known(1), location, ForecastRequirementKind.Reusable,
                    Known<EquatableArray<string>>([ready]), Known(0.0), Known((double)horizon))], []));
            foreach (var row in group.GroupBy(o => o.Pawn).Select(g => g.First()))
            {
                workers.Add(row.Pawn);
                options.Add(new(id, row.Pawn, row.FinishTicks, Known(0.0)));
                routes.Add(new(row.Pawn, "start", location, row.SiteApproachTicks));
                bindings[(id, row.Pawn)] = new(id, row.Pawn, [], PawnActivityKind.Work, row.Site, row.WorkGiver, "Deconstruct", 0, deadline, row.Location)
                { SiteDefinition = row.Definition };
            }
            outcomes.Add(new(id, "construction-method", id, Known((double)horizon), default)
            {
                ProgressUnits = Known(1.0),
                Requirements = new([new(RemovalRelief + "/" + first.Site, ProductionNeedPriority.Maintenance, 1.0)], default, default),
            });
        }
        var capacities = workers.Order(StringComparer.Ordinal).Select(pawn => new ForecastPawnCapacity(pawn, "start",
            resources.Capacity.TryGetValue(pawn, out var capacity) && capacity.Capacity.Windows.TryGetValue(out var windows)
                ? Known(EquatableArray.From(windows.Select(w => w with
                {
                    Start = Math.Max(0, w.Start - (tick - resources.Evidence.Tick.Value)),
                    End = w.End - (tick - resources.Evidence.Tick.Value)
                }).Where(w => w.End > w.Start))) : default, Known(1.0))).ToArray();
        return new(activities.Count == 0 ? null : new()
        {
            Activities = activities.ToArray(),
            Options = options.ToArray(),
            Routes = ForecastLocations.Merge(routes),
            Pawns = capacities,
            Deadline = Known((double)horizon),
            AlternativeLaborCost = Known((double)horizon)
        }, outcomes.ToArray(), bindings,
            groups.Length > 16 || removals.Length > 16)
        { Operations = EquatableArray.From(operations), Observed = EquatableArray.From(observed.Values) };
    }
}
