using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>
/// Stages the governor's layout plan in the save-owned governor state, so a served case starts from a plan describing
/// room geometry that already stands instead of playing a layout review into it.
/// </summary>
internal static class TypedLayoutPlan
{
    /// <summary>Replaces the save's layout plan blob with <paramref name="plan"/> (the service reads it at start), keeping every other blob.</summary>
    public static Task InjectAsync(TypedSession s, LayoutPlan plan, CancellationToken cancellationToken) =>
        TypedGovernorState.InjectAsync(s, Store.Store.GovernorLayoutPlanKey, plan, scoped: true, cancellationToken);
}
