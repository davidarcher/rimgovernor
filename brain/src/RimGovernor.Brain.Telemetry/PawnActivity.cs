namespace RimGovernor.Brain.Telemetry;

/// <summary>Observed activity, separate from purpose. Unknown is not idle or waiting.</summary>
public sealed record PawnActivitySample(
    string World, string Pawn, long Tick, string Job, int JobId, string Phase,
    string Activity, string Purpose, string Targets, string Location,
    string? Method = null, string? Assignment = null);

public sealed record PawnActivityInterval(long Sequence, PawnActivitySample Start,
    long EndTick, string EndLocation, string EndReason);

/// <summary>Open intervals only; detailed history belongs to the telemetry ring. Single-thread owned.</summary>
public sealed class PawnActivityLedger(Action<PawnActivityInterval> emit, int maxPawns = 512)
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
            else if (!Same(current.Start, sample))
            {
                Close(current, sample.Tick, sample.Location, "phase_changed");
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

    public void Remove(string pawn, long tick, string location, string reason)
    {
        if (!_open.Remove(pawn, out var current)) return;
        Close(current, Math.Max(current.Start.Tick, tick), location, reason);
    }
    public void Reset()
    {
        foreach (var current in _open.Values) Close(current, current.Last.Tick, current.Last.Location, "timeline_changed");
        _open.Clear();
    }

    private void Close(Open current, long tick, string location, string reason)
    {
        if (tick > current.Start.Tick)
            emit(new PawnActivityInterval(++_sequence, current.Start, tick, location, reason));
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

    /// <summary>The pawn's load id, on an interval and an output row.</summary>
    public const string PawnKey = "pawn";

    /// <summary>An interval's activity class (travel, work, needs, ...).</summary>
    public const string ActivityKey = "activity";

    /// <summary>An interval's job targets A, B and C, joined by '|': a thing's load id or a cell's x,z.</summary>
    public const string TargetsKey = "targets";

    /// <summary>The game tick an interval started at.</summary>
    public const string StartTickKey = "start_tick";

    /// <summary>An output row's produced def and unit count.</summary>
    public const string ResourceKey = "resource";

    public const string UnitsKey = "units";

    public static void Write(TelemetryEmitter emitter, PawnActivityInterval interval)
    {
        var s = interval.Start;
        emitter.EmitRow(IntervalKind, new RowContext { Component = "pawn-activity", Tick = interval.EndTick },
            new Dictionary<string, object?>
            {
                ["sequence"] = interval.Sequence, ["world"] = s.World, [PawnKey] = s.Pawn,
                [StartTickKey] = s.Tick, ["end_tick"] = interval.EndTick,
                ["job"] = s.Job, ["job_id"] = s.JobId, ["phase"] = s.Phase,
                [ActivityKey] = s.Activity, ["purpose"] = s.Purpose, [TargetsKey] = s.Targets,
                ["start_location"] = s.Location, ["end_location"] = interval.EndLocation,
                ["end_reason"] = interval.EndReason,
                ["method"] = s.Method, ["assignment"] = s.Assignment,
            });
    }
}
