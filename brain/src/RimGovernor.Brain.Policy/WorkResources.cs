namespace RimGovernor.Brain.Policy;

public sealed record WorkBuildingCost(string Definition, string Stuff, EquatableArray<Amount> Materials)
{
    /// <summary>The definition's required North footprint (x by z).</summary>
    public required Cell Size { get; init; }
}
public sealed record WorkPawnAvailability(string Pawn, bool Available, bool Applies,
    bool Occupied, string Target)
{
    public EquatableArray<string> Schedule { get; init; }
    public double LocalHour { get; init; }
    /// <summary>The pawn runs a preemptible vanilla job (#3670): it holds <see cref="Target"/>, but a coupled order replaces it.</summary>
    public bool Preemptible { get; init; }
}
public sealed record WorkAction(PlanId Plan, DomainAction Action, ProgressView Progress);
public sealed record WorkActionCensus(EquatableArray<WorkAction> Actions, bool Truncated = false);
/// <summary>An observed target's claimant capacity (vanilla ReservationManager maxPawns) and, for a stack, its quantity.</summary>
public sealed record WorkTargetCapacity(string Target, int MaxPawns, TargetQuantity Quantity);
/// <summary>The units claims on a capacity target sum against (#3895): a stack's count, or none for a bed or a recreation building.</summary>
public abstract record TargetQuantity
{
    private TargetQuantity() { }
    public sealed record Units(double Count) : TargetQuantity;
    public sealed record Unbounded : TargetQuantity;
    public static TargetQuantity Of(RimGovernor.Contracts.Wire.Observations.NeedStock stock) =>
        stock is RimGovernor.Contracts.Wire.Observations.NeedStock.Stacked stacked ? new Units(stacked.Count) : new Unbounded();
}
/// <summary>One owner's claim on a capacity target; <see cref="Count"/> is the stack units it takes (0 for a bed or a pin).
/// A stack use holds the target from <see cref="From"/> to <see cref="Until"/> (#3626); any other claim holds it throughout.</summary>
public sealed record WorkTargetClaim(string Target, string Owner, string Task, int Count)
{
    public long From { get; init; } = long.MinValue;
    public long Until { get; init; } = long.MaxValue;
    /// <summary>A haul from the stack: native reserves it for one pawn, so no other owner may hold it meanwhile.</summary>
    public bool Alone { get; init; }
    internal TargetUse Use => new(Owner, Count, From, Until, Alone);
}

/// <summary>One claim on a capacity target as <see cref="TargetUseRules"/> reads it (#3626): its owner, the stack units it
/// takes, the half-open ticks [From, Until) it holds the target, and whether it holds it alone.</summary>
public readonly record struct TargetUse(string Owner, int Count, long From, long Until, bool Alone);

/// <summary>
/// The one rule for whether claims on one capacity target conflict (#3626), read by the forecast's coupled selection and by
/// work resource admission alike, so the forecast never selects a pairing admission refuses. Units are consumed, so the
/// claims' summed count must fit the stack whatever their times. Owners are native reservations, held from departure to
/// the task's end: at no tick may more distinct owners hold the target than it admits, and a haul (ReservationManager
/// maxPawns 1) shares no tick with another owner. A claim with no schedule holds the target throughout.
/// </summary>
public static class TargetUseRules
{
    /// <summary>A use scheduled from <paramref name="depart"/> to <paramref name="end"/> ticks after <paramref name="tick"/>, held
    /// over the same whole ticks the bound task's <see cref="SelectedActivityTask.DepartTick"/> and
    /// <see cref="SelectedActivityTask.UseUntilTick"/> carry.</summary>
    public static TargetUse Scheduled(string owner, int count, long tick, double depart, double end, bool alone) =>
        new(owner, count, checked(tick + (long)Math.Ceiling(depart)), SelectedActivityTask.UseUntil(tick, depart, end), alone);

    public static bool Conflict(ReadOnlySpan<TargetUse> uses, int maxPawns, TargetQuantity quantity) =>
        UnitsConflict(uses, quantity) || OwnersConflict(uses, maxPawns);

    /// <summary>Whether the uses take more units than a stack of <paramref name="quantity"/> holds.</summary>
    public static bool UnitsConflict(ReadOnlySpan<TargetUse> uses, TargetQuantity quantity)
    {
        if (quantity is not TargetQuantity.Units(var units)) return false;
        double sum = 0;
        foreach (var use in uses) sum += use.Count;
        return sum > units;
    }

    /// <summary>Whether at some tick more distinct owners hold the target than <paramref name="maxPawns"/>, or an alone use
    /// shares a tick with another owner.</summary>
    public static bool OwnersConflict(ReadOnlySpan<TargetUse> uses, int maxPawns)
    {
        for (var i = 0; i < uses.Length; i++)
        {
            var a = uses[i];
            // The most owners at once is reached where some use begins.
            var owners = 0;
            for (var j = 0; j < uses.Length; j++)
            {
                var b = uses[j];
                if (b.From > a.From || Until(b) <= a.From) continue;
                var seen = false;
                for (var k = 0; k < j && !seen; k++)
                    seen = uses[k].Owner == b.Owner && uses[k].From <= a.From && Until(uses[k]) > a.From;
                if (!seen) owners++;
            }
            if (owners > maxPawns) return true;
            if (!a.Alone) continue;
            for (var j = 0; j < uses.Length; j++)
                if (uses[j].Owner != a.Owner && uses[j].From < Until(a) && a.From < Until(uses[j])) return true;
        }
        return false;
    }

    private static long Until(TargetUse use) => use.Until > use.From ? use.Until : use.From + 1;
}
public sealed record WorkResourceClaims(EquatableArray<ForecastCommitment> Materials,
    EquatableArray<string> Pawns, EquatableArray<string> Targets, EquatableArray<string> Findings)
{
    public bool Truncated { get; init; }
    /// <summary>Claims on sleep, recreation and observed food targets. Each such target is also in <see cref="Targets"/>
    /// (generators avoid every claimed target); admission shares it up to its capacity instead of refusing a second owner.</summary>
    public EquatableArray<WorkTargetClaim> Shared { get; init; }
}
public sealed record WorkResourceView(WorkAdmissionEvidence Evidence, WorkResourceClaims Claims,
    MaterialAvailability Materials)
{
    public IReadOnlyDictionary<string, ForecastCapacityResult> Capacity { get; init; } = new Dictionary<string, ForecastCapacityResult>();
}

public static partial class WorkRules
{
    public static ProductionPromiseSet CreditCarriedBillInputs(ProductionPromiseSet promises,
        EquatableArray<RimGovernor.Contracts.Wire.Observations.WorkResourcePawn> observation)
    {
        if (promises is not ProductionPromiseSet.Assessed(var rows)) return promises;
        return new ProductionPromiseSet.Assessed(EquatableArray.From(rows.Select(c => c with
        {
            MaterialClaims = EquatableArray.From(c.MaterialClaims.Select(m =>
            {
                if (m.ReservationBill.Length == 0 || m.Units is not MaterialUnits.Exact(var units)) return m;
                return m with { Units = new MaterialUnits.Exact(Math.Max(0, units - observation
                    .Where(n => n.Bill == m.ReservationBill && n.CarriedResource == m.Resource.Value).Sum(n => (double)n.CarriedCount))) };
            }))
        })));
    }
    public static ForecastCapacityResult WorkCapacityOf(WorkResourceView view, string pawn, double horizon, bool protectTimetable = true)
    {
        var observed = view.Evidence.Pawns.SingleOrDefault(p => p.Pawn == pawn);
        var protection = view.Claims.Truncated ? (ForecastProtection)new ForecastProtection.Truncated() : new ForecastProtection.Read(view.Claims.Pawns.Contains(pawn)
            ? [new("journal", pawn, 0, 0, ForecastOccupancyState.Untimed)] : []);
        return ForecastCapacity.ForecastPawnCapacityOf(ForecastCapacity.InputOf(pawn, observed, horizon, protection, protectTimetable));
    }
    public static RoundsFacts ReviewWorkResources(RoundsFacts facts, ProductionBatchCensus batches,
        WorkActionCensus actions)
    {
        var evidence = ProductionRules.WorkAdmissionEvidenceOf(facts, batches);
        var view = WorkResourcesOf(evidence, batches, actions.Actions, actions.Truncated);
        // An open formation hunt's retriever is held for its trip: not drafted, so no journal action names it, yet not free for other voluntary work (#3513).
        var retrievers = HuntPlanningRules.HuntRequestRetrievers(facts.FoodPlan);
        if (retrievers.Count > 0)
            view = view with { Claims = view.Claims with { Pawns = EquatableArray.From(view.Claims.Pawns.Concat(retrievers.Select(r => r.Value)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) } };
        var claims = view.Claims;
        var production = evidence.Promises;
        var reviewed = facts with
        {
            ProductionPromises = actions.Truncated ? new ProductionPromiseSet.Blocked(ProductionPromiseBlock.TruncatedWork)
                : production is ProductionPromiseSet.Assessed(var rows) ? new ProductionPromiseSet.Assessed(MergeWorkMaterialPromises(rows.Concat(claims.Materials))) : production,
            WorkClaims = claims,
            WorkView = view,
            NeedsAlternatives = ForecastNeeds.ReviewNeedsAlternatives(facts.ForecastObserved.Needs, view),
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
            .Where(m => m.ReservationBill.Length == 0))
        }).ToList();
        foreach (var bill in commitments.SelectMany(c => c.MaterialClaims)
            .Where(c => c.ReservationBill.Length != 0).GroupBy(c => c.ReservationBill))
            result.Add(new("shared-bill/" + bill.Key, default)
            {
                MaterialClaims = EquatableArray.From(bill.GroupBy(c => c.Resource)
                .Select(g => new ForecastMaterialClaim(g.Key, g.All(c => c.Units is MaterialUnits.Exact) ? new MaterialUnits.Exact(g.Max(c => ((MaterialUnits.Exact)c.Units).Count)) : new MaterialUnits.Open(), bill.Key)))
            });
        return EquatableArray.From(result);
    }
    /// <summary>
    /// Project commitments from the journal; no native writes and no independently mutable claims. A building
    /// claims its anchor cell, or with <paramref name="footprint"/> every cell of its footprint
    /// (<see cref="BuildingClaimTargets"/>).
    /// </summary>
    /// <summary>A committed work action that owns nothing: cancelled or unsuccessful before any attempt, observed absent, or refused.</summary>
    public static bool ActionAbsent(ProgressView progress) =>
        !progress.Unresolved
        && (
            progress.Attempt.Value == 0 && progress.Stage is Stage.Cancelled or Stage.Unsuccessful
            || progress.Observes(Effect.Absent)
            || progress.Received(Receipt.Refused)
        );

    public static WorkResourceClaims WorkClaimsOf(WorkAdmissionEvidence evidence, IEnumerable<WorkAction> actions, bool footprint = false,
        bool ordersOnly = false)
    {
        var materials = new List<ForecastCommitment>();
        var activityMaterials = new Dictionary<(string Pawn, string Assignment), List<ForecastMaterialClaim>>();
        var selectedMaterials = new Dictionary<(string Owner, string Task, Resource Resource, string Bill), double>();
        var pawns = new HashSet<string>(StringComparer.Ordinal);
        var targets = new HashSet<string>(StringComparer.Ordinal);
        var exclusive = new HashSet<string>(StringComparer.Ordinal);
        var shared = new Dictionary<(string Target, string Owner, string Task), (int Count, long From, long Until, bool Alone)>();
        var findings = new HashSet<string>(StringComparer.Ordinal);
        var stacks = evidence.NeedTargets.Where(t => t.Quantity is TargetQuantity.Units).Select(t => t.Target).ToHashSet(StringComparer.Ordinal);
        // A target some claim takes whole stays exclusive, whatever else shares it.
        void Take(string target) { targets.Add(target); exclusive.Add(target); }
        // The same task seen through its order and through its task row is one claim, not two. A stack use holds the stack
        // over its task's schedule (#3626): an eat, or a haul from it, which holds it alone.
        void Share(string target, string owner, string task, int count, PawnActivities batch, PawnActivityStep step, bool alone)
        {
            targets.Add(target);
            var key = (target, owner, task);
            var (from, until) = (long.MinValue, long.MaxValue);
            if ((alone || step.Kind == PawnActivityKind.Eat) && batch.Tasks.FirstOrDefault(t => t.Id == task) is { } row)
                (from, until) = (row.DepartTick, row.UseUntilTick > row.DepartTick ? row.UseUntilTick
                    : step.Deadline > row.DepartTick ? step.Deadline : long.MaxValue);
            shared[key] = (Math.Max(shared.GetValueOrDefault(key).Count, count), from, until, alone);
        }
        // A haul of an observed food stack shares it (#3626); any other named source is taken whole.
        bool HaulsStack(PawnActivityStep step, string target) => step.Kind is PawnActivityKind.Deliver or PawnActivityKind.Supply && stacks.Contains(target);
        foreach (var row in actions.OrderBy(a => a.Action.Id.Value, StringComparer.Ordinal))
        {
            var progress = row.Progress;
            if (ActionAbsent(progress)) continue;
            if (row.Action.ProductionBill is { Mode: BillMode.GearBatch } bill
                && evidence.Promises is ProductionPromiseSet.Assessed(var promises)
                && (progress.Attempt.Value == 0 || progress.Tick > evidence.Tick || promises.Any(p => p.Id == "production/" + row.Action.Id.Value)))
            {
                Take(bill.Bench);
                if (bill.Worker.Length != 0) pawns.Add(bill.Worker);
            }
            if (row.Action.Building is { } building && row.Action.ConstructionTarget.Length == 0)
            {
                // Placement has become an observed optional site. Its selected supply activity owns remaining materials.
                if (evidence.Standing is { } standing && standing.Buildings.Contains(building)) continue;
                if (!progress.Unresolved && progress.Stage is Stage.Completed or Stage.Cancelled or Stage.Unsuccessful && progress.Tick < evidence.Tick
                    && evidence.Standing is not null) continue;
                if (footprint) foreach (var cell in MethodClaimsRules.BuildingClaimTargets(evidence, building)) Take(cell);
                else Take(BuildingClaimTarget(building));
                var cost = evidence.Buildings.SingleOrDefault(c => c.Definition == building.Definition && c.Stuff == building.Stuff);
                if (cost is null)
                {
                    findings.Add("unknown_building_materials:" + row.Action.Id.Value);
                    continue;
                }
                materials.Add(new("work/" + row.Action.Id.Value, default)
                { MaterialClaims = EquatableArray.From(cost.Materials.Select(a => new ForecastMaterialClaim(a.Resource, new MaterialUnits.Exact(a.Count), ""))) });
            }
            if (row.Action.PawnActivities is not { } batch) continue;
            foreach (var task in ordersOnly ? Enumerable.Empty<SelectedActivityTask>() : batch.Tasks)
            {
                var owner = batch.Orders.FirstOrDefault(o => o.Pawn == task.Pawn && !o.Release);
                if (owner is not null && progress.Attempt.Value > 0 && !progress.Unresolved && progress.Tick < evidence.Tick
                    && evidence.WorkResources is var owners && owners.Any(p => p.Pawn == owner.Pawn
                        && (p.Assignment != owner.Assignment || p.Revision != owner.Revision))) continue;
                // Preparation owns the selected future service too, not only its approach cell.
                // Produced food keeps its unresolved identity until native output rebinding.
                var sharesService = SharesCapacity(task.Step);
                if (sharesService) Share(task.Step.Target, task.Pawn, task.Id, EatCount(task.Step), batch, task.Step, false);
                foreach (var input in task.Inputs.Where(i => i.Observed && i.Lot.Length != 0 && !(sharesService && i.Lot == task.Step.Target)))
                    if (HaulsStack(task.Step, input.Lot)) Share(input.Lot, task.Pawn, task.Id, input.Count, batch, task.Step, true);
                    else Take(input.Lot);
                if (task.Step is CellActivityStep { Destination: var destination }) Take("cell/" + destination.X + "/" + destination.Z);
                if (task.Step.Container.Length != 0) Take(task.Step.Container);
                var executing = batch.Orders.FirstOrDefault(o => !o.Release && !o.Preparing && o.Task == task.Id);
                var actual = executing is not null && evidence.WorkResources is var actualWorkers
                    ? actualWorkers.SingleOrDefault(p => p.Pawn == executing.Pawn && p.Assignment == executing.Assignment && p.Revision == executing.Revision) : null;
                var partial = actual?.State == "completed" && task.Step.Kind is PawnActivityKind.Deliver or PawnActivityKind.Supply
                    && executing!.Steps[0].Count < task.Step.Count;
                if ((actual?.State is not ("completed" or "failed") || partial) && actual is not { Unfinished.Length: > 0 })
                {
                    // Reserve the physical input frontier, not every downstream handoff. Producer
                    // completion makes an obligation claimable; it never adds predicted stock.
                    bool Resolved(string producer)
                    {
                        var source = batch.Tasks.SingleOrDefault(t => t.Id == producer);
                        if (source is null) return true;
                        if (source.CompletionMustBeObserved) return false;
                        var order = batch.Orders.FirstOrDefault(o => !o.Release && !o.Preparing && o.Task == producer);
                        if (order is not null && source.Step.Kind is PawnActivityKind.Deliver or PawnActivityKind.Supply
                            && order.Steps[0].Count < source.Step.Count) return false;
                        return order is not null && evidence.WorkResources is var workers
                            && workers.Any(p => p.Pawn == order.Pawn && p.Assignment == order.Assignment
                                && p.Revision == order.Revision && p.State == "completed");
                    }
                    foreach (var group in task.Inputs.Where(i => i.Observed || i.Producers.Count > 0 && i.Producers.All(Resolved)).GroupBy(i => i.Resource))
                    {
                        var required = group.Sum(i => i.Count);
                        if (task.Step.Kind is PawnActivityKind.Deliver or PawnActivityKind.Supply)
                            required = Math.Min(required, task.Step.Count) - (partial ? executing!.Steps[0].Count : 0);
                        var carried = actual;
                        if (carried is null && executing is not null && progress.Attempt.Value == 0
                            && evidence.WorkResources is var previousWorkers)
                            carried = previousWorkers.SingleOrDefault(p => p.Pawn == executing.Pawn && p.Assignment == executing.Assignment
                                && p.Revision == executing.ExpectedRevision && p.State is "completed" or "failed"
                                && p.CarriedThing == executing.Steps[0].Target);
                        var count = Math.Max(0, required - (carried?.CarriedResource == group.Key ? carried.CarriedCount : 0));
                        var key = (batch.Orders.FirstOrDefault()?.Method ?? row.Plan.Value, task.Id, new Resource(group.Key), task.Step.Recipe?.Bill ?? "");
                        selectedMaterials[key] = Math.Max(selectedMaterials.GetValueOrDefault(key), count);
                    }
                }
                if (task.SiteDefinition.Length != 0 && task.Step.Target.Length != 0) Take(task.Step.Target);
                if (task.Step.Recipe is { } recipe)
                {
                    Take(task.Step.Target);
                    if (recipe.Bill.Length != 0) Take(recipe.Bill);
                    if (recipe.Unfinished.Length != 0) Take(recipe.Unfinished);
                }
            }
            foreach (var order in batch.Orders)
            {
                // Accepted release ends ownership. A pending release must retain its predecessor's claim.
                if (order.Release) continue;
                if (progress.Attempt.Value > 0 && !progress.Unresolved && progress.Tick < evidence.Tick
                    && evidence.WorkResources is var observedOwners
                    && observedOwners.Any(n => n.Pawn == order.Pawn && (n.Assignment != order.Assignment || n.Revision != order.Revision))) continue;
                pawns.Add(order.Pawn);
                var observed = evidence.WorkResources is var native
                    ? native.SingleOrDefault(p => p.Pawn == order.Pawn && p.Assignment == order.Assignment && p.Revision == order.Revision) : null;
                foreach (var step in order.Steps)
                {
                    if (step is CellActivityStep { Destination: var destination }) Take("cell/" + destination.X + "/" + destination.Z);
                    if (SharesCapacity(step)) Share(step.Target, order.Pawn, order.Task, EatCount(step), batch, step, false);
                    else if (HaulsStack(step, step.Target)) Share(step.Target, order.Pawn, order.Task, step.Count, batch, step, true);
                    else if (step.Target.Length != 0) Take(step.Target);
                    if (step.Container.Length != 0) Take(step.Container);
                    if (step.Recipe is { } cooking) Take(cooking.Bill);
                }
                if (order.OutputDelivery?.Recipe is { } continuation)
                { Take(continuation.Bench); Take(continuation.Bill); }
                var claims = new List<ForecastMaterialClaim>();
                if (batch.Tasks.Any(t => t.Id == order.Task && t.Inputs.Count > 0)) continue;
                foreach (var step in order.Steps)
                {
                    if (step.Medicine.Length != 0 && step.Recipe is null && observed?.State is not ("completed" or "failed"))
                    {
                        var task = batch.Tasks.SingleOrDefault(t => t.Id == order.Task);
                        if (task is null || task.Inputs.Count == 0) findings.Add("unknown_selected_care_materials:" + row.Action.Id.Value);
                        else claims.AddRange(task.Inputs.Select(i => new ForecastMaterialClaim(new(i.Resource),
                            new MaterialUnits.Exact(Math.Max(0, i.Count - (observed?.CarriedResource == i.Resource ? observed.CarriedCount : 0))), "")));
                    }
                    if (step.Kind == PawnActivityKind.Deliver && order.OutputDelivery is { } output)
                    {
                        // Moving stock protects its use, not a second physical consumption. Carried units are outside loose stock.
                        var input = observed ?? (progress.Attempt.Value == 0 && evidence.WorkResources is var inputs
                            ? inputs.SingleOrDefault(p => p.Pawn == order.Pawn && p.Assignment == order.Assignment
                                && p.Revision == order.ExpectedRevision && p.State is "completed" or "failed") : null);
                        var carried = input?.CarriedResource == output.Resource && (observed is not null || input.CarriedThing == step.Target) ? input.CarriedCount : 0;
                        if (observed?.State == "completed") continue;
                        claims.Add(new(new(output.Resource), new MaterialUnits.Exact(Math.Max(0, step.Count - carried)), ""));
                    }
                    if (step.Recipe is { } cooking)
                    {
                        if (observed?.State is "completed" or "failed") continue; // Fresh stock owns any remaining/consumed ingredients.
                        if (observed is { Unfinished.Length: > 0 }) continue; // Actual UFT owns consumed ingredients outside loose stock.
                        if (cooking.Resource.Length == 0) { findings.Add("unknown_cooking_materials:" + row.Action.Id.Value); continue; }
                        var carried = observed?.CarriedResource == cooking.Resource ? observed.CarriedCount : 0;
                        claims.Add(new(new(cooking.Resource), new MaterialUnits.Exact(Math.Max(0, cooking.Ingredients.Sum(i => i.Count) - carried)), cooking.Bill));
                    }
                }
                if (claims.Count != 0)
                {
                    var key = (order.Pawn, order.Assignment);
                    if (!activityMaterials.TryGetValue(key, out var held)) activityMaterials[key] = held = [];
                    // The migrated material path binds one delivery/cooking step. Repeated uses inside
                    // one revision need a dependency-aware consumption estimate, not max or sum guesses.
                    held.AddRange(claims.GroupBy(c => c.Resource).Select(g => g.Count() == 1 ? g.First()
                        : new ForecastMaterialClaim(g.Key, new MaterialUnits.Open(), "")));
                }
            }
        }
        foreach (var group in selectedMaterials.GroupBy(p => (p.Key.Owner, p.Key.Task)))
            materials.Add(new("activity/selected-input/" + group.Key.Owner + "/" + group.Key.Task, [])
            { MaterialClaims = EquatableArray.From(group.Select(p => new ForecastMaterialClaim(p.Key.Resource, new MaterialUnits.Exact(p.Value), p.Key.Bill))) });
        foreach (var (owner, claims) in activityMaterials)
            materials.Add(new("activity/" + owner.Assignment + "/" + owner.Pawn, default)
            {
                MaterialClaims = EquatableArray.From(claims.GroupBy(c => c.Resource).Select(g => new ForecastMaterialClaim(g.Key,
                g.All(c => c.Units is MaterialUnits.Exact) && g.Select(c => c.ReservationBill).Distinct().Count() == 1 ? new MaterialUnits.Exact(g.Max(c => ((MaterialUnits.Exact)c.Units).Count)) : new MaterialUnits.Open(),
                g.Select(c => c.ReservationBill).Distinct().Count() == 1 ? g.First().ReservationBill : "")))
            });
        return new(EquatableArray.From(materials), EquatableArray.From(pawns.Order(StringComparer.Ordinal)),
            EquatableArray.From(targets.Order(StringComparer.Ordinal)), EquatableArray.From(findings.Order(StringComparer.Ordinal)))
        {
            Shared = EquatableArray.From(shared.Where(s => !exclusive.Contains(s.Key.Target))
                .Select(s => new WorkTargetClaim(s.Key.Target, s.Key.Owner, s.Key.Task, s.Value.Count)
                    { From = s.Value.From, Until = s.Value.Until, Alone = s.Value.Alone })
                .OrderBy(c => c.Target, StringComparer.Ordinal).ThenBy(c => c.Owner, StringComparer.Ordinal).ThenBy(c => c.Task, StringComparer.Ordinal))
        };
    }

    // Preparation owns the selected future service too, not only its approach cell. Produced food keeps its
    // unresolved identity until native output rebinding.
    private static bool SharesCapacity(PawnActivityStep step) => step.Target.Length != 0
        && (step.Kind is PawnActivityKind.Sleep or PawnActivityKind.Recreation
            || step.Kind == PawnActivityKind.Eat && step.Target != "observed-output");

    private static int EatCount(PawnActivityStep step) => step.Kind == PawnActivityKind.Eat ? step.Count : 0;

    /// <summary>True when <paramref name="claims"/> on one target conflict by <see cref="TargetUseRules"/>: more owners at once
    /// than its MaxPawns (a target the need read does not name admits one, as a vanilla reservation does), a haul beside another owner, or, for a
    /// stack, more units than it holds. <paramref name="otherOwners"/> are pawns outside the claims that
    /// already occupy it, holding it throughout.</summary>
    private static bool TargetOverCapacity(WorkAdmissionEvidence evidence, string target, IEnumerable<WorkTargetClaim> claims, IEnumerable<string> otherOwners)
    {
        var capacity = evidence.NeedTargets.FirstOrDefault(t => t.Target == target);
        var uses = claims.Select(c => c.Use).Concat(otherOwners.Select(o => new TargetUse(o, 0, long.MinValue, long.MaxValue, false))).ToArray();
        return TargetUseRules.Conflict(uses, capacity?.MaxPawns ?? 1, capacity?.Quantity ?? new TargetQuantity.Unbounded());
    }

    /// <summary>
    /// The observed need target as another Method may still propose it against <paramref name="claims"/> (#3194): an
    /// unclaimed target whole; a claimed food stack narrowed to the units and claimant slots the committed claims leave,
    /// its observed count less the units they still owe; null for any other claimed target, a stack some claim takes
    /// whole or a committed haul holds alone (#3626: the forecast does not read the committed claims' times), or a
    /// remainder that is unknown or exhausted. The owed units are the journal's remaining Eat tasks, which
    /// continuation drops once native completion is observed, never predicted consumption; admission then shares the
    /// stack by the same arithmetic (<see cref="TargetOverCapacity"/>).
    /// </summary>
    public static RimGovernor.Contracts.Wire.Observations.NeedTarget? ProposableNeedTarget(WorkResourceClaims claims,
        RimGovernor.Contracts.Wire.Observations.NeedTarget target)
    {
        if (!claims.Targets.Contains(target.Id)) return target;
        if (target.Kind != "food" || target.Stock is not RimGovernor.Contracts.Wire.Observations.NeedStock.Stacked(var stock, var freshness)) return null;
        var held = claims.Shared.Where(s => s.Target == target.Id).ToArray();
        if (held.Length == 0 || held.Any(s => s.Alone)) return null;
        var units = stock - held.Sum(s => s.Count);
        var owners = target.MaxPawns - held.Select(s => s.Owner).Distinct(StringComparer.Ordinal).Count();
        return units > 0 && owners > 0
            ? target with { AvailableCount = Math.Min(target.AvailableCount, units), Stock = new RimGovernor.Contracts.Wire.Observations.NeedStock.Stacked(units, freshness), MaxPawns = owners }
            : null;
    }

    public static string BuildingClaimTarget(Building building) => "cell/" + building.Cell.X + "/" + building.Cell.Z;

    public static WorkResourceView WorkResourcesOf(WorkAdmissionEvidence evidence, ProductionBatchCensus batches,
        IEnumerable<WorkAction> actions, bool truncated = false)
    {
        var claims = WorkClaimsOf(evidence, actions) with { Truncated = truncated };
        var materials = ProductionRules.ProductionAdmissionBudget(evidence, batches, claims.Materials);
        // An unpriced commitment can consume any material. Keep pawn/target accounting usable independently.
        if (claims.Findings.Count != 0 || truncated) materials = new MaterialAvailability.Blocked(MaterialBudgetBlock.WorkClaims);
        var view = new WorkResourceView(evidence, claims, materials);
        return view with
        {
            Capacity = evidence.Pawns.ToDictionary(p => p.Pawn, p => WorkCapacityOf(view, p.Pawn, 7 * GameTime.TicksPerDay), StringComparer.Ordinal)
        };
    }

    public static bool WorkResourcesFit(WorkResourceView view, PlanSpec proposed, IReadOnlySet<string>? retainedParticipants = null) =>
        WorkResourcesRefusal(view, proposed, retainedParticipants) is null;

    /// <summary>The first reason <paramref name="proposed"/> does not fit the committed work and observed evidence, or null when it fits.</summary>
    public static string? WorkResourcesRefusal(WorkResourceView view, PlanSpec proposed, IReadOnlySet<string>? retainedParticipants = null)
    {
        if (view.Claims.Truncated) return "claims_truncated";
        var exclusive = new HashSet<string>(StringComparer.Ordinal);
        var sharedSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in proposed.Actions)
        {
            // Each order alone, with its own task only to time its stack claims (#3626).
            var parts = action.PawnActivities is { } batch
                ? batch.Orders.Select(o => DomainAction.NewPawnActivitiesAction(action.Id, batch with { Orders = [o], Tasks = o.Release ? []
                    : EquatableArray.From(batch.Tasks.Where(t => t.Id == o.Task && t.Pawn == o.Pawn).Select(t => t with { Prerequisites = [] })) })) : [action];
            foreach (var part in parts)
            {
                var claim = WorkClaimsOf(view.Evidence, [new(proposed.Id, part, new ProgressView { Action = part.Id, Stage = Stage.Pending })], ordersOnly: true);
                // An order's cell is where its pawn walks or stands (approach, hold, a stockpile drop), not a thing
                // only one pawn may use: orders of one batch may meet at a cell. Named things (stacks, containers,
                // bills, benches, sites) and a building's own cell stay exclusive. A capacity target (a bed, a pin,
                // an observed food stack) is shared up to its capacity below, unless another claim takes it whole.
                var sharedHere = claim.Shared.Select(s => s.Target).ToHashSet(StringComparer.Ordinal);
                var exclusiveTargets = (action.PawnActivities is null ? claim.Targets : claim.Targets.Where(t => !t.StartsWith("cell/", StringComparison.Ordinal)))
                    .Where(t => !sharedHere.Contains(t));
                if (exclusiveTargets.FirstOrDefault(t => !exclusive.Add(t) || sharedSeen.Contains(t)) is { } taken) return "shared_target:" + taken;
                if (sharedHere.FirstOrDefault(exclusive.Contains) is { } contested) return "shared_target:" + contested;
                sharedSeen.UnionWith(sharedHere);
            }
        }
        var candidate = WorkClaimsOf(view.Evidence, proposed.Actions.Select(a => new WorkAction(proposed.Id, a,
            new ProgressView { Action = a.Id, Stage = Stage.Pending })));
        var capacityTargets = candidate.Shared.Select(s => s.Target).ToHashSet(StringComparer.Ordinal);
        if (capacityTargets.FirstOrDefault(t => TargetOverCapacity(view.Evidence, t, candidate.Shared.Where(s => s.Target == t), [])) is { } crowded)
            return "shared_target:" + crowded;
        if (candidate.Findings.Count != 0) return "unknown_claims:" + string.Join(",", candidate.Findings);
        if (candidate.Pawns.FirstOrDefault(view.Claims.Pawns.Contains) is { } held) return "pawn_claimed:" + held;
        var committedShared = view.Claims.Shared.Select(s => s.Target).ToHashSet(StringComparer.Ordinal);
        if (candidate.Targets.FirstOrDefault(t => view.Claims.Targets.Contains(t) && !(capacityTargets.Contains(t) && committedShared.Contains(t))) is { } blocked)
            return "target_claimed:" + blocked;
        if (capacityTargets.FirstOrDefault(t => TargetOverCapacity(view.Evidence, t,
            view.Claims.Shared.Concat(candidate.Shared).Where(s => s.Target == t), [])) is { } full) return "target_claimed:" + full;
        foreach (var pawn in candidate.Pawns)
        {            var observed = view.Evidence.Pawns.SingleOrDefault(p => p.Pawn == pawn);
            if (observed is null || !observed.Available || !observed.Applies) return "pawn_unavailable:" + pawn;
            if (retainedParticipants?.Contains(pawn) != true && proposed.Actions.SelectMany(a => a.PawnActivities?.Orders ?? []).Any(o => o.Pawn == pawn
                && o.Steps.Any(s => s.Kind is PawnActivityKind.Work or PawnActivityKind.Deliver or PawnActivityKind.Recipe or PawnActivityKind.Approach or PawnActivityKind.Supply))
                && (!view.Capacity.TryGetValue(pawn, out var capacity) || capacity.Capacity.Windows is not ForecastCapacityWindows.Read(var windows)
                    || !windows.Any(w => w.Start == 0 && w.End > 0))) return "pawn_capacity_unavailable:" + pawn;
        }
        // A pawn the plan does not move already occupying a target: whole for an exclusive one, one more owner for a capacity target.
        // A preemptible holder runs vanilla work the coupled order interrupts (#3671), so it holds nothing against the candidate (#3670).
        IEnumerable<string> Holders(string target, bool journal) => journal
            ? view.Evidence.Pawns.Where(p => !p.Preemptible && p.Target == target).Select(p => p.Pawn)
            : view.Evidence.WorkResources.Where(p => p.Target == target).Select(p => p.Pawn);
        bool Occupied(string target, bool journal) => Holders(target, journal).Any(p => !candidate.Pawns.Contains(p));
        bool Crowded(string target, bool journal) => Occupied(target, journal) && TargetOverCapacity(view.Evidence, target,
            view.Claims.Shared.Concat(candidate.Shared).Where(s => s.Target == target), Holders(target, journal));
        if (candidate.Targets.FirstOrDefault(t => t.Length != 0 && (capacityTargets.Contains(t) ? Crowded(t, true) : Occupied(t, true))) is not null) return "pawn_target_claimed";
        if (candidate.Targets.FirstOrDefault(t => t.Length != 0 && (capacityTargets.Contains(t) ? Crowded(t, false) : Occupied(t, false))) is not null) return "native_target_claimed";
        var bills = proposed.Actions.Where(a => a.ProductionBill is { Mode: BillMode.GearBatch })
            .Select(a => new ProductionBatch(a.ProductionBill!, new ProgressView { Action = a.Id, Stage = Stage.Pending }));
        var production = ProductionRules.ProductionPromisesOf(ProductionRules.PriceProductionBatches(EquatableArray.From(bills), view.Evidence.Recipes), default);
        if (production is not ProductionPromiseSet.Assessed(var promises)) return "production_promises_unknown";
        // A blueprint spends nothing until delivery: it is placed whatever the stock, and its admitted cost is held
        // against the budget as construction the colony gathers toward. Other work in the same plan cannot spend it.
        var placements = proposed.Actions.Where(a => a.Building is not null && a.ConstructionTarget.Length == 0)
            .Select(a => "work/" + a.Id.Value).ToHashSet(StringComparer.Ordinal);
        if (placements.Count != 0 && view.Claims.Truncated) return "work_census_truncated";
        var placed = candidate.Materials.Where(c => placements.Contains(c.Id)).SelectMany(c => c.MaterialClaims)
            .GroupBy(c => c.Resource).ToDictionary(g => g.Key, g => g.Sum(c => ((MaterialUnits.Exact)c.Units).Count));
        var needed = candidate.Materials.Where(c => !placements.Contains(c.Id)).Concat(promises).SelectMany(c => c.MaterialClaims).GroupBy(c => c.Resource);
        foreach (var group in needed)
        {
            var credits = group.Where(c => c.ReservationBill.Length != 0)
                .GroupBy(c => c.ReservationBill).Select(g => Methods.Evaluation.WorkMaterialEvaluation.ExistingBillCredit(view, g.Key, group.Key,
                    g.All(c => c.Units is MaterialUnits.Exact) ? g.Sum(c => ((MaterialUnits.Exact)c.Units).Count) : 0)).ToArray();
            if (group.Any(c => c.Units is MaterialUnits.Open) || credits.Any(c => c is MaterialUnits.Open) || view.Materials is not MaterialAvailability.Assessed(var budget)
                || budget.GetValueOrDefault(group.Key, new MaterialUnits.Exact(0)) is not MaterialUnits.Exact(var free))
                return "materials_short:" + group.Key.Value;
            var need = group.Sum(c => ((MaterialUnits.Exact)c.Units).Count);
            var credit = credits.Sum(c => ((MaterialUnits.Exact)c).Count);
            free -= placed.GetValueOrDefault(group.Key);
            if (need > free + credit) return $"materials_short:{group.Key.Value} (need {need}, free {free}, credit {credit})";
        }
        return null;
    }

    public static WorkAdmissionEvidence WorkContinuationEvidence(WorkAdmissionEvidence evidence, PawnActivities batch,
        IReadOnlyList<ActivityOwnership>? observed = null)
    {
        var pawns = evidence.Pawns; var native = evidence.WorkResources;
        return evidence with
        {
            Pawns = EquatableArray.From(pawns.Select(p => batch.Orders.Any(o => o.Pawn == p.Pawn
            && (observed is not null ? observed.Any(n => ActivityContinuationRules.MatchesActivityOwner(o, n)
                && (n.State is "completed" or "failed" || o.Preparing && n.State == "holding"))
                : native.Any(n => n.Pawn == o.Pawn && n.Assignment == o.Assignment && n.Revision == o.Revision
                    && (n.State is "completed" or "failed" || o.Preparing && n.State == "holding"))))
            ? p with { Occupied = false } : p))
        };
    }
}
