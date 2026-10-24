namespace RimGovernor.Brain.Domain;

public enum ForecastMetric { FoodStock, FoodLevel, RestLevel, Mood, ShelterTemperature, TaskCompletion }
public enum ForecastAdmission { Selected, Admitted, Continued, Refused, Counterfactual }
/// <summary>The execution cohort: Unconfirmed until task reads show the method ran consistently or diverged.</summary>
public enum ForecastExecution { Unconfirmed, Consistent, Interrupted, Replaced, NotExecuted }

/// <summary>Sample is unique within a review; Method/Task/Revision are the existing execution identities.</summary>
public sealed record ForecastEvidenceKey(string Run, string Load, string Map, string Review, string Sample,
    ForecastMetric Metric, string Subject, string Scope, string Unit, string Method, string Task, long Revision);

/// <summary>A sample's value: the number predicted or read, or the reason the sample carries none.</summary>
public abstract record EvidenceValue
{
    private protected EvidenceValue() { }
    public sealed record Valued(double Value) : EvidenceValue;
    public sealed record Unavailable(string Reason) : EvidenceValue;
}

/// <summary>A task sample's completion evidence; ticks are absolute. Interval bounds describe (lower, upper].</summary>
public abstract record ForecastCompletion
{
    private protected ForecastCompletion() { }
    /// <summary>Not a task sample.</summary>
    public sealed record NotApplicable : ForecastCompletion;
    /// <summary>The observation's value is the completion tick.</summary>
    public sealed record Exact : ForecastCompletion;
    /// <summary>Unfinished at <paramref name="Lower"/> and complete at <paramref name="Upper"/>.</summary>
    public sealed record Interval(double Lower, double Upper) : ForecastCompletion;
    /// <summary>Complete at <paramref name="Upper"/>, with no earlier read of it unfinished.</summary>
    public sealed record CompletedBy(double Upper) : ForecastCompletion;
    public sealed record Unfinished : ForecastCompletion;
    /// <summary>No completion was read: the execution diverged or the pending row retired.</summary>
    public sealed record Unobserved : ForecastCompletion;
}

/// <summary>Scope includes stock census eligibility or physical shelter geometry, never just a recycled room ID.</summary>
public sealed record ForecastPrediction(ForecastEvidenceKey Key, long OriginTick, long TargetTick,
    EvidenceValue Value, ForecastAdmission Admission);

/// <summary>Tick is when read, not an invented finish time.</summary>
public sealed record ForecastObservation(ForecastEvidenceKey Key, long Tick, EvidenceValue Value,
    ForecastExecution Execution, ForecastCompletion Completion, string Provenance);
