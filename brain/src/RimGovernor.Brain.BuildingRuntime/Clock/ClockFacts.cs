namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The scheduler's fact store. The Go event-page invalidation (<c>apply</c>) is dropped with the native
/// event poll: the clock stays in the mod.
/// </summary>
internal sealed class ClockFacts
{
    public ClockFacts(FactStore? store)
    {
        Store = store ?? new FactStore();
        PlannerSnapshot.RecordTables(Store);
    }

    public FactStore Store { get; }
}

/// <summary>One action of the plan a run watches.</summary>
internal sealed record ClockWorkItem(ActionId Action, ActionKind Kind, Stage Stage, AttemptId Attempt, bool Unresolved);
