using System.Globalization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;
using RimGovernor.Brain.Telemetry;
using RimGovernor.Brain.Telemetry.Otlp;
using Fx = RimGovernor.Fixtures.Contracts;

namespace RimGovernor.Brain.Acceptance.Cases;

/// <summary>A whole production horizon with no changes to the precondition after service starts.</summary>
internal static class ForecastResidualCase
{
    private static readonly string[] NoShelterPrediction = ["shelter_prediction_unavailable"];
    // A quarter day allows the initial review and a subsequent ordinary review after its forecast horizon.
    private const int ObservationSlackTicks = (int)(GameTime.TicksPerDay / 4);
    private static readonly HorizonObservation Horizon = new()
    {
        Horizon = ProductionObservationHorizon.CoupledForecast,
        ClaimReason = "Injecting endpoint needs or task completion would replace the native observations being compared with the original forecast.",
        ObservationSlackTicks = ObservationSlackTicks,
        ObservationSlackReason = "One quarter day for initial review and the first ordinary review after its target; no forced review or clock wake.",
    };

    internal static TypedCase Case() => new()
    {
        Name = "production/forecast-residuals",
        Scope = "A live selected forecast and later native food, rest, actual mood and completed-task reads correlate through the registered residual reader. "
            + "A unit test cannot prove native evolution or served task completion. These needs remain unfrozen for the full production horizon; quiet-world removes irrelevant wildlife.",
        Ticks = Horizon.Budget,
        ObservesHorizon = Horizon,
        Tier = CaseTier.OnDemand,
        Crew = new Crew { Size = 1 },
        Keep = ["Food", "Rest", "Joy", "Mood"],
        QuietWorld = true,
        Serve = new ServeSpec { Prefix = "forecast-residuals" },
        Stage = new Fx.StageSpec
        {
            Paused = true,
            World = new Fx.StageWorld { OutdoorTemperature = 21 },
            Site = new Fx.StageSite { Width = 12, Height = 8, Radius = 30, Require = [Fx.StageGround.Standable, Fx.StageGround.Heavy, Fx.StageGround.Unzoned, Fx.StageGround.Unfogged] },
            Clear = [new Fx.StageClear { Rect = new(0, 0, 12, 8), Kinds = [Fx.StageClearKind.Things, Fx.StageClearKind.Zones, Fx.StageClearKind.Roofs] }],
            Cells = [new Fx.StageCells { Rect = new(0, 0, 12, 8), Home = true }],
            Rooms = [new Fx.StageRoom { Name = "bedroom", Rect = new(0, 0, 6, 6), Stuff = "WoodLog", Doors = [new(5, 3)], Roof = true }],
            Buildings = [new Fx.StageBuilding { Name = "bed", Def = "Bed", Cells = [new(2, 2)] }, new Fx.StageBuilding { Name = "pin", Def = "HorseshoesPin", Cells = [new(9, 3)] }],
            Things = [new Fx.StageThing { Name = "meals", Def = "MealSurvivalPack", Count = 30, Cell = new(3, 4), Near = true }],
            Pawns = [new Fx.StagePawn { Name = "worker", Colonist = 0, Cell = new(3, 2), AllWork = 0, Timetable = Fx.StageTimeAssignment.Work,
                Needs = new Dictionary<string, float> { ["Food"] = .35f, ["Rest"] = .15f, ["Joy"] = .6f } }],
        },
        Run = RunAsync,
    };

    private static async Task RunAsync(TypedSession s, CancellationToken cancellationToken)
    {
        var after = s.Telemetry(TelemetryKinds.ForecastPrediction).Select(r => r.Sequence).DefaultIfEmpty().Max();
        _ = await s.ServeUntilAsync("native-residuals", s.TicksLeft, ct =>
        {
            ct.ThrowIfCancellationRequested();
            var evidence = ForecastEvidenceReader.Decode(s.Telemetry(TelemetryKinds.ForecastPrediction)
                .Concat(s.Telemetry(TelemetryKinds.ForecastObservation)).Where(r => r.Sequence > after).Select(r => r.Record()));
            var residuals = ForecastResiduals.Compare(evidence.Predictions, evidence.Observations);
            s.Record("forecast_residuals", OutputJson.Fields(
                ("history", "retained_log_window_only"),
                ("reader_gaps", OutputJson.Array(evidence.Gaps)),
                ("coverage_gaps", OutputJson.Array(evidence.Predictions.Where(p => !p.Value.IsKnown).Select(p => p.UnknownReason)
                    .Concat(evidence.Observations.Where(o => !o.Value.IsKnown && o.Completion != ForecastCompletion.Interval).Select(o => o.UnknownReason))
                    .Concat(evidence.Predictions.Any(p => p.Key.Metric == ForecastMetric.ShelterTemperature) ? [] : NoShelterPrediction).Distinct())),
                ("summary", OutputJson.Array(ForecastResiduals.Summarize(residuals), r => OutputJson.Fields(
                    ("metric", r.Metric.ToString()), ("cohort", r.Cohort.ToString()), ("admission", r.Admission.ToString()),
                    ("samples", r.Samples), ("scalars", r.Scalars), ("intervals", r.Intervals), ("mean_lag", r.MeanLag),
                    ("reasons", OutputJson.Array(r.Reasons, reason => OutputJson.Fields(("reason", reason.Key), ("count", reason.Value))))))),
                ("rows", OutputJson.Array(residuals, r => OutputJson.Fields(
                    ("run", r.Prediction.Key.Run), ("load", r.Prediction.Key.Load), ("map", r.Prediction.Key.Map),
                    ("review", r.Prediction.Key.Review), ("sample", r.Prediction.Key.Sample), ("metric", r.Prediction.Key.Metric.ToString()),
                    ("subject", r.Prediction.Key.Subject), ("scope", r.Prediction.Key.Scope), ("unit", r.Prediction.Key.Unit),
                    ("method", r.Prediction.Key.Method), ("task", r.Prediction.Key.Task), ("revision", r.Prediction.Key.Revision),
                    ("origin_tick", r.Prediction.OriginTick), ("target_tick", r.Prediction.TargetTick), ("observation_tick", r.Observation?.Tick),
                    ("prediction", r.Prediction.Value.TryGetValue(out var predicted) ? predicted : null),
                    ("observation", r.Observation?.Value.TryGetValue(out var observed) == true ? observed : null),
                    ("prediction_unknown", r.Prediction.UnknownReason), ("observation_unknown", r.Observation?.UnknownReason),
                    ("provenance", r.Observation?.Provenance), ("completion", r.Observation?.Completion.ToString()),
                    ("cohort", r.Cohort.ToString()), ("reason", r.Reason), ("scalar", r.Scalar),
                    ("lower_residual", r.Lower), ("upper_residual", r.Upper), ("lag", r.Lag))))));
            var lastTick = evidence.Observations.Select(o => o.Tick).Concat(evidence.Predictions.Select(p => p.OriginTick)).DefaultIfEmpty().Max();
            return Task.FromResult((lastTick.ToString(CultureInfo.InvariantCulture), Complete(residuals)));
        }, cancellationToken).ConfigureAwait(false);
    }

    internal static bool Complete(IReadOnlyList<ForecastResidual> rows) =>
        new[] { ForecastMetric.FoodLevel, ForecastMetric.RestLevel, ForecastMetric.Mood }.All(metric =>
            rows.Any(r => r.Prediction.Key.Metric == metric && r.Observation is { Value.IsKnown: true, Provenance: "rounds_native_observation" }
                && r.Reason is "aligned" or "tick_mismatch"))
        && rows.Any(r => r.Prediction.Key.Metric == ForecastMetric.TaskCompletion
            && r.Observation?.Provenance == "native_task_read" && r.Reason is "interval" or "aligned");
}
