using System.Text.Json;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Telemetry;
using RimGovernor.Brain.Telemetry.Otlp;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

[Collection(SpanWriters.Name)]
public sealed class ForecastResidualTests
{
    private static ForecastPrediction Prediction(ForecastMetric metric = ForecastMetric.FoodLevel, string review = "1") =>
        new(new("run", "load", "map", review, "sample", metric, "subject", "physical-scope", "unit", "method", "task", 1),
            100, 200, Fact.Known(metric == ForecastMetric.TaskCompletion ? 180d : 0.4), "", ForecastAdmission.Admitted);

    private static ForecastObservation Observation(ForecastPrediction p, double value = 0.6) =>
        new(p.Key, 200, Fact.Known(value), "", ForecastExecution.Consistent,
            p.Key.Metric == ForecastMetric.TaskCompletion ? ForecastCompletion.Exact : ForecastCompletion.NotApplicable,
            default, default, "native_read");

    [Theory]
    [InlineData(ForecastMetric.FoodStock)]
    [InlineData(ForecastMetric.FoodLevel)]
    [InlineData(ForecastMetric.RestLevel)]
    [InlineData(ForecastMetric.Mood)]
    [InlineData(ForecastMetric.ShelterTemperature)]
    [InlineData(ForecastMetric.TaskCompletion)]
    public void EachMetricUsesObservedMinusPredicted(ForecastMetric metric)
    {
        var p = Prediction(metric);
        var value = p.Value.Value + 0.2;
        var row = ForecastResiduals.Match(p, Observation(p, value));
        Assert.Equal("aligned", row.Reason);
        Assert.Equal(0.2, row.Scalar!.Value, 10);
        Assert.Equal(0, row.Lag);
    }

    [Fact]
    public void UnknownValuesMissingEvidenceAndScopeNeverBecomeZero()
    {
        var p = Prediction();
        var o = Observation(p);
        Assert.Equal("unknown_prediction", ForecastResiduals.Match(p with { Value = default, UnknownReason = "unavailable" }, o).Reason);
        Assert.Equal("unknown_observation", ForecastResiduals.Match(p, o with { Value = default, UnknownReason = "missing_need" }).Reason);
        Assert.Equal("missing_observation", ForecastResiduals.Match(p, null).Reason);
        Assert.Null(ForecastResiduals.Match(p, null).Scalar);
        foreach (var key in new[] { o.Key with { Scope = "changed_geometry" }, o.Key with { Subject = "other_resource" }, o.Key with { Unit = "other_unit" } })
            Assert.Equal("scope_mismatch", ForecastResiduals.Match(p, o with { Key = key }).Reason);
        Assert.Equal("identity_mismatch", ForecastResiduals.Match(p, o with { Key = o.Key with { Load = "another_load" } }).Reason);
        Assert.Equal("execution_identity_mismatch", ForecastResiduals.Match(p, o with { Key = o.Key with { Revision = 2 } }).Reason);
        Assert.Equal("invalid_observation_value", ForecastResiduals.Match(p, o with { Value = Fact.Known(double.NaN) }).Reason);
    }

    [Fact]
    public void LateObservationKeepsOperationalPairAndLagButHasNoAlignedError()
    {
        var p = Prediction();
        var row = ForecastResiduals.Match(p, Observation(p) with { Tick = 210 });
        Assert.Equal("tick_mismatch", row.Reason);
        Assert.Equal(10, row.Lag);
        Assert.Equal(0.6, row.Observation!.Value.Value);
        Assert.Null(row.Scalar);
        var summary = Assert.Single(ForecastResiduals.Summarize([row]));
        Assert.Equal(0, summary.Matched);
        Assert.Null(summary.Mean);
        Assert.Null(summary.PopulationDeviation);
        Assert.Equal(10, summary.MeanLag);
    }

    [Fact]
    public void CompletionTimeIsNotThePollingTickAndIntervalsHaveNoMidpoint()
    {
        var p = Prediction(ForecastMetric.TaskCompletion);
        var exact = ForecastResiduals.Match(p, Observation(p, 185) with { Tick = 250 });
        Assert.Equal(5, exact.Scalar);
        Assert.Equal(50, exact.Lag);
        var interval = ForecastResiduals.Match(p, Observation(p) with
        {
            Completion = ForecastCompletion.Interval, Value = default, UnknownReason = "interval_only",
            LowerTick = Fact.Known(170d), UpperTick = Fact.Known(190d),
        });
        Assert.Equal("interval", interval.Reason);
        Assert.Equal(-10, interval.Lower);
        Assert.Equal(10, interval.Upper);
        Assert.Null(interval.Scalar);
        var summary = Assert.Single(ForecastResiduals.Summarize([exact, interval]));
        Assert.Equal(2, summary.Matched);
        Assert.Equal(1, summary.Scalars);
        Assert.Equal(1, summary.Intervals);
        Assert.Equal(5, summary.Mean);
        Assert.Equal(0, summary.PopulationDeviation);
        Assert.Equal("unfinished", ForecastResiduals.Match(p, Observation(p) with { Completion = ForecastCompletion.Unfinished }).Reason);
        Assert.Equal("missing_completion_evidence", ForecastResiduals.Match(p, Observation(p) with { Completion = ForecastCompletion.Unknown }).Reason);
        Assert.Equal("unsupported_prediction_time", ForecastResiduals.Match(p with { Value = Fact.Known(201d) }, Observation(p, 190)).Reason);
    }

    [Fact]
    public void DuplicatesAndArrivalOrderDoNotWeightStatisticsOrHideCompletion()
    {
        var p = Prediction();
        var exact = Observation(p);
        var late = exact with { Tick = 210 };
        var a = Assert.Single(ForecastResiduals.Compare([p], [late, exact, exact]));
        var b = Assert.Single(ForecastResiduals.Compare([p], [exact, late]));
        Assert.Equal(a, b);
        Assert.Equal(1, Assert.Single(ForecastResiduals.Summarize([a])).Matched);
        var conflict = Assert.Single(ForecastResiduals.Compare([p], [exact, exact with { Value = Fact.Known(0.9) }]));
        Assert.Equal("conflicting_observations", conflict.Reason);
        Assert.Null(conflict.Scalar);
        var task = Prediction(ForecastMetric.TaskCompletion);
        var finished = Observation(task, 190) with { Tick = 250 };
        var unfinished = Observation(task) with { Completion = ForecastCompletion.Unfinished };
        Assert.Equal(10, Assert.Single(ForecastResiduals.Compare([task], [unfinished, finished])).Scalar);
    }

    [Theory]
    [InlineData(ForecastExecution.Consistent)]
    [InlineData(ForecastExecution.Interrupted)]
    [InlineData(ForecastExecution.Replaced)]
    [InlineData(ForecastExecution.Unknown)]
    [InlineData(ForecastExecution.NotExecuted)]
    public void ExecutionCohortsRetainOperationalResidualsSeparately(ForecastExecution execution)
    {
        var p = Prediction();
        var row = ForecastResiduals.Match(p, Observation(p) with { Execution = execution });
        Assert.Equal(execution, row.Cohort);
        Assert.Equal(execution, Assert.Single(ForecastResiduals.Summarize([row])).Cohort);
        Assert.NotNull(row.Scalar);
    }

    [Fact]
    public void RefusalCannotBeConsistentAndCounterfactualCannotBeScored()
    {
        var p = Prediction();
        var refused = ForecastResiduals.Match(p with { Admission = ForecastAdmission.Refused }, Observation(p));
        Assert.Equal(ForecastExecution.NotExecuted, refused.Cohort);
        Assert.Equal(ForecastAdmission.Refused, Assert.Single(ForecastResiduals.Summarize([refused])).Admission);
        var counterfactual = ForecastResiduals.Match(p with { Admission = ForecastAdmission.Counterfactual }, Observation(p));
        Assert.Equal("counterfactual", counterfactual.Reason);
        Assert.Null(counterfactual.Scalar);
        var continued = ForecastResiduals.Match(p with { Admission = ForecastAdmission.Continued }, Observation(p));
        Assert.Equal(ForecastExecution.Consistent, continued.Cohort);
        Assert.NotNull(continued.Scalar);
    }

    [Fact]
    public void SummarySeparatesResourcesUnitsAdmissionAndExecution()
    {
        var p = Prediction(ForecastMetric.FoodStock);
        var rows = new List<ForecastResidual>();
        foreach (var changed in new[] { p, p with { Key = p.Key with { Subject = "other" } }, p with { Key = p.Key with { Unit = "other" } }, p with { Admission = ForecastAdmission.Refused } })
            rows.Add(ForecastResiduals.Match(changed, Observation(changed)));
        rows.Add(ForecastResiduals.Match(p, Observation(p) with { Execution = ForecastExecution.Interrupted }));
        Assert.Equal(5, ForecastResiduals.Summarize(rows).Count);
        Assert.Empty(ForecastResiduals.Summarize([]));
    }

    [Fact]
    public async Task SerializedOtlpReaderAndReportPreserveEvidenceStatisticsAndGaps()
    {
        var directory = Path.Combine(Path.GetTempPath(), "forecast-residuals-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var writer = OtlpFileWriter.Start(new() { Dir = directory }))
            {
                var emitter = new TelemetryEmitter(writer);
                foreach (var (review, error) in new[] { ("1", -2d), ("2", 4d) })
                {
                    var p = Prediction(review: review) with { Key = Prediction(review: review).Key with { Revision = long.MaxValue } };
                    emitter.Event(TelemetryLevel.Info, "fixture", ForecastEvidenceReader.PredictionKind, "", ForecastEvidenceReader.Fields(p));
                    emitter.Event(TelemetryLevel.Info, "fixture", ForecastEvidenceReader.ObservationKind, "", ForecastEvidenceReader.Fields(Observation(p, p.Value.Value + error)));
                }
            }
            var path = Path.Combine(directory, "logs.jsonl");
            var evidence = ForecastEvidenceReader.Read(path);
            Assert.Empty(evidence.Gaps);
            Assert.Equal(long.MaxValue, evidence.Predictions[0].Key.Revision);
            var summary = Assert.Single(ForecastResiduals.Summarize(ForecastResiduals.Compare(evidence.Predictions, evidence.Observations)));
            Assert.Equal(2, summary.Matched);
            Assert.Equal(1, summary.Mean);
            Assert.Equal(3, summary.MeanAbsolute);
            Assert.Equal(3, summary.PopulationDeviation);
            using var report = JsonDocument.Parse(JsonSerializer.Serialize(ForecastResidualReport.Build(evidence)));
            Assert.Equal(2, report.RootElement.GetProperty("Rows").GetArrayLength());
            Assert.Equal(2, report.RootElement.GetProperty("Summary")[0].GetProperty("Reasons").GetProperty("aligned").GetInt32());
            File.AppendAllText(path, "not json\n{\"torn\":");
            var damaged = ForecastEvidenceReader.Read(path);
            Assert.Contains(damaged.Gaps, g => g.StartsWith("invalid_record:", StringComparison.Ordinal));
            Assert.Contains(damaged.Gaps, g => g.StartsWith("unterminated_segment:", StringComparison.Ordinal));
            Assert.Equal(2, damaged.Predictions.Count);
            Assert.Contains("missing_logs", ForecastEvidenceReader.Read(Path.Combine(directory, "missing.jsonl")).Gaps);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DecoderFlagsTruncationMissingPredictionsAndDuplicateEvidence()
    {
        var p = Prediction();
        var prediction = new TimelineRecord { Kind = ForecastEvidenceReader.PredictionKind, Payload = ForecastEvidenceReader.Fields(p) };
        var observation = new TimelineRecord { Kind = ForecastEvidenceReader.ObservationKind, Payload = ForecastEvidenceReader.Fields(Observation(p)) };
        var truncated = new TimelineRecord { Kind = ForecastEvidenceReader.PredictionKind, Payload = new Dictionary<string, object?> { ["truncated"] = true } };
        var orphan = ForecastEvidenceReader.Decode([observation, truncated]);
        Assert.Contains("truncated_record", orphan.Gaps);
        Assert.Contains("no_predictions", orphan.Gaps);
        Assert.Contains(orphan.Gaps, g => g.StartsWith("missing_prediction:", StringComparison.Ordinal));
        var duplicate = ForecastEvidenceReader.Decode([observation, prediction, prediction, observation]);
        Assert.Single(duplicate.Predictions);
        Assert.Contains("duplicate_observations:1", duplicate.Gaps);
        Assert.Single(ForecastResiduals.Compare(duplicate.Predictions, duplicate.Observations));
        var unknown = ForecastEvidenceReader.Fields(p with { Value = default, UnknownReason = "not_supported" });
        Assert.False(Assert.Single(ForecastEvidenceReader.Decode([prediction with { Payload = unknown }]).Predictions).Value.IsKnown);
        unknown["unknown_reason"] = "";
        Assert.Contains("invalid_evidence:unknown_reason", ForecastEvidenceReader.Decode([prediction with { Payload = unknown }]).Gaps);
    }
}
