using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy.Methods.Binding;

internal sealed record ActivityTaskTransition(PawnActivities? Batch, bool Release);

internal static partial class CoordinatedActivityBinding
{
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
                // Delivery counts come from the exact completed operation, never predicted yield.
                if (task.Step.Kind is PawnActivityKind.Deliver or PawnActivityKind.Supply && order.Steps[0].Count < task.Step.Count)
                    remaining[remaining.IndexOf(task)] = task with { Step = task.Step with { Count = task.Step.Count - order.Steps[0].Count } };
                else { remaining.Remove(task); completed.Add(task.Id); }
            }
            else if (state == "failed")
            {
                // A failed consuming/producing operation cannot be replayed from expected output.
                if (task.Step.Kind is not (PawnActivityKind.Deliver or PawnActivityKind.Supply) || task.AttemptsLeft == 0)
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
            if (binding.RemainingCount.TryGetValue(out var remainingCount))
                remaining[remaining.IndexOf(next)] = next = next with { Step = next.Step with { Count = Math.Min(next.Step.Count, remainingCount) } };
            orders.Add(order with { ExpectedRevision = order.Revision, Revision = order.Revision + 1, Release = false,
                Task = next.Id, Preparing = false, Steps = [binding.Step],
                OutputDelivery = next.Resource.Length == 0 || binding.Step.Count <= 0 ? null : new(next.Resource, next.Source ?? next.Location,
                    binding.Step.Destination ?? next.Location, binding.Step.Count, binding.Step.Deadline, next.AttemptsLeft) });
            changed = true;
        }
        if (!changed) return new(null, false);
        return new(new(batch.AuthorityGeneration, orders.ToArray()) { Tasks = EquatableArray.From(remaining) }, false);
    }

    private static bool CompatibleActivityBinding(SelectedActivityTask task, PawnActivityStep bound) =>
        bound.Kind == task.Step.Kind && bound.Deadline == task.Step.Deadline && bound.WorkGiver == task.Step.WorkGiver
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
