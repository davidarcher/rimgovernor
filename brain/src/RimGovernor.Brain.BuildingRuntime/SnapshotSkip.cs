namespace RimGovernor.Brain.BuildingRuntime;

internal static partial class BuildingRuntimePackage
{
    /// <summary>
    /// The snapshot_skip row of a snapshot the recorder could not write: target the snapshot (colony, combat,
    /// defense, layout, firebreak), reason not_recorded, WARN, the write error as an attr.
    /// </summary>
    internal static Decision SnapshotSkipDecision(string component, string target, Exception err, Tick tick) =>
        new()
        {
            Kind = "snapshot_skip",
            Component = component,
            Level = TelemetryLevel.Warn,
            Verdict = "skipped",
            Reason = "not_recorded",
            Target = target,
            Attrs = new Dictionary<string, object?> { ["error"] = err, ["tick"] = tick.Value },
        };
}
