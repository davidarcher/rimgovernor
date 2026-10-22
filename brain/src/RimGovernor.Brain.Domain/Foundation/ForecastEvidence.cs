namespace RimGovernor.Brain.Domain;

public enum ForecastMetric { FoodStock, FoodLevel, RestLevel, Mood, ShelterTemperature, TaskCompletion }
public enum ForecastAdmission { Selected, Admitted, Continued, Refused, Counterfactual }
public enum ForecastExecution { Unknown, Consistent, Interrupted, Replaced, NotExecuted }
public enum ForecastCompletion { NotApplicable, Exact, Interval, Unfinished, Unknown }

/// <summary>Sample is unique within a review; Method/Task/Revision are the existing execution identities.</summary>
public sealed record ForecastEvidenceKey(string Run, string Load, string Map, string Review, string Sample,
    ForecastMetric Metric, string Subject, string Scope, string Unit, string Method, string Task, long Revision);

/// <summary>Scope includes stock census eligibility or physical shelter geometry, never just a recycled room ID.</summary>
public sealed record ForecastPrediction(ForecastEvidenceKey Key, long OriginTick, long TargetTick,
    Fact<double> Value, string UnknownReason, ForecastAdmission Admission);

/// <summary>Tick is when read, not an invented finish time. Interval bounds describe (lower, upper].</summary>
public sealed record ForecastObservation(ForecastEvidenceKey Key, long Tick, Fact<double> Value,
    string UnknownReason, ForecastExecution Execution, ForecastCompletion Completion,
    Fact<double> LowerTick, Fact<double> UpperTick, string Provenance);

