using RimGovernor.Brain.Telemetry.Otlp;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>The sole disposable pending owner. Retains compact rows only, never forecast graphs or game intent.</summary>
internal sealed class ForecastEvidencePending
{
    internal const int Capacity = 4096;
    internal const long ExpiryTicks = GameTime.TicksPerDay;
    private readonly object _gate = new();
    private readonly string _run = Guid.NewGuid().ToString("N");
    private string _load = "", _map = "";
    private long _reviewTick = -1;
    private ulong? _capturedReview;
    private long _epoch;
    private readonly Dictionary<ForecastEvidenceKey, Pending> _pending = [];
    private readonly Action<ForecastPrediction> _prediction;
    private readonly Action<ForecastObservation> _observation;
    private sealed class Pending(ForecastPrediction prediction)
    {
        internal readonly ForecastPrediction Prediction = prediction;
        internal ForecastExecution Execution = prediction.Admission == ForecastAdmission.Refused ? ForecastExecution.NotExecuted : ForecastExecution.Unknown;
        internal long? Unfinished;
        internal long LastTaskTick = -1;
        internal bool Seen;
        internal bool ConsistentStart;
    }
    internal ForecastEvidencePending(Action<ForecastPrediction>? prediction = null, Action<ForecastObservation>? observation = null)
    {
        _prediction = prediction ?? (p => Tel.Decide(new Decision { Kind = TelemetryKinds.ForecastPrediction.Name,
            Component = "rounds", Target = p.Key.Sample, Verdict = "captured", Reason = "selected_forecast", Attrs = ForecastEvidenceReader.Fields(p) }));
        _observation = observation ?? (o => Tel.Decide(new Decision { Kind = TelemetryKinds.ForecastObservation.Name,
            Component = "rounds", Target = o.Key.Sample, Verdict = o.Value.IsKnown || o.Completion == ForecastCompletion.Interval ? "observed" : "unknown",
            Reason = o.UnknownReason.Length == 0 ? "native_observation" : o.UnknownReason, Attrs = ForecastEvidenceReader.Fields(o) }));
    }
    internal int Count { get { lock (_gate) return _pending.Count; } }

    internal void Review(GenerationSnapshot scope, long tick, O.NeedsForecastRead? needs, ForecastCoupledContext? context, FoodSupply? food = null)
    {
        var emit = new List<ForecastObservation>();
        lock (_gate)
        {
            var changed = _load != scope.Load.Value || _map != scope.Map.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (changed || tick < _reviewTick)
            {
                foreach (var row in _pending.Values) emit.Add(ForecastLiveEvidence.Gap(row.Prediction, row.Prediction.OriginTick,
                    changed ? "world_changed" : "tick_rewind", row.Execution));
                _pending.Clear(); _capturedReview = null; _epoch++;
            }
            _load = scope.Load.Value; _map = scope.Map.Value.ToString(System.Globalization.CultureInfo.InvariantCulture); _reviewTick = tick;
            foreach (var (key, row) in _pending.ToArray())
            {
                if (tick > row.Prediction.TargetTick + ExpiryTicks)
                    emit.Add(ForecastLiveEvidence.Gap(row.Prediction, tick, "pending_expired", row.Execution));
                else if (key.Metric != ForecastMetric.TaskCompletion && tick >= row.Prediction.TargetTick)
                    emit.Add(ForecastLiveEvidence.Observe(row.Prediction, tick, needs, context, row.Execution, food));
                else continue;
                _pending.Remove(key);
            }
        }
        foreach (var row in emit) _observation(row);
    }

    internal void Capture(GenerationSnapshot scope, long tick, ulong reviewRevision, ForecastCoupledSelection selection,
        ForecastCoupledContext context, ForecastAdmission admission, IReadOnlyList<PawnActivities> batches, string proposalMethod)
    {
        var predictions = new List<ForecastPrediction>();
        var gaps = new List<ForecastObservation>();
        lock (_gate)
        {
            if (_load != scope.Load.Value || _map != scope.Map.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) || tick != _reviewTick) return;
            if (_capturedReview is { } captured && reviewRevision <= captured) return;
            _capturedReview = reviewRevision;
            var review = _epoch.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + reviewRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var p in ForecastLiveEvidence.Predictions(_run, _load, _map, review, tick, selection, context, admission, batches, proposalMethod))
            {
                predictions.Add(p);
                if (!p.Value.IsKnown || p.Key.Metric == ForecastMetric.TaskCompletion && p.Key.Method.Length == 0)
                {
                    gaps.Add(ForecastLiveEvidence.Gap(p, tick, !p.Value.IsKnown ? p.UnknownReason : "task_execution_identity_unavailable",
                        admission == ForecastAdmission.Refused ? ForecastExecution.NotExecuted : ForecastExecution.Unknown));
                    continue;
                }
                if (_pending.Count == Capacity)
                {
                    var oldest = _pending.OrderBy(p => p.Value.Prediction.OriginTick).ThenBy(p => p.Key.Review, StringComparer.Ordinal).ThenBy(p => p.Key.Sample, StringComparer.Ordinal).First();
                    gaps.Add(ForecastLiveEvidence.Gap(oldest.Value.Prediction, tick, "pending_capacity", oldest.Value.Execution));
                    _pending.Remove(oldest.Key);
                }
                _pending.Add(p.Key, new(p));
            }
        }
        foreach (var row in predictions) _prediction(row);
        foreach (var row in gaps) _observation(row);
    }

    internal void Tasks(GenerationSnapshot scope, PawnActivities batch, O.ActivityTaskRead read)
    {
        var emit = new List<ForecastObservation>();
        lock (_gate)
        {
            if (_load != scope.Load.Value || _map != scope.Map.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) || read.Context?.Tick is not { } tick || tick < _reviewTick) return;
            foreach (var (key, row) in _pending.ToArray())
            {
                if (key.Metric != ForecastMetric.TaskCompletion || tick <= row.LastTaskTick || row.Prediction.Admission == ForecastAdmission.Refused
                    || !read.Pawns.Any(p => p.Pawn == key.Subject)) continue;
                var execution = ForecastLiveEvidence.ExecutionOf(row.Prediction, read, row.Seen);
                if (execution is ForecastExecution.Interrupted or ForecastExecution.Replaced)
                {
                    foreach (var related in _pending.Values.Where(p => p.Prediction.Key.Review == key.Review && p.Prediction.Key.Subject == key.Subject)) related.Execution = execution;
                }
                if (read.Pawns.Any(p => p.Pawn == key.Subject && p.Execution?.Method == key.Method)) row.Seen = true;
                var observed = ForecastLiveEvidence.TaskObservation(row.Prediction, batch, read, row.Unfinished, row.ConsistentStart, row.Execution, out var unfinished);
                row.LastTaskTick = tick;
                if (unfinished)
                {
                    row.Unfinished = tick;
                    row.ConsistentStart = read.Pawns.Any(p => p.Pawn == key.Subject && p.Execution?.Revision == key.Revision);
                }
                if (observed is null && execution is ForecastExecution.Interrupted or ForecastExecution.Replaced)
                    observed = ForecastLiveEvidence.Gap(row.Prediction, tick, "execution_diverged", execution);
                if (observed is null) continue;
                row.Execution = observed.Execution;
                emit.Add(observed); _pending.Remove(key);
            }
        }
        foreach (var row in emit) _observation(row);
    }
}
