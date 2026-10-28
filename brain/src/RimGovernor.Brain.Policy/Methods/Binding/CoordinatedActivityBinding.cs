using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Brain.Policy.Methods.Binding;

internal sealed record ActivityTaskTransition(PawnActivities? Batch, bool Release);

internal static class CoordinatedActivityBinding
{
    private static PawnActivityStep[] PreparationSteps(SelectedActivityTask task) =>
        [new PawnActivityStep(PawnActivityKind.Approach, "", "", "", 0, task.Step.Deadline) { NotBefore = task.DepartTick }.At(task.Step.Kind == PawnActivityKind.Supply ? task.Source : task.Location),
         new(PawnActivityKind.Hold, "", "", "", 0, task.Step.Deadline)];
    /// <summary>The steps the order runs before the task's own (#3617): a roof-holder's Work/RemoveRoof cell set step over the
    /// roofed cells it leaves unsupported (<see cref="ActivityTaskCandidate.RoofCells"/>), through the RemoveRoofs giver, as
    /// the shelter family names BuildRoofs.</summary>
    private static EquatableArray<PawnActivityStep> PriorSteps(ActivityTaskCandidate task) => task.RoofCells.Count == 0 ? default
        : [new(PawnActivityKind.Work, task.Target, "RemoveRoofs", "RemoveRoof", task.RoofCells.Count, task.Deadline) { Cells = task.RoofCells }];
    internal static PawnActivities? StartSelected(ForecastActivityOutcomeComparison selection,
        IReadOnlyDictionary<(string Task, string Pawn), ActivityTaskCandidate> bindings, ulong authority, string method, long tick,
        ForecastCoupledContext context, IEnumerable<ForecastPlannedSite>? planned = null)
    {
        // Planned sites are selectable but not placed: they have no action template, so they start no order (#3278). A
        // planned Deconstruct clearing is bound to its standing building and is not among them (#3557).
        var unplaced = (planned ?? []).SelectMany(s => s.Activities).ToHashSet(StringComparer.Ordinal);
        if (selection.Plan is not { } plan) return null;
        plan = ForDispatch(plan, context);
        var ordered = OrderedAtStart(selection.Selected, plan.Allocations, bindings, unplaced);
        if (ordered.Count == 0) return null;
        var active = Start(plan, bindings, authority, method, tick, unplaced);
        if (active.Orders.All(o => ordered.Contains(o.Pawn))) return active;
        return new(active.AuthorityGeneration, active.Orders.Where(o => ordered.Contains(o.Pawn)).ToArray())
            { Tasks = EquatableArray.From(active.Tasks.Where(t => ordered.Contains(t.Pawn))) };
    }

    /// <summary>Only work before the participant's native boundary is installed in PawnActivities. Routine native work
    /// is a conditional forecast, not an order. External comms/formation has its own executor. Neither can hold later
    /// work through unobserved progress; a fresh review must establish that work's prerequisites again.</summary>
    internal static ForecastActivityPlan ForDispatch(ForecastActivityPlan plan, ForecastCoupledContext context)
    {

        var external = context.TradeWork.Select(w => (w.Task, w.Pawn)).ToHashSet();
        var native = context.Activities.Activities.Where(a => a.NativeExecution).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var services = plan.Allocations.Where(a => NativeNeedServices.RoutineTask(a.Activity, a.Pawn, context))
            .Select(a => a.Activity).ToHashSet(StringComparer.Ordinal);
        var servicing = context.Needs.Pawns.Where(p => p.CurrentService is not null).Select(p => p.Pawn).ToHashSet(StringComparer.Ordinal);
        var dropped = plan.Allocations.Where(a => native.Contains(a.Activity) || external.Contains((a.Activity, a.Pawn))
                || services.Contains(a.Activity))
            .Select(a => a.Activity).ToHashSet(StringComparer.Ordinal);
        for (var grew = true; grew;)
        {
            grew = false;
            var boundaries = plan.Allocations.Where(a => dropped.Contains(a.Activity)).GroupBy(a => a.Pawn)
                // An actual service already occupies this pawn. Its what-if allocation later in the
                // schedule cannot authorize earlier work that would interrupt that native service now.
                .ToDictionary(g => g.Key, g => servicing.Contains(g.Key) && g.Any(a => services.Contains(a.Activity))
                    ? 0 : g.Min(a => a.Depart), StringComparer.Ordinal);
            foreach (var allocation in plan.Allocations)
                if (boundaries.TryGetValue(allocation.Pawn, out var start) && allocation.Depart >= start)
                    grew |= dropped.Add(allocation.Activity);
            foreach (var task in context.Activities.Activities)
                if (task.Prerequisites.Any(dropped.Contains)) grew |= dropped.Add(task.Id);
        }
        return plan with { Allocations = plan.Allocations.Where(a => !dropped.Contains(a.Activity)).ToArray() };
    }

    /// <summary>
    /// The pawns a selection orders at once (#3884, #3889): the one rule <see cref="StartSelected"/> installs and the forecast reads
    /// for a pawn asleep now, whose lie-down an order interrupts. The review dispatches the selection first
    /// (<see cref="ForecastIncumbentHold.Dispatched"/>); the forecast passes the same dispatch. No one is ordered when no id is
    /// selected (retaining observed baseline work requires no new orders) or when a placed allocation has no binding
    /// (<see cref="Unbound"/>); a planned
    /// site's work has no action template, so it orders no one (#3278). Otherwise <see cref="Start"/> orders each pawn with a
    /// placed task its first, by departure then id. Both production callers supply the ordinary-service-filtered dispatch
    /// plan, so no timed meal/sleep/recreation hold is installed here.
    /// </summary>
    internal static IReadOnlySet<string> OrderedAtStart(IReadOnlyCollection<string> selected, IReadOnlyList<ForecastActivityAllocation> allocations,
        IReadOnlyDictionary<(string Task, string Pawn), ActivityTaskCandidate> bindings, IReadOnlySet<string> unplaced)
    {
        if (selected.Count == 0 || Unbound(selected, allocations, bindings, unplaced) is not null) return new HashSet<string>();
        return allocations.Where(a => !unplaced.Contains(a.Activity)).Select(a => a.Pawn).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The placed allocation of a selection that has no binding (#3889), by which the binding starts none of it: null when
    /// nothing is selected (nothing to start) or every placed allocation is bound. <see cref="OrderedAtStart"/> orders no one then,
    /// and the forecast refuses such an outcome, so it never selects a plan the binding will not start.</summary>
    internal static ForecastActivityAllocation? Unbound(IReadOnlyCollection<string> selected, IReadOnlyList<ForecastActivityAllocation> allocations,
        IReadOnlyDictionary<(string Task, string Pawn), ActivityTaskCandidate> bindings, IReadOnlySet<string> unplaced)
    {
        if (selected.Count == 0) return null;
        foreach (var a in allocations)
            if (!unplaced.Contains(a.Activity) && !bindings.ContainsKey((a.Activity, a.Pawn))) return a;
        return null;
    }

    /// <summary>The orders of a schedule <see cref="OrderedAtStart"/> orders someone of, so every placed allocation is bound: each
    /// pawn is ordered its first task, by departure then id.</summary>
    internal static PawnActivities Start(ForecastActivityPlan plan,
        IReadOnlyDictionary<(string Task, string Pawn), ActivityTaskCandidate> bindings, ulong authority, string method, long tick,
        IReadOnlySet<string>? unplaced = null)
    {
        var selected = plan.Allocations.Where(a => unplaced?.Contains(a.Activity) != true).Select(a =>
        {
            var task = bindings[(a.Activity, a.Pawn)];
            return new SelectedActivityTask(task.Id, task.Pawn, task.Prerequisites,
                Step(task, new PawnActivityStep(task.Kind, task.Target, task.WorkGiver, task.Job, task.Count,
                    task.Practice != null || task.Kind is PawnActivityKind.Sleep or PawnActivityKind.Recreation ? checked(tick + (long)Math.Ceiling(a.End)) : task.Deadline)
                    { Practice = task.Practice, Container = task.Container, SupportDemand = task.SupportDemand, Medicine = task.Medicine,
                        Targets = task.Targets, Cells = task.Cells, FullLoad = task.FullLoad, Collect = task.Collect, Recipe = task.Recipe }),
                task.Location, checked(tick + (long)Math.Ceiling(a.Depart)))
                    { Resource = task.Resource, Source = task.Source, SiteDefinition = task.SiteDefinition, ProductStuff = task.ProductStuff,
                        Inputs = task.SelectedInputs, CompletionMustBeObserved = task.CompletionMustBeObserved, SupportDemand = task.SupportDemand,
                        PriorSteps = PriorSteps(task), UseUntilTick = SelectedActivityTask.UseUntil(tick, a.Depart, a.End) };
        }).ToArray();
        var orders = selected.GroupBy(t => t.Pawn).Select(group =>
        {
            var task = group.OrderBy(t => t.DepartTick).ThenBy(t => t.Id, StringComparer.Ordinal).First();
            var preparing = task.Prerequisites.Count != 0 || task.DepartTick > tick;
            // A supply, and a delivery whose carry is known, start with one carry: the continuation repeats the step over the
            // rest (a stack larger than one carry, #3625).
            var capacity = bindings[(task.Id, task.Pawn)].Capacity;
            var own = task.Step.Kind == PawnActivityKind.Supply || task.Step.Kind == PawnActivityKind.Deliver && capacity > 0
                ? task.Step with { Count = Math.Min(task.Step.Count, capacity) } : task.Step;
            var steps = preparing ? PreparationSteps(task) : [.. task.PriorSteps, own];
            return new PawnActivityOrder(task.Pawn, method + "/" + task.Pawn, method, 0, 1, false, steps)
            { Task = task.Id, Preparing = preparing, OutputDelivery = !preparing && task.Resource.Length != 0 && task.Step.Count > 0
                ? new(task.Resource, task.Source, task.Step is CellActivityStep cell ? cell.Destination : task.Location, task.Step.Count, task.Step.Deadline, task.AttemptsLeft) : null };
        }).ToArray();
        return new(authority, orders) { Tasks = EquatableArray.From(selected) };
    }

    private static PawnActivityStep Step(ActivityTaskCandidate task, PawnActivityStep step) =>
        task is CellActivityTaskCandidate cell ? step.At(cell.Destination) : step;

    internal static ActivityTaskTransition Continue(PawnActivities batch, ActivityTaskRead read,
        IReadOnlyDictionary<string, ActivityOwnership> rows, long tick, bool startWork)
    {
        if (batch.Orders.Any(o => !o.Release && o.Preparing && rows[o.Pawn].State == "failed"))
            return new(null, true);
        var remaining = batch.Tasks.ToList();
        var completed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var order in batch.Orders.Where(o => !o.Release && !o.Preparing && o.Task.Length != 0))
        {
            var state = rows[order.Pawn].State;
            var task = remaining.SingleOrDefault(t => t.Id == order.Task);
            if (task is null) continue;
            if (state is "completed" or "failed" && read.SatisfiedTasks.Contains(task.Id))
            { remaining.Remove(task); completed.Add(task.Id); continue; }
            if (state == "completed")
            {
                if (task.CompletionMustBeObserved) return new(null, true);
                // The task's own step is the order's last; its prior steps (#3617) run before it.
                var worked = order.Steps[^1];
                // Delivery counts come from the exact completed operation, never predicted yield.
                if (task.Step.Kind is PawnActivityKind.Deliver or PawnActivityKind.Supply && worked.Count < task.Step.Count)
                    remaining[remaining.IndexOf(task)] = task with { Step = task.Step with { Count = task.Step.Count - worked.Count } };
                // A set step's order worked the members native found legal (#3613); the rest of the set remains.
                else if (task.Step.IsSet && worked.IsSet && worked.Count < task.Step.Count)
                    remaining[remaining.IndexOf(task)] = task with { Step = RemainingSet(task.Step, worked) };
                else { remaining.Remove(task); completed.Add(task.Id); }
            }
            else if (state == "failed")
            {
                // A failed consuming/producing operation cannot be replayed from expected output.
                if (task.Step.Kind is not (PawnActivityKind.Deliver or PawnActivityKind.Supply) && !task.Step.IsSet || task.AttemptsLeft == 0)
                    return new(null, true);
                remaining[remaining.IndexOf(task)] = task with { AttemptsLeft = task.AttemptsLeft - 1 };
            }
        }
        // Release finished participants before attempting any new productive binding.
        // A temporarily claimed output stack must not strand an unrelated terminal pawn.
        var cleanup = batch.Orders.Where(o => !o.Release && rows[o.Pawn].State is "completed" or "failed"
            && !remaining.Any(t => t.Pawn == o.Pawn)).ToArray();
        if (cleanup.Length != 0) return new(Release(batch, cleanup.Select(o => o.Pawn).ToHashSet(StringComparer.Ordinal)), false);
        remaining = remaining.Select(t => t with { Prerequisites = EquatableArray.From(t.Prerequisites.Where(p => !completed.Contains(p))) }).ToList();
        var orders = new List<PawnActivityOrder>();
        var changed = startWork && completed.Count > 0;
        foreach (var order in batch.Orders)
        {
            if (order.Release) continue;
            var row = rows[order.Pawn];
            if (row.State is not ("completed" or "failed") && !(order.Preparing && row.State == "holding"))
            { orders.Add(order); continue; } // Exact same revision: no restart and no lost cleanup ownership.
            var next = remaining.Where(t => t.Pawn == order.Pawn).OrderBy(t => t.DepartTick).ThenBy(t => t.Id, StringComparer.Ordinal).FirstOrDefault();
            if (next is null)
            {
                if (order.Revision == long.MaxValue) return new(null, false);
                orders.Add(order with { ExpectedRevision = order.Revision, Revision = order.Revision + 1, Release = true,
                    Steps = [], Task = "", Preparing = false, OutputDelivery = null });
                changed = true; continue;
            }
            // A previous exact forecast is no authority to start or hold ordinary native service.
            // Reconcile only at a task boundary, preserving in-flight work. If another participant
            // depends on this participant's remaining work, end the coordinated attempt rather
            // than erase an unobserved prerequisite and accidentally authorize its dependent.
            if (batch.Tasks.Any(t => t.Pawn == order.Pawn && t.Step.Kind == PawnActivityKind.Recipe) || next.ProductStuff.Length > 0
                || next.Step.Kind == PawnActivityKind.Supply || next.SupportDemand.Length > 0
                || next.Step.Kind == PawnActivityKind.Work && next.Step.Job is "FinishFrame" or "BuildRoof"
                // Standing designations carry this producer identity; planned footprint clearance does not.
                || next.Id.StartsWith(NativeConstructionWork.StandingRemovalPrefix, StringComparison.Ordinal)
                    && next.Step.Kind == PawnActivityKind.Work
                    && (next.Step.Job is "Uninstall" or "CutPlant" or "RemoveFloor" or "Mine"
                        || next.Step.Job == "Deconstruct" && next.PriorSteps.Count == 0)
                || NativeNeedServices.Routine(next.Step.Kind))
                return remaining.Any(t => t.Pawn != order.Pawn && t.Prerequisites.Any(p => remaining.Any(r => r.Pawn == order.Pawn && r.Id == p)))
                    ? new(null, true)
                    : new(Release(batch, new HashSet<string>(StringComparer.Ordinal) { order.Pawn }), false);
            // Unlisted demand permits physical cleanup, not another work/preparation order.
            if (!startWork) { orders.Add(order); continue; }
            // A task in flight is never interrupted; at this boundary (a task just ended, or the pawn is waiting to depart) a participant already at or below a rest or food
            // threshold is not handed more work. Its order is released and the next review compares the need service
            // against the remaining work. Tasks others wait on stay, so no participant is stranded.
            // A hypothetical later meal does not exempt its earlier work from native needs handback.
            if (NeedsService(read, order.Pawn)
                && !remaining.Any(t => t.Pawn != order.Pawn && t.Prerequisites.Any(p => remaining.Any(r => r.Pawn == order.Pawn && r.Id == p))))
            {
                if (order.Revision == long.MaxValue) return new(null, false);
                remaining.RemoveAll(t => t.Pawn == order.Pawn);
                orders.Add(order with { ExpectedRevision = order.Revision, Revision = order.Revision + 1, Release = true,
                    Steps = [], Task = "", Preparing = false, OutputDelivery = null });
                changed = true; continue;
            }
            if (next.Prerequisites.Count != 0 || next.DepartTick > tick)
            {
                if (order.Preparing) { orders.Add(order); continue; }
                if (order.Revision == long.MaxValue) return new(null, false);
                // Completion stops native time. Install the next task's bounded preparation
                // rather than retaining a terminal order until a future departure tick.
                orders.Add(order with { ExpectedRevision = order.Revision, Revision = order.Revision + 1,
                    Task = next.Id, Preparing = true, Steps = PreparationSteps(next), OutputDelivery = null });
                changed = true; continue;
            }
            var binding = read.Bindings.Where(b => b.Task == next.Id && CompatibleActivityBinding(next, b.Step))
                .OrderBy(b => b.Step.Target, StringComparer.Ordinal).FirstOrDefault();
            if (binding is null)
            {
                if (read.Truncated) return new(null, false);
                return new(null, true);
            }
            if (order.Revision == long.MaxValue) return new(null, false);
            if (next.Step.IsSet)
                remaining[remaining.IndexOf(next)] = next = next with { Step = next.Step with { Targets = binding.Step.Targets, Cells = binding.Step.Cells, Count = binding.Step.Count } };
            else if (binding is ActivityTaskBinding.Supply supply)
                remaining[remaining.IndexOf(next)] = next = next with { Step = next.Step with { Count = Math.Min(next.Step.Count, supply.RemainingCount) } };
            orders.Add(order with { ExpectedRevision = order.Revision, Revision = order.Revision + 1, Release = false,
                Task = next.Id, Preparing = false, Steps = [.. next.PriorSteps, binding.Step],
                OutputDelivery = next.Resource.Length == 0 || binding.Step.Count <= 0 ? null : new(next.Resource, next.Source,
                    binding.Step is CellActivityStep cell ? cell.Destination : next.Location, binding.Step.Count, binding.Step.Deadline, next.AttemptsLeft) });
            changed = true;
        }
        if (!changed) return new(null, false);
        return new(new(batch.AuthorityGeneration, orders.ToArray()) { Tasks = EquatableArray.From(remaining) }, false);
    }

    /// <summary>The batch that releases <paramref name="pawns"/> and keeps every other order exactly as it is, so no other
    /// participant is interrupted: the released pawns' tasks leave and no remaining task waits on one (the journal's cleanup
    /// delta). Null when a released order's revision cannot advance.</summary>
    internal static PawnActivities? Release(PawnActivities batch, IReadOnlySet<string> pawns)
    {
        var orders = batch.Orders.Where(o => !o.Release).ToArray();
        if (orders.Any(o => pawns.Contains(o.Pawn) && o.Revision == long.MaxValue)) return null;
        var removed = batch.Tasks.Where(t => pawns.Contains(t.Pawn)).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        return new(batch.AuthorityGeneration, orders.Select(o => pawns.Contains(o.Pawn)
            ? o with { ExpectedRevision = o.Revision, Revision = o.Revision + 1, Release = true, Steps = [], Task = "", Preparing = false, OutputDelivery = null }
            : o).ToArray())
        {
            Tasks = EquatableArray.From(batch.Tasks.Where(t => !removed.Contains(t.Id)).Select(t =>
                t with { Prerequisites = EquatableArray.From(t.Prerequisites.Where(p => !removed.Contains(p))) }))
        };
    }

    private static bool NeedsService(ActivityTaskRead read, string pawn) =>
        read.Needs.Pawns.FirstOrDefault(p => p.Pawn == pawn) is { } observed
        && observed.Needs.Any(n => n.Kind is "rest" or "food" && n.Level <= n.Threshold);

    /// <summary>The set a completed set order leaves (#3613): the members the order did not carry, Count their number.</summary>
    private static PawnActivityStep RemainingSet(PawnActivityStep step, PawnActivityStep worked)
    {
        var targets = EquatableArray.From(step.Targets.Where(t => !worked.Targets.Contains(t)));
        var cells = EquatableArray.From(step.Cells.Where(c => !worked.Cells.Contains(c)));
        return step with { Targets = targets, Cells = cells, Count = targets.Count + cells.Count };
    }

    private static bool CompatibleActivityBinding(SelectedActivityTask task, PawnActivityStep bound) =>
        bound.Kind == task.Step.Kind && bound.Deadline == task.Step.Deadline && bound.WorkGiver == task.Step.WorkGiver
        // A set step binds to a non-empty subset of its own members (#3613).
        && (!task.Step.IsSet || bound.IsSet && bound.Count == bound.Targets.Count + bound.Cells.Count
            && bound.Targets.All(task.Step.Targets.Contains) && bound.Cells.All(task.Step.Cells.Contains))
        && bound.Practice == task.Step.Practice && bound.Medicine == task.Step.Medicine && bound.SupportDemand == task.Step.SupportDemand
        && (bound.Kind != PawnActivityKind.Recipe || bound.Target == task.Step.Target)
        && bound.Job == task.Step.Job && (bound.Kind is not (PawnActivityKind.Deliver or PawnActivityKind.Supply)
            || bound.Count > 0 && bound.Count <= task.Step.Count)
        && bound.FullLoad == task.Step.FullLoad && bound.Collect.All(task.Step.Collect.Contains)
        && (task.Step.Recipe is not { } recipe || bound.Recipe is { } cooking && cooking.Bill == recipe.Bill
            && cooking.Recipe == recipe.Recipe && cooking.Resource == recipe.Resource && cooking.Stuff == recipe.Stuff && cooking.Unfinished == recipe.Unfinished
            && cooking.Ingredients.Sum(i => i.Count) == recipe.Ingredients.Sum(i => i.Count));
}
