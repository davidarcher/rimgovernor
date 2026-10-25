namespace RimGovernor.Brain.Telemetry;

/// <summary>
/// The ColonyReview's per-review <c>layout_progress</c> event (docs/developers/contracts/otel-signals.md#kinds, #3751): how far
/// the held layout plan has come and how many rings stand open against the builders. Reader: colonyreview's layout section.
/// </summary>
public static class LayoutProgressTelemetry
{
    public const string Kind = "layout_progress";

    /// <summary>The held plan's rooms.</summary>
    public const string PlannedKey = "planned";

    /// <summary>The rooms with anything of ours on them (started), done ones included.</summary>
    public const string StartedKey = "started";

    /// <summary>The rooms done by their latest reconcile (built).</summary>
    public const string BuiltKey = "built";

    /// <summary>The open rooms' ids: started and not done, the rings the ring-closure projection closes first (#3744, #3754).</summary>
    public const string OpenKey = "open";

    /// <summary>The colonists the coupled comparison plans construction for.</summary>
    public const string BuildersKey = "builders";
}
