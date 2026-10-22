namespace RimGovernor.Brain.Policy;

public sealed record ForecastResidual(ForecastPrediction Prediction, ForecastObservation? Observation,
    ForecastExecution Cohort, string Reason, double? Scalar, double? Lower, double? Upper, long? Lag);

public sealed record ForecastResidualSummary(ForecastMetric Metric, string Unit, string Resource, string Scope,
    ForecastExecution Cohort, ForecastAdmission Admission, int Samples, int Matched, int Scalars, int Intervals,
    double? Mean, double? MeanAbsolute, double? PopulationDeviation, double? MeanLag,
    IReadOnlyDictionary<string, int> Reasons);

/// <summary>Pure evidence comparison. Execution cohorts describe detected consistency, not causal isolation.</summary>
public static class ForecastResiduals
{
    public static ForecastResidual Match(ForecastPrediction prediction, ForecastObservation? observation)
    {
        ArgumentNullException.ThrowIfNull(prediction);
        var cohort = prediction.Admission == ForecastAdmission.Refused ? ForecastExecution.NotExecuted : observation?.Execution ?? ForecastExecution.Unknown;
        ForecastResidual Result(string reason, double? scalar = null, double? lower = null, double? upper = null) =>
            new(prediction, observation, cohort, reason, scalar, lower, upper,
                observation is null ? null : observation.Tick - prediction.TargetTick);
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
        if (!prediction.Value.TryGetValue(out var predicted)) return Result("unknown_prediction");
        if (!double.IsFinite(predicted)) return Result("invalid_prediction_value");
        if (p.Metric == ForecastMetric.TaskCompletion)
        {
            if (predicted < prediction.OriginTick || predicted > prediction.TargetTick) return Result("unsupported_prediction_time");
            if (observation.Completion == ForecastCompletion.Unfinished) return Result("unfinished");
            if (observation.Completion == ForecastCompletion.Interval)
            {
                if (!observation.LowerTick.TryGetValue(out var lo) || !observation.UpperTick.TryGetValue(out var hi))
                    return Result("unknown_interval");
                if (!double.IsFinite(lo) || !double.IsFinite(hi) || lo >= hi || lo < prediction.OriginTick || hi > observation.Tick)
                    return Result("invalid_interval");
                return Result("interval", lower: lo - predicted, upper: hi - predicted);
            }
            if (observation.Completion != ForecastCompletion.Exact) return Result("missing_completion_evidence");
        }
        else if (observation.Completion != ForecastCompletion.NotApplicable) return Result("unexpected_completion_evidence");
        if (!observation.Value.TryGetValue(out var observed)) return Result("unknown_observation");
        if (!double.IsFinite(observed)) return Result("invalid_observation_value");
        if (p.Metric == ForecastMetric.TaskCompletion)
        {
            if (observed < prediction.OriginTick || observed > observation.Tick) return Result("invalid_completion_tick");
        }
        else if (observation.Tick != prediction.TargetTick) return Result("tick_mismatch");
        return Result("aligned", observed - predicted);
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
                    && o.Completion == ForecastCompletion.Exact ? 0 : p.Key.Metric == ForecastMetric.TaskCompletion
                    && o.Completion == ForecastCompletion.Interval ? 1 : 2)
                .ThenBy(o => Math.Abs(o.Tick - p.TargetTick)).ThenBy(o => o.Tick).ToArray();
            if (candidates.Length == 0) return Match(p, null);
            var chosen = candidates[0];
            var result = Match(p, chosen);
            return candidates.Skip(1).Any(o => o.Tick == chosen.Tick && o != chosen)
                ? result with { Reason = "conflicting_observations", Scalar = null, Lower = null, Upper = null }
                : result;
        }).ToArray();
    }
    private static string EvidenceReason(ForecastResidual row) =>
        row.Reason == "unknown_prediction" && row.Prediction.UnknownReason.Length > 0 ? row.Reason + ":" + row.Prediction.UnknownReason
        : row.Reason is "unknown_observation" or "missing_completion_evidence" && row.Observation is { UnknownReason.Length: > 0 } observation
            ? row.Reason + ":" + observation.UnknownReason : row.Reason;

    public static IReadOnlyList<ForecastResidualSummary> Summarize(IEnumerable<ForecastResidual> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.GroupBy(r => (r.Prediction.Key.Metric, r.Prediction.Key.Unit,
                Resource: r.Prediction.Key.Metric == ForecastMetric.FoodStock ? r.Prediction.Key.Subject : "",
                r.Prediction.Key.Scope, r.Cohort, r.Prediction.Admission))
            .Select(g =>
            {
                var scalar = g.Where(r => r.Scalar.HasValue).Select(r => r.Scalar!.Value).ToArray();
                var lag = g.Where(r => r.Lag.HasValue).Select(r => (double)r.Lag!.Value).ToArray();
                double? mean = scalar.Length == 0 ? null : scalar.Average();
                var intervals = g.Count(r => r.Lower.HasValue && r.Upper.HasValue);
                return new ForecastResidualSummary(g.Key.Metric, g.Key.Unit, g.Key.Resource, g.Key.Scope, g.Key.Cohort, g.Key.Admission,
                    g.Count(), scalar.Length + intervals, scalar.Length, intervals, mean,
                    scalar.Length == 0 ? null : scalar.Average(Math.Abs),
                    mean is null ? null : Math.Sqrt(scalar.Average(v => (v - mean.Value) * (v - mean.Value))),
                    lag.Length == 0 ? null : lag.Average(),
                    g.GroupBy(EvidenceReason).ToDictionary(r => r.Key, r => r.Count(), StringComparer.Ordinal));
            }).ToArray();
    }
}
