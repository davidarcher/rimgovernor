namespace RimGovernor.Brain.Policy.Methods.Binding;

internal static partial class CoordinatedActivityBinding
{
    private static PawnActivityStep[] PreparationSteps(SelectedActivityTask task) =>
        [new(PawnActivityKind.Approach, "", "", "", 0, task.Step.Deadline)
            { Destination = task.Step.Kind == PawnActivityKind.Supply ? task.Source ?? task.Location : task.Location,
                NotBefore = task.DepartTick },
         new(PawnActivityKind.Hold, "", "", "", 0, task.Step.Deadline)];
    internal static PawnActivities? StartWithDeferred(ForecastCoupledSelection selection,
        IReadOnlyDictionary<(string Task, string Pawn), ActivityTaskCandidate> bindings, ulong authority, string method, long tick,
        IEnumerable<ForecastPlannedSite>? planned = null)
    {
        // Planned sites are selectable but not placed: they have no action template, so they start no order (#3278).
        var unplaced = (planned ?? []).SelectMany(s => s.Activities).ToHashSet(StringComparer.Ordinal);
        var active = Start(selection.Selection, bindings, authority, method, tick, unplaced);
        var deferredPawns = selection.Deferred.Where(t => t.Until > tick).ToList();
        active = DeferDistantServices(active, tick, deferredPawns);
        var deferred = deferredPawns.Select(t => new SelectedActivityTask(
            "defer/" + t.Pawn, t.Pawn, [], new(PawnActivityKind.Idle, "", "", "", 0, t.Until), t.Location, tick)).ToArray();
        if (deferred.Length == 0) return active;
        return new(authority, [.. active?.Orders ?? [], .. deferred.Select(t => new PawnActivityOrder(t.Pawn,
            method + "/" + t.Pawn, method, 0, 1, false, [t.Step]) { Task = t.Id })])
        { Tasks = EquatableArray.From((active?.Tasks ?? []).Concat(deferred)) };
    }

    /// <summary>An installed Method is protected from reselection, so holding a pawn for a service that departs far ahead would
    /// freeze the colony until the hold ends (#3047). A pawn whose first task is such a service, with nothing waiting on it,
    /// is deferred like any unselected pawn: it is reconsidered after the interval and the service starts once it is due.</summary>
    internal static PawnActivities? DeferDistantServices(PawnActivities? active, long tick, List<ForecastDeferredPawn> deferred)
    {
        if (active is null) return null;
        var far = active.Orders.Where(o => !o.Release && o.Preparing && active.Tasks.SingleOrDefault(t => t.Id == o.Task) is { } task
            && task.Prerequisites.Count == 0 && task.Step.Kind is PawnActivityKind.Sleep or PawnActivityKind.Recreation
            && task.DepartTick - tick > DistantServiceTicks
            && !active.Tasks.Any(t => t.Pawn != o.Pawn && t.Prerequisites.Any(p => active.Tasks.Any(r => r.Pawn == o.Pawn && r.Id == p))))
            .ToArray();
        if (far.Length == 0) return active;
        var pawns = far.Select(o => o.Pawn).ToHashSet(StringComparer.Ordinal);
        foreach (var order in far)
            deferred.Add(new(order.Pawn, active.Tasks.Single(t => t.Id == order.Task).Location, tick + DistantServiceTicks));
        var orders = active.Orders.Where(o => !pawns.Contains(o.Pawn)).ToArray();
        return orders.Length == 0 ? null : new(active.AuthorityGeneration, orders)
            { Tasks = EquatableArray.From(active.Tasks.Where(t => !pawns.Contains(t.Pawn))) };
    }
    private const long DistantServiceTicks = 600;
    internal static PawnActivities? Start(ForecastActivityOutcomeComparison? selection,
        IReadOnlyDictionary<(string Task, string Pawn), ActivityTaskCandidate> bindings, ulong authority, string method, long tick,
        IReadOnlySet<string>? unplaced = null)
    {
        if (selection?.Plan is not { } plan || selection.Selected.Length == 0) return null;
        var tasks = plan.Allocations.Where(a => unplaced?.Contains(a.Activity) != true).Select(a => bindings.TryGetValue((a.Activity, a.Pawn), out var task)
            ? new SelectedActivityTask(task.Id, task.Pawn, task.Prerequisites,
                new(task.Kind, task.Target, task.WorkGiver, task.Job, task.Count,
                    task.Practice != null || task.Kind is PawnActivityKind.Sleep or PawnActivityKind.Recreation ? checked(tick + (long)Math.Ceiling(a.End)) : task.Deadline)
                    { Practice = task.Practice, Destination = task.Destination, Container = task.Container, SupportDemand = task.SupportDemand, Medicine = task.Medicine, Recipe = task.Recipe ?? (task.Cooking is { } cooking
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
                ? PreparationSteps(task)
                : new[] { task.Step.Kind == PawnActivityKind.Supply ? task.Step with { Count = Math.Min(task.Step.Count, bindings[(task.Id, task.Pawn)].Capacity) } : task.Step };
            return new PawnActivityOrder(task.Pawn, method + "/" + task.Pawn, method, 0, 1, false, steps)
            { Task = task.Id, Preparing = preparing, OutputDelivery = !preparing && task.Resource.Length != 0 && task.Step.Count > 0
                ? new(task.Resource, task.Source ?? task.Location, task.Step.Destination ?? task.Location, task.Step.Count, task.Step.Deadline, task.AttemptsLeft) : null };
        }).ToArray();
        return new(authority, orders) { Tasks = EquatableArray.From(selected) };
    }
}
