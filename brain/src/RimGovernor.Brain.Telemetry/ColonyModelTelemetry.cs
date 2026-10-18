namespace RimGovernor.Brain.Telemetry;

/// <summary>
/// The tier-2 colony model's per-review row (the Rounder's <c>colony_model</c> event; docs/developers/contracts/otel-signals.md#kinds):
/// its kind and the attrs a reader selects. Reader: the <c>goals/research-goal</c> acceptance case.
/// </summary>
public static class ColonyModelTelemetry
{
    public const string Kind = "colony_model";

    /// <summary>Days simulated.</summary>
    public const string HorizonKey = "horizon_days";

    /// <summary>Events the run fired (the horizon end among them); the budget counts these.</summary>
    public const string EventsKey = "events";

    /// <summary>The day nutrition falls to the stock that bridges the harvest gap; absent when it does not within the run.</summary>
    public const string RunwayKey = "runway_day";

    /// <summary>Whether the run reached its event budget before the horizon.</summary>
    public const string TruncatedKey = "truncated";

    /// <summary>The run's value in capability-days; absent while unassessed.</summary>
    public const string ValueKey = "value";

    /// <summary>The nutrition stock at the end of the run.</summary>
    public const string NutritionEndKey = "nutrition_end";

    /// <summary>The projects completed, each as <c>day:id</c>, in order.</summary>
    public const string CompletionsKey = "completions";

    /// <summary>Capability-days an hour of each valued project's work adds (dV/dt), each as <c>node=value</c>, joined by <c>;</c>.</summary>
    public const string ProjectsKey = "dvdt";

    /// <summary>The run's findings and the input sections it could not start from.</summary>
    public const string FindingsKey = "findings";

    /// <summary>Wall-clock milliseconds the review's runs took (diagnostic; the engine's budget is events).</summary>
    public const string ElapsedKey = "elapsed_ms";
}
