using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

public sealed record ForecastObservedIncumbent(PlanId Plan, PawnActivities Batch, string Commitment, ActivityOwnership Ownership)
{
    public EquatableArray<string> Activities { get; init; }
    public EquatableArray<ActivityOwnership> ReleasedOwnership { get; init; }
    /// <summary>A productive Method's other participants, owned and observed at a boundary with it (#3167).</summary>
    public EquatableArray<ActivityOwnership> Participants { get; init; }
    /// <summary>A productive Method's remaining bills. Non-empty marks a reconsidered Method: it has no retain commitment, and
    /// it is released only when the selection moves one of these bills to a pawn outside it (#3167).</summary>
    public EquatableArray<string> Bills { get; init; }
    /// <summary>A Method that only defers its pawns, offered beside a reconsidered Method: released when the selection gives one
    /// of its pawns work (#3167).</summary>
    public bool Deferral { get; init; }
    public IReadOnlyList<ActivityOwnership> Owned => [Ownership, .. Participants, .. ReleasedOwnership];

    /// <summary>The pawns a batch has cooking one of <paramref name="bills"/>.</summary>
    public static HashSet<string> CooksOf(PawnActivities batch, IEnumerable<string> bills)
    {
        var set = bills.ToHashSet(StringComparer.Ordinal);
        return batch.Tasks.Where(t => t.Step.Recipe is { } recipe && set.Contains(recipe.Bill)).Select(t => t.Pawn).ToHashSet(StringComparer.Ordinal);
    }
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
        var servicePawns = chosen.Select(e => e.Owner.Pawn).ToHashSet(StringComparer.Ordinal);
        var methods = BoundaryMethods(facts, actions, original.Evidence, native)
            .Where(m => !m.Owners.Any(o => servicePawns.Contains(o.Pawn))).ToList();
        ForecastCoupledCandidates? Generate()
        {
            var omitted = chosen.Select(e => (e.Row.Plan, e.Row.Action.Id)).Concat(methods.Select(m => (m.Row.Plan, m.Row.Action.Id))).ToHashSet();
            var releasedPawns = servicePawns.Concat(methods.SelectMany(m => m.Owners.Select(o => o.Pawn))).ToHashSet(StringComparer.Ordinal);
            var evidence = original.Evidence with { Pawns = Known(EquatableArray.From(availability.Select(p =>
                releasedPawns.Contains(p.Pawn) ? p with { Occupied = Known(false) } : p))) };
            var conditional = WorkRules.WorkResourcesOf(evidence, batches,
                actions.Actions.Where(a => !omitted.Contains((a.Plan, a.Action.Id))), actions.Truncated);
            return ForecastCoupledCandidates.GenerateCoupledActivities(facts with { WorkView = conditional, WorkClaims = conditional.Claims });
        }
        // A deferred pawn is offered only beside a reconsidered Method: alone, its deferral stands until it is due.
        if (!methods.Any(m => m.Bills.Count > 0)) methods.Clear();
        if (chosen.Length == 0 && methods.Count == 0) return Protected();
        var generated = Generate();
        // A Method is reconsidered only while a pawn outside it is offered its bills at a faster rate than its own cooks:
        // otherwise its owners stay protected and the comparison runs on the claims as admitted.
        if (generated is not null)
        {
            var before = methods.Count;
            methods.RemoveAll(m => m.Bills.Count > 0 && !FasterOutside(generated, m));
            if (!methods.Any(m => m.Bills.Count > 0)) methods.Clear();
            if (methods.Count != before) generated = chosen.Length == 0 && methods.Count == 0 ? null : Generate();
        }
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
        foreach (var method in methods)
            incumbents.Add(new(method.Row.Plan, method.Batch, "method/" + method.Row.Plan.Value + "/" + method.Row.Action.Id.Value, method.Owners[0])
            {
                Activities = EquatableArray.From(method.Batch.Tasks.Select(t => t.Id)), Participants = EquatableArray.From(method.Owners.Skip(1)),
                ReleasedOwnership = method.Released, Bills = method.Bills, Deferral = method.Bills.Count == 0,
            });
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

    private sealed record BoundaryMethod(WorkAction Row, PawnActivities Batch, ActivityOwnership[] Owners,
        EquatableArray<ActivityOwnership> Released, EquatableArray<string> Bills);

    /// <summary>A coupled Method with bills still to cook, every participant between tasks (its task completed, holding for
    /// the next, or deferred) and observed exactly at its order, carrying nothing (#3167). Nothing is in flight, so a release
    /// interrupts no recipe; the bills say what moving the work would mean. A Method that only defers its pawns has no bills: it
    /// is offered beside such a Method, so a deferred colonist can take the work.</summary>
    private static List<BoundaryMethod> BoundaryMethods(RoundsFacts facts, WorkActionCensus actions, WorkAdmissionEvidence evidence,
        EquatableArray<WorkResourcePawn> native)
    {
        var methods = new List<BoundaryMethod>();
        foreach (var row in actions.Actions.OrderBy(a => a.Plan.Value, StringComparer.Ordinal).ThenBy(a => a.Action.Id.Value, StringComparer.Ordinal))
        {
            if (row.Progress.Unresolved || row.Progress.Receipt != Known(Receipt.Accepted)
                || row.Progress.Stage is not (Stage.Completed or Stage.Cancelled) || row.Progress.Tick > facts.Tick
                || row.Action.PawnActivities is not { Tasks.Count: > 0 } batch) continue;
            var orders = batch.Orders.Where(o => !o.Release).ToArray();
            if (orders.Length == 0 || orders.Any(o => !o.Method.StartsWith("coupled-", StringComparison.Ordinal))) continue;
            var done = orders.Where(o => !o.Preparing).Select(o => o.Task).ToHashSet(StringComparer.Ordinal);
            var bills = batch.Tasks.Where(t => !done.Contains(t.Id)).Select(t => t.Step.Recipe?.Bill).OfType<string>()
                .Where(b => b.Length != 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (bills.Length == 0 && !orders.All(PawnActivities.IsDeferral)) continue;
            var owners = new List<ActivityOwnership>();
            foreach (var order in orders)
            {
                var physical = native.Where(p => p.Pawn == order.Pawn).ToArray();
                if (physical.Length != 1) break;
                var p = physical[0];
                if (p.Assignment != order.Assignment || p.Revision != order.Revision
                    || !(PawnActivities.IsDeferral(order) ? p.State != "failed" : order.Preparing ? p.State == "holding" : p.State == "completed")
                    || p.CarriedCount != 0 || p.CarriedThing.Length != 0 || p.CarriedResource.Length != 0
                    || p.Bill.Length != 0 || p.Unfinished.Length != 0) break;
                owners.Add(new(order.Pawn, order.Assignment, order.Method, order.Revision, p.State));
            }
            if (owners.Count != orders.Length) continue;
            var released = new List<ActivityOwnership>();
            foreach (var tombstone in batch.Orders.Where(o => o.Release))
            {
                var rows = native.Where(p => p.Pawn == tombstone.Pawn).ToArray();
                if (rows.Length != 1 || rows[0].Assignment == tombstone.Assignment) break;
                released.Add(new(rows[0].Pawn, rows[0].Assignment, "", rows[0].Revision, rows[0].State));
            }
            if (released.Count != batch.Orders.Count(o => o.Release)) continue;
            var others = WorkRules.WorkClaimsOf(evidence, actions.Actions.Where(a => a.Plan != row.Plan || a.Action.Id != row.Action.Id)).Pawns;
            if (owners.Any(o => others.Contains(o.Pawn))) continue;
            methods.Add(new(row, batch, owners.ToArray(), EquatableArray.From(released), EquatableArray.From(bills)));
        }
        return methods;
    }

    /// <summary>Whether a pawn other than the Method's cooks is offered one of its bills' recipes in fewer work ticks than any
    /// of them.</summary>
    private static bool FasterOutside(ForecastCoupledCandidates generated, BoundaryMethod method)
    {
        var own = ForecastObservedIncumbent.CooksOf(method.Batch, method.Bills);
        var cooks = generated.Context.Bindings.Values.Where(b => b.Kind == PawnActivityKind.Recipe
            && (b.Recipe?.Bill ?? b.Cooking?.Continuation.Bill) is { } bill && method.Bills.Contains(bill)).Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        var offered = generated.Context.Activities.Options.Where(o => cooks.Contains(o.Activity)
            && o.WorkTicks.TryGetValue(out var ticks) && double.IsFinite(ticks) && ticks > 0).ToArray();
        var best = offered.Where(o => own.Contains(o.Pawn)).Select(o => o.WorkTicks.Value).DefaultIfEmpty(double.PositiveInfinity).Min();
        return offered.Any(o => !own.Contains(o.Pawn) && o.WorkTicks.Value < best);
    }
}
