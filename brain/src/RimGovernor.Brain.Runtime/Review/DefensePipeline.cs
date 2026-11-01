namespace RimGovernor.Brain.Runtime;

/// <summary>The defense step's open-work check and its telemetry rows.</summary>
internal static class DefensePipeline
{
    /// <summary>
    /// Whether a retained tier's, fight's or cover's Method of the layout/defense Need still holds an open plan. A bound
    /// choice's builds and removals and a turret refuel are not counted: the step re-compares over them (DefenseBindingRules),
    /// so a choice that loses retires its work and a lost replacement holds its removal.
    /// </summary>
    internal static bool DefenseWorkOpen(NeedOwner goal, Func<PlanId, PlanState> plans) =>
        goal.Methods.Any(method => !DefenseBindingRules.ChainMethod(method.Method.Value) && !method.Method.Value.StartsWith(DefenseRearm.RefuelMethod, StringComparison.Ordinal)
            && StorePackage.PlanOpen(plans(method.Plan)));

    internal static void DefenseAction(string component, TelemetryLevel level, string verdict, string reason, string target, IReadOnlyDictionary<string, object?>? attrs, CancellationToken ct)
    {
        Tel.Decide(new Decision { Kind = "defense_action", Component = component, Level = level, Verdict = verdict, Reason = reason, Target = target, Attrs = attrs ?? new Dictionary<string, object?>() });
    }

    internal static void DefenseSnapshotSkip(string component, string target, Exception error, CancellationToken ct)
    {
        Tel.Decide(new Decision
        {
            Kind = "snapshot_skip",
            Component = component,
            Level = TelemetryLevel.Warn,
            Verdict = "skipped",
            Reason = "not_recorded",
            Target = target,
            Attrs = new Dictionary<string, object?> { ["error"] = error },
        });
    }
}
