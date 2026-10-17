using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

public sealed record ForecastObservedIncumbent(PlanId Plan, PawnActivities Batch, string Commitment, ActivityOwnership Ownership)
{
    public EquatableArray<string> Activities { get; init; }
    public EquatableArray<ActivityOwnership> ReleasedOwnership { get; init; }
}

public static class ForecastIncumbentPreparation
{
    private sealed record IncumbentServiceProjection(string Assignment, string Method, long Revision,
        string Target, string Kind, Cell Location, Fact<double> RemainingTicks, Fact<double> GainPerTick,
        EquatableArray<string> Unsupported);

    /// <summary>Conditional comparison only. The original review's admission claims remain authoritative
    /// until exact native release is observed on a later review.</summary>
    public static (RoundsFacts Facts, EquatableArray<ForecastObservedIncumbent> Incumbents) PrepareCoupledIncumbents(
        RoundsFacts facts, Fact<EquatableArray<ProductionBatch>> batches, WorkActionCensus actions)
    {
        (RoundsFacts Facts, EquatableArray<ForecastObservedIncumbent> Incumbents) Protected() => (ForecastCoupledCandidates.ReviewCoupledActivities(facts), []);
        var original = facts.WorkView;
        var needs = facts.ForecastObserved.Needs;
        if (original is null || needs is null || actions.Truncated || original.Claims.Truncated
            || original.Evidence.Tick != facts.Tick || needs.Tick != facts.Tick.Value
            || facts.ForecastObserved.Coupled?.Tick != needs.Tick
            || !original.Evidence.WorkResources.TryGetValue(out var native)
            || !original.Evidence.Pawns.TryGetValue(out var availability)) return Protected();

        var eligible = new List<(WorkAction Row, IncumbentServiceProjection Service, ActivityOwnership Owner,
            Cell Start, double Wait, double Travel, EquatableArray<ActivityOwnership> Released)>();
        foreach (var row in actions.Actions.OrderBy(a => a.Plan.Value, StringComparer.Ordinal).ThenBy(a => a.Action.Id.Value, StringComparer.Ordinal))
        {
            if (row.Progress.Unresolved || row.Progress.Receipt != Known(Receipt.Accepted)
                || row.Progress.Stage is not (Stage.Completed or Stage.Cancelled) || row.Progress.Tick > facts.Tick
                || row.Action.PawnActivities is not { Tasks.Count: 1 } batch
                || batch.Orders.Count(o => !o.Release) != 1) continue;
            var order = batch.Orders.Single(o => !o.Release); var task = batch.Tasks[0];
            var released = new List<ActivityOwnership>();
            foreach (var tombstone in batch.Orders.Where(o => o.Release))
            {
                var rows = native.Where(p => p.Pawn == tombstone.Pawn).ToArray();
                if (rows.Length != 1 || rows[0].Assignment == tombstone.Assignment) break;
                var actual = rows[0];
                released.Add(new(actual.Pawn, actual.Assignment, "", actual.Revision, actual.State));
            }
            if (released.Count != batch.Orders.Count(o => o.Release)) continue;
            if (order.OutputDelivery is not null
                || order.Task != task.Id || order.Pawn != task.Pawn || task.Prerequisites.Count != 0 || task.Inputs.Count != 0
                || task.Step.Kind is not (PawnActivityKind.Sleep or PawnActivityKind.Recreation)
                || task.Step.Recipe is not null || task.Step.Medicine.Length != 0 || task.Resource.Length != 0
                || task.SupportDemand.Length != 0 || task.SiteDefinition.Length != 0 || task.ProductStuff.Length != 0
                || task.CompletionMustBeObserved || task.Step.Deadline <= facts.Tick.Value) continue;
            var observations = needs.Pawns.Where(p => p.Pawn == order.Pawn).ToArray();
            var physical = native.Where(p => p.Pawn == order.Pawn).ToArray();
            var available = availability.Where(p => p.Pawn == order.Pawn).ToArray();
            if (observations.Length != 1 || physical.Length != 1 || available.Length != 1
                || observations[0].State != "eligible" || observations[0].VoluntaryControlBlockers.Count != 0
                || observations[0].Location is not { } start
                || available[0].Available != Known(true) || available[0].Applies != Known(true)
                || physical[0].CarriedCount != 0 || physical[0].CarriedThing.Length != 0 || physical[0].CarriedResource.Length != 0
                || physical[0].Bill.Length != 0 || physical[0].Unfinished.Length != 0
                || physical[0].Assignment != order.Assignment || physical[0].Revision != order.Revision) continue;
            IncumbentServiceProjection service;
            double wait = 0, travel = 0;
            if (order.Preparing)
            {
                if (physical[0].State != "holding" || task.DepartTick <= facts.Tick.Value
                    || order.Steps.Length != 2 || order.Steps[0].Kind != PawnActivityKind.Approach
                    || order.Steps[0].Destination != task.Location || order.Steps[0].NotBefore != task.DepartTick
                    || order.Steps[1].Kind != PawnActivityKind.Hold
                    || order.Steps.Any(s => s.Deadline != task.Step.Deadline || s.Target.Length != 0
                        || s.Recipe is not null || s.Medicine.Length != 0 || s.Container.Length != 0)) continue;
                var targets = observations[0].Targets.Where(t => t.Id == task.Step.Target).ToArray();
                if (targets.Length != 1 || targets[0].Kind != (task.Step.Kind == PawnActivityKind.Sleep ? "rest" : "joy")
                    || targets[0].Location != task.Location || !targets[0].TravelTicks.TryGetValue(out travel)
                    || !double.IsFinite(travel) || travel < 0) continue;
                wait = task.DepartTick - facts.Tick.Value;
                service = new(order.Assignment, order.Method, order.Revision, task.Step.Target, targets[0].Kind,
                    task.Location, Known(task.Step.Deadline - task.DepartTick - travel), targets[0].GainPerTick,
                    targets[0].Unsupported);
            }
            else
            {
                if (order.Steps.Length != 1 || task.Step != order.Steps[0] || task.Step.Kind != PawnActivityKind.Recreation
                    || physical[0].State != "started" || observations[0].CurrentService is not { Kind: "joy" } current
                    || start != current.Location) continue;
                service = new(current.Assignment, current.Method, current.Revision, current.Target, current.Kind,
                    current.Location, current.RemainingTicks, current.GainPerTick, current.Unsupported);
            }
            if (task.Step.Deadline - facts.Tick.Value > GameTime.TicksPerDay || wait + travel > GameTime.TicksPerDay
                || service.Assignment != order.Assignment || service.Method != order.Method || service.Revision != order.Revision
                || service.Target != task.Step.Target || !service.RemainingTicks.TryGetValue(out var remaining)
                || !service.GainPerTick.TryGetValue(out var rate) || !double.IsFinite(remaining) || remaining <= 0
                || remaining > GameTime.TicksPerDay || remaining > task.Step.Deadline - facts.Tick.Value
                || !double.IsFinite(rate) || rate <= 0 || !double.IsFinite(rate * remaining)) continue;
            eligible.Add((row, service, new(order.Pawn, order.Assignment, order.Method, order.Revision, physical[0].State),
                start, wait, travel, EquatableArray.From(released)));
        }
        // Ambiguous/mixed owners remain protected, including duplicate current journal rows.
        eligible = eligible.Where(e => eligible.Count(x => x.Owner.Pawn == e.Owner.Pawn) == 1).ToList();
        eligible = eligible.Where(e => !WorkRules.WorkClaimsOf(original.Evidence, actions.Actions.Where(a =>
            a.Plan != e.Row.Plan || a.Action.Id != e.Row.Action.Id)).Pawns.Contains(e.Owner.Pawn)).ToList();
        var truncated = eligible.Count > 4;
        var chosen = eligible.Take(4).ToArray();
        if (chosen.Length == 0) return Protected();
        var omitted = chosen.Select(e => (e.Row.Plan, e.Row.Action.Id)).ToHashSet();
        var releasedPawns = chosen.Select(e => e.Owner.Pawn).ToHashSet(StringComparer.Ordinal);
        var evidence = original.Evidence with { Pawns = Known(EquatableArray.From(availability.Select(p =>
            releasedPawns.Contains(p.Pawn) ? p with { Occupied = Known(false) } : p))) };
        var conditional = WorkRules.WorkResourcesOf(evidence, batches,
            actions.Actions.Where(a => !omitted.Contains((a.Plan, a.Action.Id))), actions.Truncated);
        var generated = ForecastCoupledCandidates.GenerateCoupledActivities(facts with { WorkView = conditional, WorkClaims = conditional.Claims });
        if (generated is null) return Protected();
        var incumbents = new List<ForecastObservedIncumbent>();
        var activities = generated.Context.Activities.Activities.ToList();
        var options = generated.Context.Activities.Options.ToList();
        var routes = generated.Context.Activities.Routes.ToDictionary(r => (r.Pawn, r.From, r.To));
        var needTasks = generated.Context.NeedTasks.ToList();
        var waiting = generated.Context.NonproductiveOccupancy.ToList();
        var retained = new List<ForecastCommitment>();
        var findings = new SortedSet<string>(generated.Findings, StringComparer.Ordinal);
        foreach (var entry in chosen)
        {
            var id = "incumbent/" + entry.Row.Plan.Value + "/" + entry.Row.Action.Id.Value;
            var service = entry.Service; var pawn = entry.Owner.Pawn;
            var location = "cell/" + service.Location.X + "/" + service.Location.Z;
            var startLocation = "cell/" + entry.Start.X + "/" + entry.Start.Z;
            var duration = service.RemainingTicks.Value;
            // Native positively observed this stationary participant (serving or waiting).
            // Only routes from that observed start cell can be aliased without another probe.
            foreach (var route in routes.Values.Where(r => r.Pawn == pawn && r.From == "start").ToArray())
                routes.TryAdd((pawn, startLocation, route.To), route with { From = startLocation });
            routes[(pawn, startLocation, "start")] = new(pawn, startLocation, "start", Known(0d));
            var graph = new List<string>();
            if (entry.Wait > 0)
            {
                var waitId = id + "/wait";
                activities.Add(new(waitId, startLocation, startLocation, []) { NotBefore = 0, LatestStart = 0 });
                options.Add(new(waitId, pawn, Known(entry.Wait), Known(0d)));
                waiting.Add(waitId); graph.Add(waitId);
                routes[(pawn, startLocation, location)] = new(pawn, startLocation, location, Known(entry.Travel));
            }
            activities.Add(new(id, location, location, graph.ToArray())
                { NotBefore = entry.Wait + entry.Travel, LatestStart = entry.Wait + entry.Travel });
            graph.Add(id);
            options.Add(new(id, pawn, Known(duration), Known(0d)));
            routes[(pawn, "start", "start")] = new(pawn, "start", "start", Known(0d));
            routes[(pawn, "start", startLocation)] = new(pawn, "start", startLocation, Known(0d));
            routes[(pawn, "start", location)] = new(pawn, "start", location, Known(entry.Travel));
            needTasks.Add(new(id, pawn, service.Kind, service.Target, default, 0, Known(service.GainPerTick.Value * duration), [])
                { MaxPawns = needs.Pawns.SelectMany(p => p.Targets).FirstOrDefault(t => t.Id == service.Target)?.MaxPawns ?? default });
            retained.Add(new(id, []) { ActivityCompletions = [id] });
            incumbents.Add(new(entry.Row.Plan, entry.Row.Action.PawnActivities!, id, entry.Owner)
                { Activities = EquatableArray.From(graph), ReleasedOwnership = entry.Released });
            foreach (var unknown in service.Unsupported) findings.Add("incumbent:" + pawn + ":" + unknown);
        }
        if (truncated) findings.Add("incumbent_preparation_truncated:protected_remaining_owners");
        var prepared = generated with
        {
            Incumbents = EquatableArray.From(retained),
            Findings = EquatableArray.From(findings), Truncated = generated.Truncated || truncated,
            Context = generated.Context with
            {
                NeedTasks = EquatableArray.From(needTasks), Findings = EquatableArray.From(findings),
                NonproductiveOccupancy = EquatableArray.From(waiting),
                Truncated = generated.Context.Truncated || truncated,
                Activities = generated.Context.Activities with { Activities = activities.ToArray(), Options = options.ToArray(), Routes = routes.Values.ToArray() }
            }
        };
        return (facts with { CoupledAlternatives = prepared }, EquatableArray.From(incumbents));
    }
}
