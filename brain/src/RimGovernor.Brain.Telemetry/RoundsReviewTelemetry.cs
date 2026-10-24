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

    /// <summary>The native hunt read: known, hunter and prey counts, findings.</summary>
    public const string HuntReadKey = "hunt_read";

    /// <summary>The coupled search's scheduler expansions, against <see cref="CoupledTraversalBoundKey"/>; -1 when nothing was compared.</summary>
    public const string CoupledSearchExpansionsKey = "coupled_search_expansions";

    /// <summary>The work the selection left unscheduled (#3825): per work type and stat, its ticks a day at speed 1; null when tier 2 did not run.</summary>
    public const string WorkShortfallKey = "work_shortfall";

    /// <summary>Per colonist its value in capability-days a day (#3825) and the parts: work added, food, break and bed upkeep; null when tier 2 did not run.</summary>
    public const string PawnValuesKey = "pawn_values";

    /// <summary>The coupled search's outcome evaluations (#3722), the baseline's included, against <see cref="CoupledEvaluationBudgetKey"/>; -1 when nothing was compared.</summary>
    public const string CoupledSearchEvaluationsKey = "coupled_search_evaluations";

    /// <summary>The mandatory baseline's outcome evaluations, counted in <see cref="CoupledSearchEvaluationsKey"/> and charged to no allowance.</summary>
    public const string CoupledBaselineEvaluationsKey = "coupled_baseline_evaluations";

    /// <summary>The mandatory baseline's scheduler expansions, counted in <see cref="CoupledSearchExpansionsKey"/> and charged to no allowance.</summary>
    public const string CoupledBaselineExpansionsKey = "coupled_baseline_expansions";

    /// <summary>The outcome-evaluation budget the comparison spent against.</summary>
    public const string CoupledEvaluationBudgetKey = "coupled_evaluation_budget";

    /// <summary>The traversal bound on scheduler expansions the comparison spent against.</summary>
    public const string CoupledTraversalBoundKey = "coupled_traversal_bound";

    /// <summary>Per search phase reached, its scheduler expansions (#3563).</summary>
    public const string SearchPhasesKey = "search_phases";

    /// <summary>Per search phase reached, its profile (#3556): <c>ms</c>, <c>schedule_searches</c>, <c>outcome_evaluations</c>, <c>schedule_ms</c>, <c>infeasible</c>, <c>unsupported</c>, <c>swaps</c>, <c>improved</c>.</summary>
    public const string SearchProfileKey = "search_profile";
}
