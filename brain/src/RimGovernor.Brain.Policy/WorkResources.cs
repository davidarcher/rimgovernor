namespace RimGovernor.Brain.Policy;

public sealed record WorkBuildingCost(string Definition, string Stuff, Fact<EquatableArray<Amount>> Materials);
public sealed record WorkPawnAvailability(string Pawn, Fact<bool> Available, Fact<bool> Applies,
    Fact<bool> Occupied, Fact<string> Target)
{
    public Fact<EquatableArray<string>> Schedule { get; init; }
    public Fact<double> LocalHour { get; init; }
}
public sealed record WorkAction(PlanId Plan, DomainAction Action, ProgressView Progress);
public sealed record WorkActionCensus(EquatableArray<WorkAction> Actions, bool Truncated = false);
public sealed record WorkResourceClaims(EquatableArray<ForecastCommitment> Materials,
    EquatableArray<string> Pawns, EquatableArray<string> Targets, EquatableArray<string> Findings)
{
    public bool Truncated { get; init; }
}
public sealed record WorkResourceView(WorkAdmissionEvidence Evidence, WorkResourceClaims Claims,
    Fact<IReadOnlyDictionary<Resource, Fact<double>>> Materials)
{
    public IReadOnlyDictionary<string, ForecastCapacityResult> Capacity { get; init; } = new Dictionary<string, ForecastCapacityResult>();
}

public static partial class PolicyFunctions
{
    public static Fact<EquatableArray<ForecastCommitment>> CreditCarriedBillInputs(Fact<EquatableArray<ForecastCommitment>> promises,
        Fact<EquatableArray<RimGovernor.Contracts.Wire.Observations.WorkResourcePawn>> observation)
    {
        if (!promises.TryGetValue(out var rows)) return default;
        return Known(EquatableArray.From(rows.Select(c => c with
        {
            MaterialClaims = EquatableArray.From(c.MaterialClaims.Select(m =>
        {
            if (!m.ReservationBill.TryGetValue(out var bill) || bill.Length == 0 || !m.Units.TryGetValue(out var units)) return m;
            return m with
            {
                Units = observation.TryGetValue(out var native)
                ? Known(Math.Max(0, units - native.Where(n => n.Bill == bill && n.CarriedResource == m.Resource.Value).Sum(n => (double)n.CarriedCount))) : default
            };
        }))
        })));
    }
    public static ForecastCapacityResult WorkCapacityOf(WorkResourceView view, string pawn, double horizon, bool protectTimetable = true)
    {
        var observed = view.Evidence.Pawns.TryGetValue(out var rows) ? rows.SingleOrDefault(p => p.Pawn == pawn) : null;
        var work = new WorkPawn
        {
            Id = new(pawn),
            Available = observed?.Available ?? default,
            Applies = observed?.Applies ?? default,
            Schedule = observed?.Schedule ?? default
        };
        return ForecastPawnCapacityOf(new()
        {
            Pawn = work,
            ProtectTimetable = protectTimetable,
            Location = "start",
            HorizonTicks = horizon,
            LocalHour = observed?.LocalHour ?? default,
            OpportunityCostPerTick = Known(1.0),
            CurrentWork = new(work.Id, observed?.Occupied ?? default, default,
                observed is { Occupied.IsKnown: true, Occupied.Value: false } ? Known(0.0) : default),
            Occupancy = view.Claims.Truncated ? default : Known<EquatableArray<ForecastOccupancy>>(view.Claims.Pawns.Contains(pawn)
                ? [new("journal", pawn, Known(0.0), default, ForecastOccupancyState.Active)] : [])
        });
    }

    public static RoundsFacts ReviewWorkResources(RoundsFacts facts, Fact<EquatableArray<ProductionBatch>> batches,
        WorkActionCensus actions)
    {
        var evidence = WorkAdmissionEvidenceOf(facts, batches);
        var view = WorkResourcesOf(evidence, batches, actions.Actions, actions.Truncated);
        var claims = view.Claims;
        var production = evidence.ForecastPromises;
        var reviewed = facts with
        {
            ProductionPromises = !actions.Truncated && production.TryGetValue(out var rows) ? Known(MergeWorkMaterialPromises(rows.Concat(claims.Materials))) : default,
            WorkClaims = claims,
            WorkView = view,
            NeedsAlternatives = ReviewNeedsAlternatives(facts.ForecastObserved.Needs, view),
        };
        return reviewed;
    }

    // A productive iteration is part of its owning fixed bill's remaining promise, not another batch.
    // Independent (unlinked) promises remain additive; only exact bill/resource identity permits overlap.
    public static EquatableArray<ForecastCommitment> MergeWorkMaterialPromises(IEnumerable<ForecastCommitment> rows)
    {
        var commitments = rows.ToArray();
        var result = commitments.Select(c => c with
        {
            MaterialClaims = EquatableArray.From(c.MaterialClaims
            .Where(m => !m.ReservationBill.TryGetValue(out var bill) || bill.Length == 0))
        }).ToList();
        foreach (var bill in commitments.SelectMany(c => c.MaterialClaims)
            .Where(c => c.ReservationBill.TryGetValue(out var id) && id.Length != 0).GroupBy(c => c.ReservationBill.Value))
            result.Add(new("shared-bill/" + bill.Key, default)
            {
                MaterialClaims = EquatableArray.From(bill.GroupBy(c => c.Resource)
                .Select(g => new ForecastMaterialClaim(g.Key, g.All(c => c.Units.IsKnown) ? Known(g.Max(c => c.Units.Value)) : default, Known(bill.Key))))
            });
        return EquatableArray.From(result);
    }
    /// <summary>Project commitments from the journal; no native writes and no independently mutable claims.</summary>
    public static WorkResourceClaims WorkClaimsOf(WorkAdmissionEvidence evidence, IEnumerable<WorkAction> actions)
    {
        var materials = new List<ForecastCommitment>();
        var activityMaterials = new Dictionary<(string Pawn, string Assignment), List<ForecastMaterialClaim>>();
        var pawns = new HashSet<string>(StringComparer.Ordinal);
        var targets = new HashSet<string>(StringComparer.Ordinal);
        var findings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in actions.OrderBy(a => a.Action.Id.Value, StringComparer.Ordinal))
        {
            var progress = row.Progress;
            var absent = !progress.Unresolved && (progress.Attempt.Value == 0 && progress.Stage is Stage.Cancelled or Stage.Unsuccessful
                || progress.Effect.TryGetValue(out var effect) && effect == Effect.Absent
                || progress.Receipt.TryGetValue(out var receipt) && receipt == Receipt.Refused);
            if (absent) continue;
            if (row.Action.ProductionBill is { Mode: BillMode.GearBatch } bill
                && evidence.ForecastPromises.TryGetValue(out var promises)
                && (progress.Attempt.Value == 0 || progress.Tick > evidence.Tick || promises.Any(p => p.Id == "production/" + row.Action.Id.Value)))
            {
                targets.Add(bill.Bench);
                if (bill.Worker.Length != 0) pawns.Add(bill.Worker);
            }
            if (row.Action.Building is { } building && row.Action.ConstructionTarget.Length == 0)
            {
                // Current sites already contribute their outstanding quantities through ConstructionDeficit.
                if (evidence.Standing.TryGetValue(out var standing) && standing.Contains(building)) continue;
                if (!progress.Unresolved && progress.Stage is Stage.Completed or Stage.Cancelled or Stage.Unsuccessful && progress.Tick < evidence.Tick
                    && evidence.Standing.IsKnown) continue;
                targets.Add(BuildingClaimTarget(building));
                var cost = evidence.Buildings.TryGetValue(out var costs)
                    ? costs.SingleOrDefault(c => c.Definition == building.Definition && c.Stuff == building.Stuff) : null;
                if (cost is null || !cost.Materials.TryGetValue(out var units))
                {
                    findings.Add("unknown_building_materials:" + row.Action.Id.Value);
                    continue;
                }
                materials.Add(new("work/" + row.Action.Id.Value, default)
                { MaterialClaims = EquatableArray.From(units.Select(a => new ForecastMaterialClaim(a.Resource, Known((double)a.Count), Known("")))) });
            }
            if (row.Action.PawnActivities is not { } batch) continue;
            foreach (var task in batch.Tasks)
            {
                if (task.Step.Destination is { } destination) targets.Add("cell/" + destination.X + "/" + destination.Z);
                if (task.Step.Container.Length != 0) targets.Add(task.Step.Container);
                if (task.SiteDefinition.Length != 0 && task.Step.Target.Length != 0) targets.Add(task.Step.Target);
                if (task.Step.Recipe is { } recipe)
                {
                    targets.Add(task.Step.Target);
                    if (recipe.Bill.Length != 0) targets.Add(recipe.Bill);
                    if (recipe.Unfinished.Length != 0) targets.Add(recipe.Unfinished);
                }
            }
            foreach (var order in batch.Orders)
            {
                // Accepted release ends ownership. A pending release must retain its predecessor's claim.
                if (order.Release) continue;
                if (progress.Attempt.Value > 0 && !progress.Unresolved && progress.Tick < evidence.Tick
                    && evidence.WorkResources.TryGetValue(out var observedOwners)
                    && observedOwners.Any(n => n.Pawn == order.Pawn && (n.Assignment != order.Assignment || n.Revision != order.Revision))) continue;
                pawns.Add(order.Pawn);
                var observed = evidence.WorkResources.TryGetValue(out var native)
                    ? native.SingleOrDefault(p => p.Pawn == order.Pawn && p.Assignment == order.Assignment && p.Revision == order.Revision) : null;
                foreach (var step in order.Steps)
                {
                    if (step.Destination is { } destination) targets.Add("cell/" + destination.X + "/" + destination.Z);
                    if (step.Target.Length != 0) targets.Add(step.Target);
                    if (step.Container.Length != 0) targets.Add(step.Container);
                    if (step.Recipe is { } cooking) targets.Add(cooking.Bill);
                }
                if (order.OutputDelivery?.Recipe is { } continuation)
                { targets.Add(continuation.Bench); targets.Add(continuation.Bill); }
                var claims = new List<ForecastMaterialClaim>();
                foreach (var step in order.Steps)
                {
                    if (step.Kind == PawnActivityKind.Deliver && order.OutputDelivery is { } output)
                    {
                        // Moving stock protects its use, not a second physical consumption. Carried units are outside loose stock.
                        var input = observed ?? (progress.Attempt.Value == 0 && evidence.WorkResources.TryGetValue(out var inputs)
                            ? inputs.SingleOrDefault(p => p.Pawn == order.Pawn && p.Assignment == order.Assignment
                                && p.Revision == order.ExpectedRevision && p.State is "completed" or "failed") : null);
                        var carried = input?.CarriedResource == output.Resource && (observed is not null || input.CarriedThing == step.Target) ? input.CarriedCount : 0;
                        if (observed?.State == "completed") continue;
                        claims.Add(new(new(output.Resource), Known((double)Math.Max(0, step.Count - carried)), Known("")));
                    }
                    if (step.Recipe is { } cooking)
                    {
                        if (observed?.State is "completed" or "failed") continue; // Fresh stock owns any remaining/consumed ingredients.
                        if (observed is { Unfinished.Length: > 0 }) continue; // Actual UFT owns consumed ingredients outside loose stock.
                        if (cooking.Resource.Length == 0) { findings.Add("unknown_cooking_materials:" + row.Action.Id.Value); continue; }
                        var carried = observed?.CarriedResource == cooking.Resource ? observed.CarriedCount : 0;
                        claims.Add(new(new(cooking.Resource), Known((double)Math.Max(0, cooking.Ingredients.Sum(i => i.Count) - carried)), Known(cooking.Bill)));
                    }
                }
                if (claims.Count != 0)
                {
                    var key = (order.Pawn, order.Assignment);
                    if (!activityMaterials.TryGetValue(key, out var held)) activityMaterials[key] = held = [];
                    // The migrated material path binds one delivery/cooking step. Repeated uses inside
                    // one revision need a dependency-aware consumption estimate, not max or sum guesses.
                    held.AddRange(claims.GroupBy(c => c.Resource).Select(g => g.Count() == 1 ? g.First()
                        : new ForecastMaterialClaim(g.Key, default, default)));
                }
            }
        }
        foreach (var (owner, claims) in activityMaterials)
            materials.Add(new("activity/" + owner.Assignment + "/" + owner.Pawn, default)
            {
                MaterialClaims = EquatableArray.From(claims.GroupBy(c => c.Resource).Select(g => new ForecastMaterialClaim(g.Key,
                g.All(c => c.Units.IsKnown) ? Known(g.Max(c => c.Units.Value)) : default,
                g.Select(c => c.ReservationBill).Distinct().Count() == 1 ? g.First().ReservationBill : default)))
            });
        return new(EquatableArray.From(materials), EquatableArray.From(pawns.Order(StringComparer.Ordinal)),
            EquatableArray.From(targets.Order(StringComparer.Ordinal)), EquatableArray.From(findings.Order(StringComparer.Ordinal)));
    }

    public static string BuildingClaimTarget(Building building) => "cell/" + building.Cell.X + "/" + building.Cell.Z;

    public static WorkResourceView WorkResourcesOf(WorkAdmissionEvidence evidence, Fact<EquatableArray<ProductionBatch>> batches,
        IEnumerable<WorkAction> actions, bool truncated = false)
    {
        var claims = WorkClaimsOf(evidence, actions) with { Truncated = truncated };
        var materials = ProductionAdmissionBudget(evidence, batches, claims.Materials);
        // An unpriced commitment can consume any material. Keep pawn/target accounting usable independently.
        if (claims.Findings.Count != 0 || truncated) materials = default;
        var view = new WorkResourceView(evidence, claims, materials);
        return view with
        {
            Capacity = evidence.Pawns.TryGetValue(out var roster)
            ? roster.ToDictionary(p => p.Pawn, p => WorkCapacityOf(view, p.Pawn, 7 * GameTime.TicksPerDay), StringComparer.Ordinal) : new Dictionary<string, ForecastCapacityResult>()
        };
    }

    public static bool WorkResourcesFit(WorkResourceView view, PlanSpec proposed, IReadOnlySet<string>? retainedParticipants = null)
    {
        if (view.Claims.Truncated) return false;
        var exclusive = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in proposed.Actions)
        {
            var parts = action.PawnActivities is { } batch
                ? batch.Orders.Select(o => DomainAction.NewPawnActivitiesAction(action.Id, batch with { Orders = [o], Tasks = [] })) : [action];
            foreach (var part in parts)
            {
                var claim = WorkClaimsOf(view.Evidence, [new(proposed.Id, part, new ProgressView { Action = part.Id, Stage = Stage.Pending })]);
                if (claim.Targets.Any(t => !exclusive.Add(t))) return false;
            }
        }
        var candidate = WorkClaimsOf(view.Evidence, proposed.Actions.Select(a => new WorkAction(proposed.Id, a,
            new ProgressView { Action = a.Id, Stage = Stage.Pending })));
        if (candidate.Findings.Count != 0 || candidate.Pawns.Any(view.Claims.Pawns.Contains)
            || candidate.Targets.Any(view.Claims.Targets.Contains)) return false;
        var usesBench = proposed.Actions.Any(a => a.ProductionBill is { Mode: BillMode.GearBatch }
            || a.PawnActivities is { } activity && activity.Orders.Any(o => o.Steps.Any(s => s.Recipe is not null) || o.OutputDelivery?.Recipe is not null));
        if (usesBench && (!view.Evidence.WorkResources.IsKnown || !view.Evidence.Pawns.TryGetValue(out var benchPawns)
            || benchPawns.Any(p => !p.Occupied.IsKnown || p.Occupied.Value && !p.Target.IsKnown))) return false;
        foreach (var pawn in candidate.Pawns)
        {
            if (!view.Evidence.Pawns.TryGetValue(out var roster)) return false;
            var observed = roster.SingleOrDefault(p => p.Pawn == pawn);
            if (observed is null || !observed.Available.TryGetValue(out var available) || !available
                || !observed.Applies.TryGetValue(out var applies) || !applies) return false;
            if (retainedParticipants?.Contains(pawn) != true && proposed.Actions.SelectMany(a => a.PawnActivities?.Orders ?? []).Any(o => o.Pawn == pawn
                && o.Steps.Any(s => s.Kind is PawnActivityKind.Work or PawnActivityKind.Deliver or PawnActivityKind.Recipe or PawnActivityKind.Approach or PawnActivityKind.Supply))
                && (!view.Capacity.TryGetValue(pawn, out var capacity) || !capacity.Capacity.Windows.TryGetValue(out var windows)
                    || !windows.Any(w => w.Start == 0 && w.End > 0))) return false;
        }
        if (view.Evidence.Pawns.TryGetValue(out var nativePawns)
            && nativePawns.Any(p => p.Target.TryGetValue(out var target) && target.Length != 0
                && candidate.Targets.Contains(target) && !candidate.Pawns.Contains(p.Pawn))) return false;
        if (view.Evidence.WorkResources.TryGetValue(out var allPawns)
            && allPawns.Any(p => p.Target.Length != 0 && candidate.Targets.Contains(p.Target) && !candidate.Pawns.Contains(p.Pawn))) return false;
        var bills = proposed.Actions.Where(a => a.ProductionBill is { Mode: BillMode.GearBatch })
            .Select(a => new ProductionBatch(a.ProductionBill!, new ProgressView { Action = a.Id, Stage = Stage.Pending }));
        var production = ProductionPromisesOf(PriceProductionBatches(Known(EquatableArray.From(bills)), view.Evidence.Recipes), default);
        if (!production.TryGetValue(out var promises)) return false;
        var needed = candidate.Materials.Concat(promises).SelectMany(c => c.MaterialClaims).GroupBy(c => c.Resource);
        foreach (var group in needed)
        {
            var credits = group.Where(c => c.ReservationBill.TryGetValue(out var id) && id.Length != 0)
                .GroupBy(c => c.ReservationBill.Value).Select(g => Methods.Evaluation.WorkMaterialEvaluation.ExistingBillCredit(view, g.Key, group.Key,
                    g.All(c => c.Units.IsKnown) ? g.Sum(c => c.Units.Value) : 0)).ToArray();
            if (group.Any(c => !c.Units.IsKnown) || credits.Any(c => !c.IsKnown) || !view.Materials.TryGetValue(out var budget)
                || !budget.GetValueOrDefault(group.Key, Known(0.0)).TryGetValue(out var free)
                || group.Sum(c => c.Units.Value) > free + credits.Sum(c => c.Value)) return false;
        }
        return true;
    }

    public static WorkAdmissionEvidence WorkContinuationEvidence(WorkAdmissionEvidence evidence, PawnActivities batch,
        IReadOnlyList<ActivityOwnership>? observed = null)
    {
        if (!evidence.Pawns.TryGetValue(out var pawns) || !evidence.WorkResources.TryGetValue(out var native)) return evidence;
        return evidence with
        {
            Pawns = Known(EquatableArray.From(pawns.Select(p => batch.Orders.Any(o => o.Pawn == p.Pawn
            && (observed is not null ? observed.Any(n => MatchesActivityOwner(o, n)
                && (n.State is "completed" or "failed" || o.Preparing && n.State == "holding"))
                : native.Any(n => n.Pawn == o.Pawn && n.Assignment == o.Assignment && n.Revision == o.Revision
                    && (n.State is "completed" or "failed" || o.Preparing && n.State == "holding"))))
            ? p with { Occupied = Known(false) } : p)))
        };
    }
}
