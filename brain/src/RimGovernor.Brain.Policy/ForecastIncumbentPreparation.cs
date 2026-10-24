using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

public sealed record ForecastObservedIncumbent(PlanId Plan, PawnActivities Batch, string Commitment, ActivityOwnership Ownership)
{
    public EquatableArray<string> Activities { get; init; }
    public EquatableArray<ActivityOwnership> ReleasedOwnership { get; init; }
    /// <summary>A productive Method's other reconsidered workers, owned and observed at a boundary with it (#3167).</summary>
    public EquatableArray<ActivityOwnership> Participants { get; init; }
    /// <summary>A productive Method's participants still working, or at a boundary but bound to the reconsidered workers' tasks
    /// (#3624): they keep their orders whatever the review decides.</summary>
    public EquatableArray<ActivityOwnership> Working { get; init; }
    /// <summary>The reconsidered workers' remaining bills. Non-empty marks a reconsidered Method: it has no retain commitment, and
    /// only its reconsidered workers are released, when the selection moves one of these bills to a pawn outside it (#3167, #3624).</summary>
    public EquatableArray<string> Bills { get; init; }
    public IReadOnlyList<ActivityOwnership> Owned => [Ownership, .. Participants, .. Working, .. ReleasedOwnership];
    /// <summary>The workers a reconsidered Method's review decides for: its owner and the other reconsidered participants.</summary>
    public IReadOnlySet<string> Reconsidered => new[] { Ownership }.Concat(Participants).Select(o => o.Pawn).ToHashSet(StringComparer.Ordinal);

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
        string Target, string Kind, Cell Location, double RemainingTicks, double GainPerTick,
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
            || original.Evidence.Tick != facts.Tick || needs.Tick != facts.Tick
            || facts.ForecastObserved.Coupled?.Tick != needs.Tick.Value
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
                || available[0].Available != Known(true) || available[0].Applies != Known(true)
                || physical[0].CarriedCount != 0 || physical[0].CarriedThing.Length != 0 || physical[0].CarriedResource.Length != 0
                || physical[0].Bill.Length != 0 || physical[0].Unfinished.Length != 0
                || physical[0].Assignment != order.Assignment || physical[0].Revision != order.Revision) continue;
            var start = observations[0].Location;
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
                    || targets[0].Location != task.Location || targets[0].Recovery is not NeedRecovery.Rate { GainPerTick: var gain }
                    || !double.IsFinite(travel = targets[0].TravelTicks) || travel < 0) continue;
                wait = task.DepartTick - facts.Tick.Value;
                service = new(order.Assignment, order.Method, order.Revision, task.Step.Target, targets[0].Kind,
                    task.Location, task.Step.Deadline - task.DepartTick - travel, gain,
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
                || service.Target != task.Step.Target || service is not { RemainingTicks: var remaining, GainPerTick: var rate }
                || !double.IsFinite(remaining) || remaining <= 0
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
            // The participants still working keep their claims: the Method as it stands after its reconsidered workers leave.
            var kept = methods.Where(m => m.Working.Length > 0)
                .Select(m => m.Row with { Action = DomainAction.NewPawnActivitiesAction(m.Row.Action.Id, m.Kept) });
            var conditional = WorkRules.WorkResourcesOf(evidence, batches,
                actions.Actions.Where(a => !omitted.Contains((a.Plan, a.Action.Id))).Concat(kept), actions.Truncated);
            return ForecastCoupledCandidates.GenerateCoupledActivities(facts with { WorkView = conditional, WorkClaims = conditional.Claims });
        }
        if (chosen.Length == 0 && methods.Count == 0) return Protected();
        var generated = Generate();
        // A Method is reconsidered only while a pawn outside it is offered its bills at a faster rate than its own cooks:
        // otherwise its owners stay protected and the comparison runs on the claims as admitted.
        if (generated is not null)
        {
            var before = methods.Count;
            methods.RemoveAll(m => !FasterOutside(generated, m));
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
            var duration = service.RemainingTicks;
            // Native positively observed this stationary participant (serving or waiting).
            // Only routes from that observed start cell can be aliased without another probe.
            foreach (var route in routes.Values.Where(r => r.Pawn == pawn && r.From == "start").ToArray())
                routes.TryAdd((pawn, startLocation, route.To), route with { From = startLocation });
            routes[(pawn, startLocation, "start")] = new(pawn, startLocation, "start", new RouteTicks.Reachable(0));
            var graph = new List<string>();
            if (entry.Wait > 0)
            {
                var waitId = id + "/wait";
                activities.Add(new(waitId, startLocation, startLocation, []) { NotBefore = 0, LatestStart = 0 });
                options.Add(new(waitId, pawn, Known(entry.Wait), Known(0d)));
                waiting.Add(waitId); graph.Add(waitId);
                routes[(pawn, startLocation, location)] = new(pawn, startLocation, location, new RouteTicks.Reachable(entry.Travel));
            }
            activities.Add(new(id, location, location, graph.ToArray())
                { NotBefore = entry.Wait + entry.Travel, LatestStart = entry.Wait + entry.Travel });
            graph.Add(id);
            options.Add(new(id, pawn, Known(duration), Known(0d)));
            routes[(pawn, "start", "start")] = new(pawn, "start", "start", new RouteTicks.Reachable(0));
            routes[(pawn, "start", startLocation)] = new(pawn, "start", startLocation, new RouteTicks.Reachable(0));
            routes[(pawn, "start", location)] = new(pawn, "start", location, new RouteTicks.Reachable(entry.Travel));
            needTasks.Add(new(id, pawn, service.Kind, service.Target, default, 0, service.GainPerTick * duration, [])
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
                Working = EquatableArray.From(method.Working), ReleasedOwnership = method.Released, Bills = method.Bills,
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
                IncumbentHold = ForecastIncumbentHold.Of(incumbents),
                Truncated = generated.Context.Truncated || truncated,
                Activities = generated.Context.Activities with { Activities = activities.ToArray(), Options = options.ToArray(), Routes = routes.Values.ToArray() }
            }
        };
        return (facts with { CoupledAlternatives = prepared }, EquatableArray.From(incumbents));
    }

    private sealed record BoundaryMethod(WorkAction Row, PawnActivities Batch, ActivityOwnership[] Owners, ActivityOwnership[] Working,
        EquatableArray<ActivityOwnership> Released, EquatableArray<string> Bills, PawnActivities Kept);

    /// <summary>A coupled Method with bills still to cook and any worker between tasks (its task completed, or holding for the
    /// next) observed exactly at its order, carrying nothing (#3167, #3624). Such a worker is reconsidered alone: the review
    /// decides who takes its next task, and the participants still working keep theirs. A worker some other participant's
    /// task waits on (a prerequisite, an input it produces, or a bill both hold) is reconsidered only with that participant,
    /// so releasing it strands nobody. Nothing reconsidered is in flight, so a release interrupts no recipe; the bills say what
    /// moving the work would mean.</summary>
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
            var rows = new List<ActivityOwnership>();
            var boundary = new HashSet<string>(StringComparer.Ordinal);
            foreach (var order in orders)
            {
                var physical = native.Where(p => p.Pawn == order.Pawn).ToArray();
                if (physical.Length != 1 || physical[0].Assignment != order.Assignment || physical[0].Revision != order.Revision) break;
                var p = physical[0];
                rows.Add(new(order.Pawn, order.Assignment, order.Method, order.Revision, p.State));
                if ((order.Preparing ? p.State == "holding" : p.State == "completed")
                    && p.CarriedCount == 0 && p.CarriedThing.Length == 0 && p.CarriedResource.Length == 0
                    && p.Bill.Length == 0 && p.Unfinished.Length == 0) boundary.Add(order.Pawn);
            }
            if (rows.Count != orders.Length) continue;
            // A worker stays with the Method while a participant kept working needs one of its tasks.
            static string? Bill(SelectedActivityTask t) => t.Step.Recipe?.Bill is { Length: > 0 } bill ? bill : null;
            for (var changed = true; changed;)
            {
                changed = false;
                foreach (var pawn in boundary.ToArray())
                {
                    var own = batch.Tasks.Where(t => t.Pawn == pawn).ToArray();
                    var ids = own.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
                    var bills = own.Where(t => !done.Contains(t.Id)).Select(Bill).OfType<string>().ToHashSet(StringComparer.Ordinal);
                    if (!batch.Tasks.Any(t => !boundary.Contains(t.Pawn) && (t.Prerequisites.Any(ids.Contains)
                        || t.Inputs.Any(i => i.Producers.Any(ids.Contains)) || Bill(t) is { } held && bills.Contains(held)))) continue;
                    changed |= boundary.Remove(pawn);
                }
            }
            var reconsidered = batch.Tasks.Where(t => boundary.Contains(t.Pawn) && !done.Contains(t.Id)).Select(Bill).OfType<string>()
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (reconsidered.Length == 0) continue;
            var released = new List<ActivityOwnership>();
            foreach (var tombstone in batch.Orders.Where(o => o.Release))
            {
                var tombstones = native.Where(p => p.Pawn == tombstone.Pawn).ToArray();
                if (tombstones.Length != 1 || tombstones[0].Assignment == tombstone.Assignment) break;
                released.Add(new(tombstones[0].Pawn, tombstones[0].Assignment, "", tombstones[0].Revision, tombstones[0].State));
            }
            if (released.Count != batch.Orders.Count(o => o.Release)) continue;
            var others = WorkRules.WorkClaimsOf(evidence, actions.Actions.Where(a => a.Plan != row.Plan || a.Action.Id != row.Action.Id)).Pawns;
            if (boundary.Any(others.Contains)) continue;
            if (Methods.Binding.CoordinatedActivityBinding.Release(batch, boundary) is not { } kept) continue;
            methods.Add(new(row, batch, rows.Where(r => boundary.Contains(r.Pawn)).ToArray(), rows.Where(r => !boundary.Contains(r.Pawn)).ToArray(),
                EquatableArray.From(released), EquatableArray.From(reconsidered), kept));
        }
        return methods;
    }

    /// <summary>Whether a pawn other than the Method's cooks is offered one of its bills' recipes faster than the best of them
    /// (#3167): its total ticks, travelling from where it stands to the ingredients, carrying them to the bench and cooking, are
    /// lower, or no higher with a lower chance the meal is poisoned (the game's rule, read natively per cook and bench). A pawn
    /// offered no single haul of the whole ingredient count is timed walking straight to the bench.</summary>
    private static bool FasterOutside(ForecastCoupledCandidates generated, BoundaryMethod method)
    {
        var own = ForecastObservedIncumbent.CooksOf(method.Batch, method.Bills);
        var context = generated.Context;
        var options = context.Activities.Options.Where(o => o.WorkTicks.TryGetValue(out var ticks) && double.IsFinite(ticks) && ticks >= 0)
            .GroupBy(o => (o.Activity, o.Pawn)).ToDictionary(g => g.Key, g => g.First().WorkTicks.Value);
        var routes = context.Activities.Routes.Where(r => r.Ticks is RouteTicks.Reachable(var ticks) && double.IsFinite(ticks))
            .GroupBy(r => (r.Pawn, r.From, r.To)).ToDictionary(g => g.Key, g => ((RouteTicks.Reachable)g.First().Ticks).Ticks);
        double? Route(string pawn, Cell to) => routes.TryGetValue((pawn, "start", Methods.Generation.ForecastLocations.Of(to)), out var ticks) ? ticks : null;
        var offered = new List<(string Pawn, double Total, Fact<double> Poison)>();
        foreach (var cook in context.Bindings.Values.Where(b => b.Kind == PawnActivityKind.Recipe
            && b.Recipe?.Bill is { } bill && method.Bills.Contains(bill)))
        {
            if (!options.TryGetValue((cook.Id, cook.Pawn), out var work) || work <= 0) continue;
            var needed = cook.Recipe?.Ingredients.Sum(i => i.Count) ?? 0;
            var fetch = context.Bindings.Values.Where(b => b.Pawn == cook.Pawn && b.Kind == PawnActivityKind.Deliver
                    && b.Resource == cook.Resource && b.Destination == cook.Source && b.Source is { } && b.Count >= needed)
                .Select(b => options.TryGetValue((b.Id, b.Pawn), out var haul) && Route(b.Pawn, b.Source!.Value) is { } approach ? approach + haul : (double?)null)
                .Where(t => t is not null).Min() ?? Route(cook.Pawn, cook.Location);
            if (fetch is null) continue;
            offered.Add((cook.Pawn, fetch.Value + work, cook.FoodPoisonChance));
        }
        var mine = offered.Where(o => own.Contains(o.Pawn)).OrderBy(o => o.Total)
            .ThenBy(o => o.Poison.TryGetValue(out var p) ? p : double.PositiveInfinity).ToArray();
        if (mine.Length == 0) return offered.Any(o => !own.Contains(o.Pawn));
        var best = mine[0];
        return offered.Any(o => !own.Contains(o.Pawn) && (o.Total < best.Total || o.Total <= best.Total
            && o.Poison.TryGetValue(out var poison) && best.Poison.TryGetValue(out var incumbent) && poison < incumbent));
    }
}
