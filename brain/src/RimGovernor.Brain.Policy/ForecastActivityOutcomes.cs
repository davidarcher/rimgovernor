namespace RimGovernor.Brain.Policy;

/// <summary>Alternative relief of one owned need, not inferred from an activity name. Completion includes all prerequisites.</summary>
public sealed record ForecastActivityOutcome(string Id, string Need, string Completion)
{
    public ForecastRequirementAssessment? Requirements { get; init; }
    /// <summary>Observed ongoing baseline work; null for a planned outcome. Its existing claims remain in the supplied availability.</summary>
    public ForecastRetainedWork? Retained { get; init; }
    public string ExclusiveOutput { get; init; } = "";
    public EquatableArray<string> Excludes { get; init; }
}

/// <summary>Observed work already under way, which completes <paramref name="CompletionTicks"/> after the forecast's tick.</summary>
public sealed record ForecastRetainedWork(double CompletionTicks);

/// <summary>The selected outcomes and their schedule, with what the search refused and whether its budget ran out.</summary>
public sealed record ForecastActivityOutcomeComparison(string[] Selected, ForecastActivityPlan? Plan,
    string[] Findings, int Expansions, bool SearchTruncated);
