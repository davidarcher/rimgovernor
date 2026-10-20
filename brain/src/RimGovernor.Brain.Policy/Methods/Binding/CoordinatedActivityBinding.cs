using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy.Methods.Binding;

internal sealed record ActivityTaskTransition(PawnActivities? Batch, bool Release);

internal static class CoordinatedActivityBinding
{
    private static PawnActivityStep[] PreparationSteps(SelectedActivityTask task) =>
        [new(PawnActivityKind.Approach, "", "", "", 0, task.Step.Deadline)
            { Destination = task.Step.Kind == PawnActivityKind.Supply ? task.Source ?? task.Location : task.Location,
                NotBefore = task.DepartTick },
         new(PawnActivityKind.Hold, "", "", "", 0, task.Step.Deadline)];
    /// <summary>The steps the order runs before the task's own (#3617): a roof-holder's Work/RemoveRoof cell set step over the
    /// roofed cells it leaves unsupported (<see cref="ActivityTaskCandidate.RoofCells"/>), through the RemoveRoofs giver, as
    /// the shelter family names BuildRoofs.</summary>
    private static EquatableArray<PawnActivityStep> PriorSteps(ActivityTaskCandidate task) => task.RoofCells.Count == 0 ? default
        : [new(PawnActivityKind.Work, task.Target, "RemoveRoofs", "RemoveRoof", task.RoofCells.Count, task.Deadline) { Cells = task.RoofCells }];
    internal static PawnActivities? StartSelected(ForecastActivityOutcomeComparison selection,
        IReadOnlyDictionary<(string Task, string Pawn), ActivityTaskCandidate> bindings, ulong authority, string method, long tick,
        IEnumerable<ForecastPlannedSite>? planned = null)
    {
        // Planned sites are selectable but not placed: they have no action template, so they start no order (#3278). A
        // planned Deconstruct clearing is bound to its standing building and is not among them (#3557).
        var unplaced = (planned ?? []).SelectMany(s => s.Activities).ToHashSet(StringComparer.Ordinal);
        var active = Start(selection, bindings, authority, method, tick, unplaced);
        // The scheduler departs a waiting pawn at once and holds it at the site: its service is as far as its planned start.
        var leave = selection.Plan?.Allocations.GroupBy(a => a.Activity, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => checked(tick + (long)Math.Floor(g.Min(a => a.Start - a.TravelTicks))), StringComparer.Ordinal);
        return DeferDistantServices(active, tick, leave);
    }

    /// <summary>An installed Method is protected from reselection, so holding a pawn for a service that departs far ahead would
    /// freeze the colony until the hold ends (#3047). A pawn whose first task is such a service, with nothing waiting on it,
    /// is left out like any unselected pawn: standby takes it (#3624) and a later review starts the service once it is due. So
    /// is a meal waiting only on other pawns' work (#3167): holding the eater until the cook's last meal froze it for most of
    /// a day; left out, it eats from what the work has stocked by then.</summary>
    internal static PawnActivities? DeferDistantServices(PawnActivities? active, long tick, IReadOnlyDictionary<string, long>? leave = null)
    {
        if (active is null) return null;
        var far = active.Orders.Where(o => !o.Release && o.Preparing && active.Tasks.SingleOrDefault(t => t.Id == o.Task) is { } task
            && task.Prerequisites.All(p => active.Tasks.Any(t => t.Id == p && t.Pawn != o.Pawn))
            && task.Step.Kind is PawnActivityKind.Sleep or PawnActivityKind.Recreation or PawnActivityKind.Eat
            && Math.Max(task.DepartTick, leave?.GetValueOrDefault(task.Id) ?? 0) - tick > DistantServiceTicks
            && !active.Tasks.Any(t => t.Pawn != o.Pawn && t.Prerequisites.Any(p => active.Tasks.Any(r => r.Pawn == o.Pawn && r.Id == p))))
            .ToArray();
        if (far.Length == 0) return active;
        var pawns = far.Select(o => o.Pawn).ToHashSet(StringComparer.Ordinal);
        var orders = active.Orders.Where(o => !pawns.Contains(o.Pawn)).ToArray();
        return orders.Length == 0 ? null : new(active.AuthorityGeneration, orders)
            { Tasks = EquatableArray.From(active.Tasks.Where(t => !pawns.Contains(t.Pawn))) };
    }
    private const long DistantServiceTicks = 600;

    /// <summary>One task started now outside a schedule (#3624, a store haul): its pawn's one order runs the task's step at
    /// once, and its stack claim holds for the task's <paramref name="ticks"/>. The selected task is kept, so continuation
    /// releases the pawn when the step completes.</summary>
    internal static PawnActivities StartAlone(ActivityTaskCandidate task, ulong authority, string method, long tick, double ticks)
    {
        var selected = new SelectedActivityTask(task.Id, task.Pawn, [],
            new(task.Kind, task.Target, task.WorkGiver, task.Job, task.Count, task.Deadline) { Destination = task.Destination }, task.Location, tick)
            { Resource = task.Resource, Source = task.Source, UseUntilTick = SelectedActivityTask.UseUntil(tick, 0, ticks) };
        var order = new PawnActivityOrder(task.Pawn, method + "/" + task.Pawn, method, 0, 1, false, [selected.Step])
        { Task = selected.Id, OutputDelivery = task.Resource.Length != 0 && task.Count > 0
            ? new(task.Resource, task.Source ?? task.Location, task.Destination ?? task.Location, task.Count, task.Deadline, selected.AttemptsLeft) : null };
        return new(authority, [order]) { Tasks = [selected] };
    }
    internal static PawnActivities? Start(ForecastActivityOutcomeComparison? selection,
        IReadOnlyDictionary<(string Task, string Pawn), ActivityTaskCandidate> bindings, ulong authority, string method, long tick,
        IReadOnlySet<string>? unplaced = null)
    {
        if (selection?.Plan is not { } plan || selection.Selected.Length == 0) return null;
        var tasks = plan.Allocations.Where(a => unplaced?.Contains(a.Activity) != true).Select(a => bindings.TryGetValue((a.Activity, a.Pawn), out var task)
            ? new SelectedActivityTask(task.Id, task.Pawn, task.Prerequisites,
                new(task.Kind, task.Target, task.WorkGiver, task.Job, task.Count,
                    task.Practice != null || task.Kind is PawnActivityKind.Sleep or PawnActivityKind.Recreation ? checked(tick + (long)Math.Ceiling(a.End)) : task.Deadline)
                    { Practice = task.Practice, Destination = task.Destination, Container = task.Container, SupportDemand = task.SupportDemand, Medicine = task.Medicine,
                        Targets = task.Targets, Cells = task.Cells, Recipe = task.Recipe ?? (task.Cooking is { } cooking
                        ? new(cooking.Continuation.Bill, cooking.Continuation.Recipe, [new(task.Ingredient, cooking.IngredientCount)]) { Resource = task.Resource } : null) },
                task.Location, checked(tick + (long)Math.Ceiling(a.Depart)))
                    { Resource = task.Resource, Source = task.Source, SiteDefinition = task.SiteDefinition, ProductStuff = task.ProductStuff,
                        Inputs = task.SelectedInputs, CompletionMustBeObserved = task.CompletionMustBeObserved, SupportDemand = task.SupportDemand,
                        // The native binding checks a recipe step's learning-dependent duration; support work keeps its own bound.
                        ProductiveWorkTicks = a.Learning is null || task.Kind != PawnActivityKind.Recipe ? default : Known(a.WorkTicks), PriorSteps = PriorSteps(task),
                        UseUntilTick = SelectedActivityTask.UseUntil(tick, a.Depart, a.End) } : null).ToArray();
        if (tasks.Any(t => t is null)) return null;
        var selected = tasks.Select(t => t!).ToArray();
        if (selected.Length == 0) return null; // Retaining observed baseline work requires no new orders.
        var orders = selected.GroupBy(t => t.Pawn).Select(group =>
        {
            var task = group.OrderBy(t => t.DepartTick).ThenBy(t => t.Id, StringComparer.Ordinal).First();
            var preparing = task.Prerequisites.Count != 0 || task.DepartTick > tick;
            var steps = preparing
                ? PreparationSteps(task)
                : [.. task.PriorSteps, task.Step.Kind == PawnActivityKind.Supply ? task.Step with { Count = Math.Min(task.Step.Count, bindings[(task.Id, task.Pawn)].Capacity) } : task.Step];
            return new PawnActivityOrder(task.Pawn, method + "/" + task.Pawn, method, 0, 1, false, steps)
            { Task = task.Id, Preparing = preparing, OutputDelivery = !preparing && task.Resource.Length != 0 && task.Step.Count > 0
                ? new(task.Resource, task.Source ?? task.Location, task.Step.Destination ?? task.Location, task.Step.Count, task.Step.Deadline, task.AttemptsLeft) : null };
        }).ToArray();
        return new(authority, orders) { Tasks = EquatableArray.From(selected) };
    }

    internal static ActivityTaskTransition Continue(PawnActivities batch, ActivityTaskRead read,
        IReadOnlyDictionary<string, ActivityOwnership> rows, long tick)
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
        if (cleanup.Length != 0)
        {
            if (cleanup.Any(o => o.Revision == long.MaxValue)) return new(null, false);
            var releasedPawns = cleanup.Select(o => o.Pawn).ToHashSet(StringComparer.Ordinal);
            var removed = batch.Tasks.Where(t => releasedPawns.Contains(t.Pawn)).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            return new(new(batch.AuthorityGeneration, batch.Orders.Where(o => !o.Release).Select(o =>
                releasedPawns.Contains(o.Pawn)
                    ? o with { ExpectedRevision = o.Revision, Revision = o.Revision + 1, Release = true,
                        Steps = [], Task = "", Preparing = false, OutputDelivery = null }
                    : o).ToArray())
            {
                Tasks = EquatableArray.From(batch.Tasks.Where(t => !removed.Contains(t.Id)).Select(t =>
                    t with { Prerequisites = EquatableArray.From(t.Prerequisites.Where(p => !removed.Contains(p))) }))
            }, false);
        }
        remaining = remaining.Select(t => t with { Prerequisites = EquatableArray.From(t.Prerequisites.Where(p => !completed.Contains(p))) }).ToList();
        var orders = new List<PawnActivityOrder>();
        var changed = completed.Count > 0;
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
            // A task in flight is never interrupted; at this boundary (a task just ended, or the pawn is waiting to depart) a participant already at or below a rest or food
            // threshold is not handed more work. Its order is released and the next review compares the need service
            // against the remaining work. Tasks others wait on stay, so no participant is stranded. A next task that is
            // itself a service is what that review selected: releasing it would only reselect it, every review (#3167).
            // So is a task that leads to the pawn's own remaining service, such as harvesting the rice it then eats (#3598).
            // A sleep or recreation that native recovery ended before its planned end (the pawn woke rested) leaves the rest of
            // the pawn's timeline early. As at the start (DeferDistantServices, #3047), it is not held for a next service departing
            // far ahead, owned and unreviewable (#3598): released, the next review replans it from its observed needs. Nor is a
            // participant whose next service waits only on other pawns' work, such as an eater held for the cook's last meal
            // for most of a day (#3167).
            var distant = row.State == "completed" && IsService(next) && next.DepartTick - tick > DistantServiceTicks
                && (next.Prerequisites.Count == 0 && batch.Tasks.SingleOrDefault(t => t.Id == order.Task) is
                        { Step.Kind: PawnActivityKind.Sleep or PawnActivityKind.Recreation } ended && ended.Step.Deadline > tick
                    || next.Prerequisites.Count > 0 && next.Prerequisites.All(p => remaining.Any(t => t.Id == p && t.Pawn != order.Pawn)));
            if ((distant || !IsService(next) && !FeedsOwnService(remaining, next) && NeedsService(read, order.Pawn))
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
            var binding = read.Bindings.Where(b => b.Task == next.Id && CompatibleActivityBinding(next, b.Step)
                    && (!next.ProductiveWorkTicks.IsKnown || b.ProductiveWorkTicks.IsKnown && b.ProductiveWorkTicks.Value <= Math.Ceiling(next.ProductiveWorkTicks.Value)))
                .OrderBy(b => b.Step.Target, StringComparer.Ordinal).FirstOrDefault();
            if (binding is null)
            {
                if (read.Truncated) return new(null, false);
                return new(null, true);
            }
            if (order.Revision == long.MaxValue) return new(null, false);
            if (next.Step.IsSet)
                remaining[remaining.IndexOf(next)] = next = next with { Step = next.Step with { Targets = binding.Step.Targets, Cells = binding.Step.Cells, Count = binding.Step.Count } };
            else if (binding.RemainingCount.TryGetValue(out var remainingCount))
                remaining[remaining.IndexOf(next)] = next = next with { Step = next.Step with { Count = Math.Min(next.Step.Count, remainingCount) } };
            orders.Add(order with { ExpectedRevision = order.Revision, Revision = order.Revision + 1, Release = false,
                Task = next.Id, Preparing = false, Steps = [.. next.PriorSteps, binding.Step],
                OutputDelivery = next.Resource.Length == 0 || binding.Step.Count <= 0 ? null : new(next.Resource, next.Source ?? next.Location,
                    binding.Step.Destination ?? next.Location, binding.Step.Count, binding.Step.Deadline, next.AttemptsLeft) });
            changed = true;
        }
        if (!changed) return new(null, false);
        return new(new(batch.AuthorityGeneration, orders.ToArray()) { Tasks = EquatableArray.From(remaining) }, false);
    }

    private static bool IsService(SelectedActivityTask task) =>
        task.Step.Kind is PawnActivityKind.Eat or PawnActivityKind.Sleep or PawnActivityKind.Recreation;

    /// <summary>Whether a remaining service task of the same pawn depends on <paramref name="task"/>, directly or through
    /// the pawn's other remaining tasks.</summary>
    private static bool FeedsOwnService(List<SelectedActivityTask> remaining, SelectedActivityTask task)
    {
        var own = remaining.Where(t => t.Pawn == task.Pawn).ToDictionary(t => t.Id, StringComparer.Ordinal);
        var pending = new Stack<string>(own.Values.Where(IsService).SelectMany(t => t.Prerequisites));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryPop(out var id))
        {
            if (id == task.Id) return true;
            if (seen.Add(id) && own.TryGetValue(id, out var prerequisite)) foreach (var p in prerequisite.Prerequisites) pending.Push(p);
        }
        return false;
    }

    private static bool NeedsService(ActivityTaskRead read, string pawn) =>
        read.Needs?.Pawns.FirstOrDefault(p => p.Pawn == pawn) is { } observed
        && observed.Needs.Any(n => n.Kind is "rest" or "food" && n.Level.TryGetValue(out var level)
            && n.Threshold.TryGetValue(out var threshold) && level <= threshold);

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
        && (bound.Kind is not (PawnActivityKind.Recipe or PawnActivityKind.Sleep or PawnActivityKind.Recreation)
            || bound.Target == task.Step.Target)
        && (bound.Kind != PawnActivityKind.Eat || bound.Count == task.Step.Count)
        && bound.Job == task.Step.Job && (bound.Kind is not (PawnActivityKind.Deliver or PawnActivityKind.Supply)
            || bound.Count > 0 && bound.Count <= task.Step.Count)
        && (task.Step.Recipe is not { } recipe || bound.Recipe is { } cooking && cooking.Bill == recipe.Bill
            && cooking.Recipe == recipe.Recipe && cooking.Resource == recipe.Resource && cooking.Stuff == recipe.Stuff && cooking.Unfinished == recipe.Unfinished
            && cooking.Ingredients.Sum(i => i.Count) == recipe.Ingredients.Sum(i => i.Count));
}
