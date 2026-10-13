using System.Text;

namespace RimGovernor.Brain.BuildingRuntime;

// Port of internal/buildingruntime/clock_combat_stops.go.

/// <summary>
/// The combat stop list and the per-combat stop metrics. The stop events a combat window arms: native stops the
/// epoch on the tick one of these happens. Aim warmup, shots and ordinary damage are never armed; stopping on them
/// would keep the clock from running. A window arms them only while an admitted fight plan owns the combat
/// (#852, armedCombatStops); the stop then re-invokes the defense planner, whose DecideCombat answers it.
/// </summary>
internal static class ClockCombatStops
{
    public static readonly IReadOnlyList<PbClock.CombatEvent> Events =
    [
        PbClock.CombatEvent.Downed,
        PbClock.CombatEvent.SeriousInjury,
        PbClock.CombatEvent.ShieldBroken,
        PbClock.CombatEvent.EnteredRange,
        PbClock.CombatEvent.MeleeContact,
        PbClock.CombatEvent.ExplosiveLaunched,
        PbClock.CombatEvent.RaidPhase,
        PbClock.CombatEvent.HostileArrived,
        PbClock.CombatEvent.Breach,
        PbClock.CombatEvent.MentalBreak,
        PbClock.CombatEvent.PrisonBreak,
    ];

    /// <summary>
    /// Names a stop for the metrics: the armed combat event, or the stop reason (<c>tick_budget</c> is the backstop)
    /// (Go <c>combatStopKind</c>). The wire enum's C# name, lower snake case, is Go's lowercased name without its prefix.
    /// </summary>
    public static string Kind(PbClock.Stopped stopped) =>
        stopped.CombatEvent is not null && stopped.CombatEvent.GetValueOrDefault() != PbClock.CombatEvent.Unspecified
            ? Snake(stopped.CombatEvent.GetValueOrDefault().ToString())
            : Snake(stopped.Reason.GetValueOrDefault().ToString());

    private static string Snake(string pascal)
    {
        var builder = new StringBuilder(pascal.Length + 4);
        for (var i = 0; i < pascal.Length; i++)
        {
            var c = pascal[i];
            if (char.IsUpper(c) && i > 0)
            {
                builder.Append('_');
            }
            builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }

    /// <summary>The nearest-rank percentile of <paramref name="values"/>, default when empty (Go <c>percentile</c>).</summary>
    public static T Percentile<T>(IReadOnlyList<T> values, int p)
        where T : IComparable<T>
    {
        if (values.Count == 0)
        {
            return default!;
        }
        var sorted = values.ToList();
        sorted.Sort();
        var rank = (p * sorted.Count + 99) / 100;
        return sorted[Math.Max(rank, 1) - 1];
    }
}

/// <summary>
/// Measures one combat's stops for the OTLP log ring: stops by event kind (the tick budget is the backstop), the
/// wall latency from a stop to the next window's admission, and the ticks between stops. A combat is the run of
/// consecutive combat windows; its summary is logged when a colony window follows. Touched only under the player
/// gate (Go <c>combatStopMetrics</c>).
/// </summary>
internal sealed class CombatStopMetrics
{
    private bool _active;
    private Dictionary<string, int>? _byKind;
    private List<TimeSpan> _latencies = [];
    private List<long> _tickGaps = [];
    private long _lastStopTick;
    private bool _lastKnown;

    /// <summary>Whether a combat is in progress: the run of consecutive combat windows has not yet been closed by a colony window.</summary>
    public bool Active => _active;

    /// <summary>
    /// Records a window admission: <paramref name="stopped"/> is the status the window resumes from (null when the
    /// clock was not stopped), <paramref name="tick"/> its tick, <paramref name="now"/> the admission's wall time and
    /// <paramref name="combat"/> the admitted window's mode.
    /// </summary>
    public void Admitted(PbClock.Stopped? stopped, long tick, DateTimeOffset now, bool combat)
    {
        if (_active && stopped is not null)
        {
            var kind = ClockCombatStops.Kind(stopped);
            _byKind ??= new Dictionary<string, int>(StringComparer.Ordinal);
            _byKind[kind] = _byKind.GetValueOrDefault(kind) + 1;
            var attrs = new List<(string Key, object? Value)> { ("event", kind), ("tick", tick) };

            // The stop that ends the combat resumes into a colony window after the full review; its latency is
            // not a combat reaction.
            if (stopped.StoppedAtUnixMs is not null && stopped.StoppedAtUnixMs.GetValueOrDefault() > 0 && combat)
            {
                var latency = now - DateTimeOffset.FromUnixTimeMilliseconds(stopped.StoppedAtUnixMs.GetValueOrDefault());
                _latencies.Add(latency);
                attrs.Add(("resume_latency_ms", (long)latency.TotalMilliseconds));
            }
            if (_lastKnown)
            {
                _tickGaps.Add(tick - _lastStopTick);
                attrs.Add(("ticks_since_stop", tick - _lastStopTick));
            }
            _lastStopTick = tick;
            _lastKnown = true;
            Tel.Event(TelemetryLevel.Info, "clock-scheduler", "clock_stop", "combat window stopped", attrs);
        }
        if (_active && !combat)
        {
            Log();
            _active = false;
            _byKind = null;
            _latencies = [];
            _tickGaps = [];
            _lastStopTick = 0;
            _lastKnown = false;
        }
        if (combat && !_active)
        {
            _active = true;
            _lastStopTick = tick;
            _lastKnown = true;
        }
    }

    private void Log()
    {
        var stops = 0;
        var byEvent = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (_byKind is not null)
        {
            foreach (var (kind, n) in _byKind)
            {
                stops += n;
                byEvent[kind] = n;
            }
        }
        Tel.Event(
            TelemetryLevel.Info,
            "clock-scheduler",
            "combat_summary",
            "combat ended",
            [
                ("stops", stops),
                ("by_event", byEvent),
                ("resume_latency_p50_ms", (long)ClockCombatStops.Percentile(_latencies, 50).TotalMilliseconds),
                ("resume_latency_p95_ms", (long)ClockCombatStops.Percentile(_latencies, 95).TotalMilliseconds),
                ("ticks_between_stops_p50", ClockCombatStops.Percentile(_tickGaps, 50)),
                ("ticks_between_stops_p95", ClockCombatStops.Percentile(_tickGaps, 95)),
            ]);
    }
}
