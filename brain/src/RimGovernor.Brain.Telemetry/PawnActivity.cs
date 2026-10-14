namespace RimGovernor.Brain.Telemetry;

/// <summary>Observed activity, separate from purpose. Unknown is not idle or waiting.</summary>
public sealed record PawnActivitySample(
    string World, string Pawn, long Tick, string Job, int JobId, string Phase,
    string Activity, string Purpose, string Targets, string Location,
    string? Method = null, string? Assignment = null);

public sealed record PawnActivityInterval(long Sequence, PawnActivitySample Start,
    long EndTick, string EndLocation, string EndReason, int SamplingTicks);

/// <summary>Open intervals only; detailed history belongs to the telemetry ring. Single-thread owned.</summary>
public sealed class PawnActivityLedger(Action<PawnActivityInterval> emit, int samplingTicks = 15, int maxPawns = 512)
{
    private sealed record Open(PawnActivitySample Start, PawnActivitySample Last);
    private readonly Dictionary<string, Open> _open = new(StringComparer.Ordinal);
    private long _sequence;
    public int Count => _open.Count;
    public long RefusedPawns { get; private set; }

    public void Observe(PawnActivitySample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (_open.TryGetValue(sample.Pawn, out var current))
        {
            if (sample.World != current.Start.World || sample.Tick < current.Last.Tick)
            {
                Close(current, current.Last.Tick, current.Last.Location, "timeline_changed");
                _open.Remove(sample.Pawn);
            }
            else if (sample.Tick - current.Last.Tick > samplingTicks * 2L)
            {
                Close(current, current.Last.Tick, current.Last.Location, "observation_gap");
                var gap = current.Last with { Activity = "unknown", Phase = "gap", Purpose = "unknown", Job = "", JobId = -1, Targets = "", Method = null, Assignment = null };
                emit(new PawnActivityInterval(++_sequence, gap, sample.Tick, sample.Location, "observation_gap", samplingTicks));
                _open.Remove(sample.Pawn);
            }
            else if (!Same(current.Start, sample) || sample.Tick - current.Start.Tick >= 2048)
            {
                Close(current, sample.Tick, sample.Location, Same(current.Start, sample) ? "checkpoint" : "phase_changed");
                _open.Remove(sample.Pawn);
            }
            else
            {
                _open[sample.Pawn] = current with { Last = sample };
                return;
            }
        }
        if (_open.Count >= maxPawns)
        {
            RefusedPawns++;
            return;
        }
        _open.Add(sample.Pawn, new Open(sample, sample));
    }

    public void End(string pawn, int jobId, long tick, string location, string reason)
    {
        if (!_open.TryGetValue(pawn, out var current) || current.Start.JobId != jobId) return;
        // An end from a stale timeline cannot extend its old interval.
        Close(current, Math.Max(current.Start.Tick, tick), location, reason);
        _open.Remove(pawn);
    }

    public void RemoveExcept(IReadOnlySet<string> present, long tick)
    {
        foreach (var id in _open.Keys.Where(id => !present.Contains(id)).ToArray())
        {
            var current = _open[id];
            Close(current, Math.Max(current.Last.Tick, tick), current.Last.Location, "death_or_despawn");
            _open.Remove(id);
        }
    }

    public void Reset()
    {
        foreach (var current in _open.Values) Close(current, current.Last.Tick, current.Last.Location, "timeline_changed");
        _open.Clear();
    }

    private void Close(Open current, long tick, string location, string reason)
    {
        if (tick > current.Start.Tick)
            emit(new PawnActivityInterval(++_sequence, current.Start, tick, location, reason, samplingTicks));
    }

    private static bool Same(PawnActivitySample a, PawnActivitySample b) =>
        a.World == b.World && a.JobId == b.JobId && a.Job == b.Job && a.Phase == b.Phase
        && a.Activity == b.Activity && a.Purpose == b.Purpose && a.Targets == b.Targets
        && a.Method == b.Method && a.Assignment == b.Assignment;
}

public static class PawnActivityTelemetry
{
    public const string IntervalKind = "pawn_activity_interval";
    public const string OutputKind = "pawn_activity_output";

    public static void Write(TelemetryEmitter emitter, PawnActivityInterval interval)
    {
        var s = interval.Start;
        emitter.EmitRow(IntervalKind, new RowContext { Component = "pawn-activity", Tick = interval.EndTick },
            new Dictionary<string, object?>
            {
                ["sequence"] = interval.Sequence, ["world"] = s.World, ["pawn"] = s.Pawn,
                ["start_tick"] = s.Tick, ["end_tick"] = interval.EndTick,
                ["job"] = s.Job, ["job_id"] = s.JobId, ["phase"] = s.Phase,
                ["activity"] = s.Activity, ["purpose"] = s.Purpose, ["targets"] = s.Targets,
                ["start_location"] = s.Location, ["end_location"] = interval.EndLocation,
                ["end_reason"] = interval.EndReason, ["sampling_ticks"] = interval.SamplingTicks,
                ["method"] = s.Method, ["assignment"] = s.Assignment,
            });
    }
}
