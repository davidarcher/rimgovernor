namespace RimGovernor.Brain.Policy.Methods.Binding;

internal static partial class CoordinatedActivityBinding
{
    internal static PawnActivities? Start(ForecastActivityOutcomeComparison? selection,
        IReadOnlyDictionary<(string Task, string Pawn), ActivityTaskCandidate> bindings, ulong authority, string method, long tick)
    {
        if (selection?.Plan is not { } plan || selection.Selected.Length == 0) return null;
        var tasks = plan.Allocations.Select(a => bindings.TryGetValue((a.Activity, a.Pawn), out var task)
            ? new SelectedActivityTask(task.Id, task.Pawn, task.Prerequisites,
                new(task.Kind, task.Target, task.WorkGiver, task.Job, task.Count,
                    task.Kind is PawnActivityKind.Sleep or PawnActivityKind.Recreation ? checked(tick + (long)Math.Ceiling(a.End)) : task.Deadline)
                    { Destination = task.Destination, Container = task.Container, SupportDemand = task.SupportDemand, Medicine = task.Medicine, Recipe = task.Recipe ?? (task.Cooking is { } cooking
                        ? new(cooking.Continuation.Bill, cooking.Continuation.Recipe, [new(task.Ingredient, cooking.IngredientCount)]) { Resource = task.Resource } : null) },
                task.Location, checked(tick + (long)Math.Ceiling(a.Depart)))
                    { Resource = task.Resource, Source = task.Source, SiteDefinition = task.SiteDefinition, ProductStuff = task.ProductStuff,
                        Inputs = task.SelectedInputs, CompletionMustBeObserved = task.CompletionMustBeObserved, SupportDemand = task.SupportDemand,
                        ProductiveWorkTicks = a.Learning is null ? default : Known(a.WorkTicks) } : null).ToArray();
        if (tasks.Any(t => t is null)) return null;
        var selected = tasks.Select(t => t!).ToArray();
        if (selected.Length == 0) return null; // Retaining observed baseline work requires no new orders.
        var orders = selected.GroupBy(t => t.Pawn).Select(group =>
        {
            var task = group.OrderBy(t => t.DepartTick).ThenBy(t => t.Id, StringComparer.Ordinal).First();
            var preparing = task.Prerequisites.Count != 0 || task.DepartTick > tick;
            var steps = preparing
                ? new[] { new PawnActivityStep(PawnActivityKind.Approach, "", "", "", 0, task.Step.Deadline)
                    { Destination = task.Step.Kind == PawnActivityKind.Supply ? task.Source ?? task.Location : task.Location, NotBefore = task.DepartTick }, new PawnActivityStep(PawnActivityKind.Hold, "", "", "", 0, task.Step.Deadline) }
                : new[] { task.Step.Kind == PawnActivityKind.Supply ? task.Step with { Count = Math.Min(task.Step.Count, bindings[(task.Id, task.Pawn)].Capacity) } : task.Step };
            return new PawnActivityOrder(task.Pawn, method + "/" + task.Pawn, method, 0, 1, false, steps)
            { Task = task.Id, Preparing = preparing, OutputDelivery = !preparing && task.Resource.Length != 0 && task.Step.Count > 0
                ? new(task.Resource, task.Source ?? task.Location, task.Step.Destination ?? task.Location, task.Step.Count, task.Step.Deadline, task.AttemptsLeft) : null };
        }).ToArray();
        return new(authority, orders) { Tasks = EquatableArray.From(selected) };
    }
}
