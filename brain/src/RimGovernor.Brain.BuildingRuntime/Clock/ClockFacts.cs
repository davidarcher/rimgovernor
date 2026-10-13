namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The scheduler's fact store beside the kind of every attempt a window arms, so a wake's outcome ids resolve to
/// the planners that dispatch their kinds. The Go event-page invalidation (<c>apply</c>) is dropped with the native
/// event poll: the clock stays in the mod.
/// </summary>
internal sealed class ClockFacts
{
    private const int WatchedMax = 256;

    private readonly Lock _mu = new();
    private Dictionary<ActionId, ActionKind> _watched = [];

    public ClockFacts(FactStore? store)
    {
        Store = store ?? new FactStore();
        PlannerSnapshot.RecordTables(Store);
    }

    public FactStore Store { get; }

    /// <summary>Keeps the kind of every attempt a window arms; the map is bounded and cleared once it fills, which only costs a broader invalidation.</summary>
    public void Remember(IEnumerable<ClockWorkItem> items)
    {
        lock (_mu)
        {
            foreach (var item in items)
            {
                if (item.Attempt.Value == 0)
                {
                    continue;
                }
                if (_watched.Count >= WatchedMax)
                {
                    _watched = [];
                }
                _watched[item.Action] = item.Kind;
            }
        }
    }

    public ActionKind? KindOf(ActionId id)
    {
        lock (_mu)
        {
            return _watched.TryGetValue(id, out var kind) ? kind : null;
        }
    }
}

/// <summary>One action of the plan a window watches.</summary>
internal sealed record ClockWorkItem(ActionId Action, ActionKind Kind, Stage Stage, AttemptId Attempt, bool Unresolved);
