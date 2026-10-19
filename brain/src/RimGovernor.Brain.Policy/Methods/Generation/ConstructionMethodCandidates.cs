using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy.Methods.Generation;

internal static class ConstructionMethodCandidates
{
    internal const string FlooringRelief = "flooring"; // one relief per floor site, so each laid floor is credited once
    internal const string RemovalRelief = "removal"; // one relief per standing removal designation, so each removal is credited once (#3557)

    /// <summary>
    /// The pawn activity job a standing removal op binds as Work of, by kind (#3612, #3613, #3615); null for a Haul, which binds
    /// as a Deliver of the stack (#3614), and for a kind no order works yet.
    /// </summary>
    internal static string? RemovalJob(PlannedClearingKind kind) => kind switch
    {
        PlannedClearingKind.Deconstruct => "Deconstruct",
        PlannedClearingKind.Uninstall => "Uninstall",
        PlannedClearingKind.Cut => "CutPlant",
        PlannedClearingKind.RemoveFloor => "RemoveFloor",
        PlannedClearingKind.Mine => "Mine",
        _ => null,
    };
    /// <summary>A set kind's op (a Cut wave over plants, #3613; a RemoveFloor wave over cells, #3615; a Mine op, #3616) is bound over its members and needs them named.</summary>
    private static bool Bindable(PlannedSiteClearing op) => op.Kind switch
    {
        PlannedClearingKind.Cut or PlannedClearingKind.Mine => op.Targets.Count > 0,
        PlannedClearingKind.RemoveFloor => op.Cells.Count > 0,
        _ => true,
    };

    /// <summary>A standing removal row a pawn can work: a Haul's delivery is known and one trip (#3614), another kind's finish.</summary>
    private static bool Workable(PlannedClearingKind kind, ConstructionActivityOption row) =>
        kind == PlannedClearingKind.Haul ? OneTrip(row) && row.DeliveryTicks.IsKnown : row.FinishTicks.IsKnown;

    /// <summary>A haul row the Deliver step can work: the whole stack in one carry (the step refuses multi-trip deliveries).</summary>
    internal static bool OneTrip(ConstructionActivityOption row) => row.Count > 0 && row.Capacity >= row.Count;

    /// <summary>
    /// The task a standing removal row binds (#3557, #3612, #3614): a Haul is the pawn's Deliver of the stack (Count the
    /// stack, Destination the row's storage cell), as a support delivery binds; any other kind is Work of the kind's job, a
    /// set op (a Cut wave, #3613; a RemoveFloor wave, #3615) one Work set step over its named <paramref name="targets"/> or
    /// <paramref name="cells"/> (Count the set size). A roof-holder's Deconstruct (#3617) runs a Work/RemoveRoof cell set
    /// step over the op's <paramref name="cells"/> (the roofed cells it leaves unsupported) before the Deconstruct, in the
    /// same order: the same task, relief and output.
    /// </summary>
    internal static ActivityTaskCandidate RemovalBinding(string id, PlannedClearingKind kind, ConstructionActivityOption row, string target, long deadline,
        EquatableArray<string> targets = default, EquatableArray<Cell> cells = default) =>
        kind == PlannedClearingKind.Haul
            ? new(id, row.Pawn, [], PawnActivityKind.Deliver, target, "", "", row.Count, deadline, row.Origin)
                { Resource = row.Resource, Source = row.Origin, Destination = row.Location, Capacity = row.Capacity }
            : kind == PlannedClearingKind.Deconstruct
                ? new(id, row.Pawn, [], PawnActivityKind.Work, target, row.WorkGiver, RemovalJob(kind)!, 0, deadline, row.Location)
                    { SiteDefinition = row.Definition, RoofCells = cells }
                : new(id, row.Pawn, [], PawnActivityKind.Work, target, row.WorkGiver, RemovalJob(kind)!, targets.Count + cells.Count, deadline, row.Location)
                    { SiteDefinition = row.Definition, Targets = targets, Cells = cells };

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
        // A standing removal designation (#3557, #3612, #3613, #3614, #3615): the room reconcile stood a Deconstruct over an
        // interior ruin or a stale piece, an Uninstall over a piece it packs, a Haul over a stack it moves out, CutPlant over a
        // wave of plants or RemoveFloor over a wave of wrong-floor cells, and under voluntary control only an order works it.
        // Each building is the pawn's Work task of the op's kind on it, each stack the pawn's Deliver of it to the row's
        // storage cell, and each Cut or RemoveFloor wave one Work set step over its members, one unit of requested development
        // when it is gone (its relief, like a floor's, also exempts it from the thermal gate). A kind with no job binds nothing.
        var removals = read.Removals.Where(op => (op.Kind == PlannedClearingKind.Haul || RemovalJob(op.Kind) != null) && Bindable(op) && !resources.Claims.Targets.Contains(op.Id))
            .Select(op => (op.Id, op.Kind, op.Targets, op.Cells, Rows: op.Options.Where(o => !resources.Claims.Pawns.Contains(o.Pawn) && Workable(op.Kind, o))
                .GroupBy(o => o.Pawn).Select(g => g.First()).ToArray()))
            .Where(op => op.Rows.Length > 0).OrderBy(op => op.Id, StringComparer.Ordinal).Take(17).ToArray();
        foreach (var op in removals.Take(16))
        {
            var id = "removal-" + outcomes.Count;
            var first = op.Rows[0];
            var haul = op.Kind == PlannedClearingKind.Haul;
            // A haul is worked from the stack's cell; a building's removal at the building.
            var location = ForecastLocations.Of(haul ? first.Origin : first.Location);
            activities.Add(new(id, location, location, []));
            // The building stands: like a supplied site's finish, the op needs only its observed readiness to compose.
            var ready = "removal-ready/" + id;
            var capability = new Resource("ConstructionReady");
            observed[ready] = new(ready, capability, Known(1), location, Known(0.0), Known((double)horizon))
            { Kind = ForecastRequirementKind.Reusable, Provenance = "observed-designation:" + op.Id };
            operations.Add(new(activities[^1] with { ChoiceIdentity = "construction-removal/" + op.Id },
                [new(id + "/ready", capability, Known(1), location, ForecastRequirementKind.Reusable,
                    Known<EquatableArray<string>>([ready]), Known(0.0), Known((double)horizon))], []));
            foreach (var row in op.Rows)
            {
                workers.Add(row.Pawn);
                options.Add(new(id, row.Pawn, haul ? row.DeliveryTicks : row.FinishTicks, Known(0.0)));
                routes.Add(new(row.Pawn, "start", location, haul ? row.ApproachTicks : row.SiteApproachTicks));
                bindings[(id, row.Pawn)] = RemovalBinding(id, op.Kind, row, op.Id, deadline, op.Targets, op.Cells);
            }
            outcomes.Add(new(id, "construction-method", id, Known((double)horizon), default)
            {
                ProgressUnits = Known(1.0),
                Requirements = new([new(RemovalRelief + "/" + op.Id, ProductionNeedPriority.Maintenance, 1.0)], default, default),
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
