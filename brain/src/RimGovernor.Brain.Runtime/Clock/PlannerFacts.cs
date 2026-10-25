namespace RimGovernor.Brain.Runtime;

/// <summary>
/// The scheduler's fact store. The clock stays in
/// the mod, so there is no event-page invalidation (<c>apply</c>).
/// </summary>
internal sealed class PlannerFacts
{
    public PlannerFacts(FactStore? store)
    {
        Store = store ?? new FactStore();
        PlannerSnapshot.RecordTables(Store);
    }

    public FactStore Store { get; }
}

/// <summary>One action of the plan a run watches.</summary>
internal sealed record PlannerWorkItem(ActionId Action, ActionKind Kind, Stage Stage, AttemptId Attempt, bool Unresolved);
