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
            100, 200, new EvidenceValue.Valued(metric == ForecastMetric.TaskCompletion ? 180d : 0.4), ForecastAdmission.Admitted);

    private static ForecastObservation Observation(ForecastPrediction p, double value = 0.6) =>
        new(p.Key, 200, new EvidenceValue.Valued(value), ForecastExecution.Consistent,
            p.Key.Metric == ForecastMetric.TaskCompletion ? new ForecastCompletion.Exact() : new ForecastCompletion.NotApplicable(), "native_read");

    private static double Valued(EvidenceValue value) => Assert.IsType<EvidenceValue.Valued>(value).Value;
    private static double? Scalar(ForecastResidual row) => (row.Error as ResidualError.Scalar)?.Error;
    private static long? Lag(ForecastResidual row) => row.Observation is { } o ? o.Tick - row.Prediction.TargetTick : null;

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
        var value = Valued(p.Value) + 0.2;
        var row = ForecastResiduals.Match(p, Observation(p, value));
        Assert.Equal("aligned", row.Reason);
        Assert.Equal(0.2, Scalar(row)!.Value, 10);
        Assert.Equal(0, Lag(row));
    }

    [Fact]
    public void UnknownValuesMissingEvidenceAndScopeNeverBecomeZero()
    {
        var p = Prediction();
        var o = Observation(p);
        Assert.Equal("unknown_prediction", ForecastResiduals.Match(p with { Value = new EvidenceValue.Unavailable("unavailable") }, o).Reason);
        Assert.Equal("unknown_observation", ForecastResiduals.Match(p, o with { Value = new EvidenceValue.Unavailable("missing_need") }).Reason);
        Assert.Equal("missing_observation", ForecastResiduals.Match(p, null).Reason);
        Assert.IsType<ResidualError.Unmatched>(ForecastResiduals.Match(p, null).Error);
        foreach (var key in new[] { o.Key with { Scope = "changed_geometry" }, o.Key with { Subject = "other_resource" }, o.Key with { Unit = "other_unit" } })
            Assert.Equal("scope_mismatch", ForecastResiduals.Match(p, o with { Key = key }).Reason);
        Assert.Equal("identity_mismatch", ForecastResiduals.Match(p, o with { Key = o.Key with { Load = "another_load" } }).Reason);
        Assert.Equal("execution_identity_mismatch", ForecastResiduals.Match(p, o with { Key = o.Key with { Revision = 2 } }).Reason);
        Assert.Equal("invalid_observation_value", ForecastResiduals.Match(p, o with { Value = new EvidenceValue.Valued(double.NaN) }).Reason);
    }

    [Fact]
    public void LateObservationKeepsOperationalPairAndLagButHasNoAlignedError()
    {
        var p = Prediction();
        var row = ForecastResiduals.Match(p, Observation(p) with { Tick = 210 });
        Assert.Equal("tick_mismatch", row.Reason);
        Assert.Equal(10, Lag(row));
        Assert.Equal(0.6, Valued(row.Observation!.Value));
        Assert.Null(Scalar(row));
        var summary = Assert.Single(ForecastResiduals.Summarize([row]));
        Assert.Equal(0, summary.Matched);
        Assert.IsType<ResidualMoments.NoScalars>(summary.Moments);
        Assert.Equal(new ResidualLag.Mean(10), summary.Lag);
    }

    [Fact]
    public void CompletionTimeIsNotThePollingTickAndIntervalsHaveNoMidpoint()
    {
        var p = Prediction(ForecastMetric.TaskCompletion);
        var exact = ForecastResiduals.Match(p, Observation(p, 185) with { Tick = 250 });
        Assert.Equal(5, Scalar(exact));
        Assert.Equal(50, Lag(exact));
        var interval = ForecastResiduals.Match(p, Observation(p) with
        {
            Completion = new ForecastCompletion.Interval(170, 190), Value = new EvidenceValue.Unavailable("interval_only"),
        });
        Assert.Equal("interval", interval.Reason);
        Assert.Equal(new ResidualError.Interval(-10, 10), interval.Error);
        var summary = Assert.Single(ForecastResiduals.Summarize([exact, interval]));
        Assert.Equal(2, summary.Matched);
        Assert.Equal(1, summary.Scalars);
        Assert.Equal(1, summary.Intervals);
        Assert.Equal(new ResidualMoments.Measured(5, 5, 0), summary.Moments);
        Assert.Equal("unfinished", ForecastResiduals.Match(p, Observation(p) with { Completion = new ForecastCompletion.Unfinished() }).Reason);
        Assert.Equal("missing_completion_evidence", ForecastResiduals.Match(p, Observation(p) with { Completion = new ForecastCompletion.Unobserved() }).Reason);
        Assert.Equal("unsupported_prediction_time", ForecastResiduals.Match(p with { Value = new EvidenceValue.Valued(201d) }, Observation(p, 190)).Reason);
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
        var conflict = Assert.Single(ForecastResiduals.Compare([p], [exact, exact with { Value = new EvidenceValue.Valued(0.9) }]));
        Assert.Equal("conflicting_observations", conflict.Reason);
        Assert.Null(Scalar(conflict));
        var task = Prediction(ForecastMetric.TaskCompletion);
        var finished = Observation(task, 190) with { Tick = 250 };
        var unfinished = Observation(task) with { Completion = new ForecastCompletion.Unfinished() };
        Assert.Equal(10, Scalar(Assert.Single(ForecastResiduals.Compare([task], [unfinished, finished]))));
    }

    [Theory]
    [InlineData(ForecastExecution.Consistent)]
    [InlineData(ForecastExecution.Interrupted)]
    [InlineData(ForecastExecution.Replaced)]
    [InlineData(ForecastExecution.Unconfirmed)]
    [InlineData(ForecastExecution.NotExecuted)]
    public void ExecutionCohortsRetainOperationalResidualsSeparately(ForecastExecution execution)
    {
        var p = Prediction();
        var row = ForecastResiduals.Match(p, Observation(p) with { Execution = execution });
        Assert.Equal(execution, row.Cohort);
        Assert.Equal(execution, Assert.Single(ForecastResiduals.Summarize([row])).Cohort);
        Assert.NotNull(Scalar(row));
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
        Assert.Null(Scalar(counterfactual));
        var continued = ForecastResiduals.Match(p with { Admission = ForecastAdmission.Continued }, Observation(p));
        Assert.Equal(ForecastExecution.Consistent, continued.Cohort);
        Assert.NotNull(Scalar(continued));
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
                    emitter.Decide(new() { Kind = TelemetryKinds.ForecastPrediction.Name, Component = "rounds", Verdict = "captured", Reason = "selected_forecast", Target = p.Key.Sample, Attrs = ForecastEvidenceReader.Fields(p) });
                    emitter.Decide(new() { Kind = TelemetryKinds.ForecastObservation.Name, Component = "rounds", Verdict = "observed", Reason = "native_observation", Target = p.Key.Sample, Attrs = ForecastEvidenceReader.Fields(Observation(p, Valued(p.Value) + error)) });
                }
            }
            var path = Path.Combine(directory, "logs.jsonl");
            var evidence = ForecastEvidenceReader.Read(path);
            Assert.Empty(evidence.Gaps);
            Assert.Equal(long.MaxValue, evidence.Predictions[0].Key.Revision);
            var summary = Assert.Single(ForecastResiduals.Summarize(ForecastResiduals.Compare(evidence.Predictions, evidence.Observations)));
            Assert.Equal(2, summary.Matched);
            Assert.Equal(new ResidualMoments.Measured(1, 3, 3), summary.Moments);
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
        var prediction = new TimelineRecord { Kind = ForecastEvidenceReader.PredictionKind, Payload = new Dictionary<string, object?> { [RimGovernor.Brain.Telemetry.Decision.AttrsKey] = ForecastEvidenceReader.Fields(p) } };
        var observation = new TimelineRecord { Kind = ForecastEvidenceReader.ObservationKind, Payload = new Dictionary<string, object?> { [RimGovernor.Brain.Telemetry.Decision.AttrsKey] = ForecastEvidenceReader.Fields(Observation(p)) } };
        var truncated = new TimelineRecord { Kind = ForecastEvidenceReader.PredictionKind, Payload = new Dictionary<string, object?> { ["truncated"] = true } };
        var orphan = ForecastEvidenceReader.Decode([observation, truncated]);
        Assert.Contains("truncated_record", orphan.Gaps);
        Assert.Contains("no_predictions", orphan.Gaps);
        Assert.Contains(orphan.Gaps, g => g.StartsWith("missing_prediction:", StringComparison.Ordinal));
        var duplicate = ForecastEvidenceReader.Decode([observation, prediction, prediction, observation]);
        Assert.Single(duplicate.Predictions);
        Assert.Contains("duplicate_observations:1", duplicate.Gaps);
        Assert.Single(ForecastResiduals.Compare(duplicate.Predictions, duplicate.Observations));
        var unknown = ForecastEvidenceReader.Fields(p with { Value = new EvidenceValue.Unavailable("not_supported") });
        Assert.False(Assert.Single(ForecastEvidenceReader.Decode([prediction with { Payload = new Dictionary<string, object?> { [RimGovernor.Brain.Telemetry.Decision.AttrsKey] = unknown } }]).Predictions).Value is EvidenceValue.Valued);
        unknown["unknown_reason"] = "";
        Assert.Contains("invalid_evidence:unknown_reason", ForecastEvidenceReader.Decode([prediction with { Payload = new Dictionary<string, object?> { [RimGovernor.Brain.Telemetry.Decision.AttrsKey] = unknown } }]).Gaps);
    }

    public static TheoryData<string> Completions() => new() { "NotApplicable", "Exact", "Interval", "CompletedBy", "Unfinished", "Unobserved" };

    [Theory]
    [MemberData(nameof(Completions))]
    public void EveryCompletionCaseAndExecutionStateRoundTripsThroughTheReader(string name)
    {
        ForecastCompletion completion = name switch
        {
            "NotApplicable" => new ForecastCompletion.NotApplicable(),
            "Exact" => new ForecastCompletion.Exact(),
            "Interval" => new ForecastCompletion.Interval(120, 160),
            "CompletedBy" => new ForecastCompletion.CompletedBy(160),
            "Unfinished" => new ForecastCompletion.Unfinished(),
            _ => new ForecastCompletion.Unobserved(),
        };
        var p = Prediction(ForecastMetric.TaskCompletion);
        var o = Observation(p) with { Value = new EvidenceValue.Unavailable("execution_diverged"), Execution = ForecastExecution.Unconfirmed, Completion = completion };
        TimelineRecord Row(string kind, Dictionary<string, object?> fields) =>
            new() { Kind = kind, Payload = new Dictionary<string, object?> { [RimGovernor.Brain.Telemetry.Decision.AttrsKey] = fields } };
        var fields = ForecastEvidenceReader.Fields(o);
        Assert.Equal(name, fields["completion"]);
        var read = ForecastEvidenceReader.Decode([Row(ForecastEvidenceReader.PredictionKind, ForecastEvidenceReader.Fields(p)), Row(ForecastEvidenceReader.ObservationKind, fields)]);
        Assert.Equal(o, Assert.Single(read.Observations));
        if (completion is ForecastCompletion.CompletedBy)
        {
            fields["upper_tick"] = null;
            Assert.Contains("invalid_evidence:upper_tick", ForecastEvidenceReader.Decode([Row(ForecastEvidenceReader.ObservationKind, fields)]).Gaps);
        }
    }

    [Fact]
    public void SummaryNamesMissingScalarsAndObservationsInsteadOfNulls()
    {
        var p = Prediction();
        var empty = Assert.Single(ForecastResiduals.Summarize([ForecastResiduals.Match(p, null)]));
        Assert.IsType<ResidualMoments.NoScalars>(empty.Moments);
        Assert.IsType<ResidualLag.NoObservations>(empty.Lag);
        var late = Assert.Single(ForecastResiduals.Summarize([ForecastResiduals.Match(p, Observation(p) with { Tick = 260 })]));
        Assert.IsType<ResidualMoments.NoScalars>(late.Moments);
        Assert.Equal(60, Assert.IsType<ResidualLag.Mean>(late.Lag).Ticks);
        var aligned = Assert.Single(ForecastResiduals.Summarize([ForecastResiduals.Match(p, Observation(p))]));
        Assert.Equal(0.2, Assert.IsType<ResidualMoments.Measured>(aligned.Moments).Mean, 9);
    }
}
