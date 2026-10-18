namespace RimGovernor.Brain.Telemetry;

/// <summary>
/// The rounds review's summary row (the Rounder's <c>rounds_review</c> event; docs/developers/contracts/otel-signals.md#kinds):
/// its kind and the attrs a reader selects. Reader: the <c>food/hunt-roster</c> acceptance case.
/// </summary>
public static class RoundsReviewTelemetry
{
    public const string Kind = "rounds_review";

    /// <summary>The food plan's Explain text: delivered, demand and gap, then one line per channel with its decision, reason and terms.</summary>
    public const string FoodPlanKey = "food_plan";

    /// <summary>The hunt comparison's note: the chosen roster key, why the others were unusable, the findings and a release reason.</summary>
    public const string HuntChoiceKey = "hunt_choice";

    /// <summary>The last restart of the hunt comparison: its tick and what drifted from the anchor; absent until one happened.</summary>
    public const string HuntRestartKey = "hunt_restart";

    /// <summary>The estimate service's state: rosters answered, pending and refused with reasons.</summary>
    public const string HuntEstimatesKey = "hunt_estimates";

    /// <summary>The native hunt read: known, hunter and prey counts, findings.</summary>
    public const string HuntReadKey = "hunt_read";
}
