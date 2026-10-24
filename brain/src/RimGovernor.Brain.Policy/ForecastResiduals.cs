namespace RimGovernor.Brain.Policy;

/// <summary>A matched residual: the observed minus the predicted value, the completion interval's bounds minus the predicted
/// tick, or none when the pair does not align (<see cref="ForecastResidual.Reason"/> says why).</summary>
public abstract record ResidualError
{
    private ResidualError() { }
    public sealed record Unmatched : ResidualError;
    public sealed record Scalar(double Error) : ResidualError;
    public sealed record Interval(double Lower, double Upper) : ResidualError;
}

/// <summary>The scalar residuals' moments, or none when a summary has no scalar residual.</summary>
public abstract record ResidualMoments
{
    private ResidualMoments() { }
    public sealed record NoScalars : ResidualMoments;
    public sealed record Measured(double Mean, double MeanAbsolute, double PopulationDeviation) : ResidualMoments;
}

/// <summary>A summary's mean observation lag past the target tick, or none when no prediction found an observation.</summary>
public abstract record ResidualLag
{
    private ResidualLag() { }
    public sealed record NoObservations : ResidualLag;
    public sealed record Mean(double Ticks) : ResidualLag;
}

/// <summary>One prediction's comparison; <see cref="Observation"/> is null when none was found.</summary>
public sealed record ForecastResidual(ForecastPrediction Prediction, ForecastObservation? Observation,
    ForecastExecution Cohort, string Reason, ResidualError Error);

public sealed record ForecastResidualSummary(ForecastMetric Metric, string Unit, string Resource, string Scope,
    ForecastExecution Cohort, ForecastAdmission Admission, int Samples, int Matched, int Scalars, int Intervals,
    ResidualMoments Moments, ResidualLag Lag, IReadOnlyDictionary<string, int> Reasons);

/// <summary>Pure evidence comparison. Execution cohorts describe detected consistency, not causal isolation.</summary>
public static class ForecastResiduals
{
    public static ForecastResidual Match(ForecastPrediction prediction, ForecastObservation? observation)
    {
        ArgumentNullException.ThrowIfNull(prediction);
        var cohort = prediction.Admission == ForecastAdmission.Refused ? ForecastExecution.NotExecuted : observation?.Execution ?? ForecastExecution.Unconfirmed;
        ForecastResidual Result(string reason, ResidualError? error = null) =>
            new(prediction, observation, cohort, reason, error ?? new ResidualError.Unmatched());
        if (prediction.Admission == ForecastAdmission.Counterfactual) return Result("counterfactual");
        if (prediction.OriginTick < 0 || prediction.TargetTick < prediction.OriginTick) return Result("invalid_prediction_ticks");
        if (observation is null) return Result("missing_observation");
        var p = prediction.Key;
        var o = observation.Key;
        if (p.Run != o.Run || p.Load != o.Load || p.Map != o.Map || p.Review != o.Review || p.Sample != o.Sample)
            return Result("identity_mismatch");
        if (p.Metric != o.Metric || p.Subject != o.Subject || p.Unit != o.Unit || p.Scope != o.Scope)
            return Result("scope_mismatch");
        if (p.Method != o.Method || p.Task != o.Task || p.Revision != o.Revision) return Result("execution_identity_mismatch");
        if (observation.Tick < prediction.OriginTick) return Result("observation_before_origin");
        if (prediction.Value is not EvidenceValue.Valued(var predicted)) return Result("unknown_prediction");
        if (!double.IsFinite(predicted)) return Result("invalid_prediction_value");
        if (p.Metric == ForecastMetric.TaskCompletion)
        {
            if (predicted < prediction.OriginTick || predicted > prediction.TargetTick) return Result("unsupported_prediction_time");
            if (observation.Completion is ForecastCompletion.Unfinished) return Result("unfinished");
            if (observation.Completion is ForecastCompletion.Interval(var lo, var hi))
            {
                if (!double.IsFinite(lo) || !double.IsFinite(hi) || lo >= hi || lo < prediction.OriginTick || hi > observation.Tick)
                    return Result("invalid_interval");
                return Result("interval", new ResidualError.Interval(lo - predicted, hi - predicted));
            }
            if (observation.Completion is not ForecastCompletion.Exact) return Result("missing_completion_evidence");
        }
        else if (observation.Completion is not ForecastCompletion.NotApplicable) return Result("unexpected_completion_evidence");
        if (observation.Value is not EvidenceValue.Valued(var observed)) return Result("unknown_observation");
        if (!double.IsFinite(observed)) return Result("invalid_observation_value");
        if (p.Metric == ForecastMetric.TaskCompletion)
        {
            if (observed < prediction.OriginTick || observed > observation.Tick) return Result("invalid_completion_tick");
        }
        else if (observation.Tick != prediction.TargetTick) return Result("tick_mismatch");
        return Result("aligned", new ResidualError.Scalar(observed - predicted));
    }

    public static IReadOnlyList<ForecastResidual> Compare(IEnumerable<ForecastPrediction> predictions,
        IEnumerable<ForecastObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(predictions);
        ArgumentNullException.ThrowIfNull(observations);
        var lookup = observations.ToLookup(o => (o.Key.Run, o.Key.Load, o.Key.Map, o.Key.Review, o.Key.Sample));
        return predictions.Select(p =>
        {
            var candidates = lookup[(p.Key.Run, p.Key.Load, p.Key.Map, p.Key.Review, p.Key.Sample)].Distinct()
                .OrderBy(o => p.Key.Metric == ForecastMetric.TaskCompletion
                    && o.Completion is ForecastCompletion.Exact ? 0 : p.Key.Metric == ForecastMetric.TaskCompletion
                    && o.Completion is ForecastCompletion.Interval ? 1 : 2)
                .ThenBy(o => Math.Abs(o.Tick - p.TargetTick)).ThenBy(o => o.Tick).ToArray();
            if (candidates.Length == 0) return Match(p, null);
            var chosen = candidates[0];
            var result = Match(p, chosen);
            return candidates.Skip(1).Any(o => o.Tick == chosen.Tick && o != chosen)
                ? result with { Reason = "conflicting_observations", Error = new ResidualError.Unmatched() }
                : result;
        }).ToArray();
    }

    private static string EvidenceReason(ForecastResidual row) =>
        row.Reason == "unknown_prediction" && row.Prediction.Value is EvidenceValue.Unavailable { Reason.Length: > 0 } prediction
            ? row.Reason + ":" + prediction.Reason
        : row.Reason is "unknown_observation" or "missing_completion_evidence"
            && row.Observation?.Value is EvidenceValue.Unavailable { Reason.Length: > 0 } observation
            ? row.Reason + ":" + observation.Reason : row.Reason;

    public static IReadOnlyList<ForecastResidualSummary> Summarize(IEnumerable<ForecastResidual> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.GroupBy(r => (r.Prediction.Key.Metric, r.Prediction.Key.Unit,
                Resource: r.Prediction.Key.Metric == ForecastMetric.FoodStock ? r.Prediction.Key.Subject : "",
                r.Prediction.Key.Scope, r.Cohort, r.Prediction.Admission))
            .Select(g =>
            {
                var scalar = g.Select(r => r.Error).OfType<ResidualError.Scalar>().Select(r => r.Error).ToArray();
                var lag = g.Where(r => r.Observation is not null).Select(r => (double)(r.Observation!.Tick - r.Prediction.TargetTick)).ToArray();
                var intervals = g.Count(r => r.Error is ResidualError.Interval);
                ResidualMoments moments = new ResidualMoments.NoScalars();
                if (scalar.Length > 0)
                {
                    var mean = scalar.Average();
                    moments = new ResidualMoments.Measured(mean, scalar.Average(Math.Abs), Math.Sqrt(scalar.Average(v => (v - mean) * (v - mean))));
                }
                return new ForecastResidualSummary(g.Key.Metric, g.Key.Unit, g.Key.Resource, g.Key.Scope, g.Key.Cohort, g.Key.Admission,
                    g.Count(), scalar.Length + intervals, scalar.Length, intervals, moments,
                    lag.Length == 0 ? new ResidualLag.NoObservations() : new ResidualLag.Mean(lag.Average()),
                    g.GroupBy(EvidenceReason).ToDictionary(r => r.Key, r => r.Count(), StringComparer.Ordinal));
            }).ToArray();
    }
}
