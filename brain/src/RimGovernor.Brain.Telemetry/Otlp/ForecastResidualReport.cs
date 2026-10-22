using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Telemetry.Otlp;

/// <summary>Inspectable report over retained evidence; never claims that a rotated ring is a complete experiment.</summary>
public static class ForecastResidualReport
{
    public static object Build(ForecastEvidenceRead evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var rows = ForecastResiduals.Compare(evidence.Predictions, evidence.Observations);
        return new
        {
            History = "retained_log_window_only",
            evidence.Gaps,
            Handoffs = evidence.Handoffs.Select(h => new { h.Day, h.EndDay, h.Available, h.Gaps }),
            Summary = ForecastResiduals.Summarize(rows).Select(s => new
            {
                Metric = s.Metric.ToString(), s.Unit, s.Resource, s.Scope, Cohort = s.Cohort.ToString(),
                Admission = s.Admission.ToString(), s.Samples, s.Matched, s.Scalars, s.Intervals,
                s.Mean, s.MeanAbsolute, s.PopulationDeviation, s.MeanLag, s.Reasons,
            }),
            Rows = rows.Select(r => new
            {
                Prediction = ForecastEvidenceReader.Fields(r.Prediction),
                Observation = r.Observation is null ? null : ForecastEvidenceReader.Fields(r.Observation),
                Cohort = r.Cohort.ToString(), r.Reason, r.Scalar, r.Lower, r.Upper, r.Lag,
            }),
        };
    }
}
