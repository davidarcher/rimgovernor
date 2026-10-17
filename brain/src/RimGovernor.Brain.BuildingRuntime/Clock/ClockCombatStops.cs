using System.Text;

namespace RimGovernor.Brain.BuildingRuntime;

/// <summary>
/// The combat hold list, the resume-latency gate and the per-combat stop metrics. The decision events a combat
/// watch arms: native holds the clock on the tick one of these happens. Only decision-class events are armed (a
/// hostile appears, a colonist is downed, an injury crosses the severity threshold; #3180): the rest of the combat
/// is the fight plan's to carry while the clock runs, and holding on it would keep the clock from running. A watch
/// arms them only while an admitted fight plan owns the combat (#852, armedCombatStops); the hold then re-invokes
/// the defense planner, whose DecideCombat answers it, and Run resumes once the immediate review has dispatched.
/// </summary>
internal static class ClockCombatStops
{
    public static readonly IReadOnlyList<PbClock.CombatEvent> DecisionEvents =
    [
        PbClock.CombatEvent.HostileArrived,
        PbClock.CombatEvent.Downed,
        PbClock.CombatEvent.SeriousInjury,
    ];

    /// <summary>
    /// The longest wall time from a combat hold to the Run that resumes it before the gate trips. UNMEASURED: the
    /// live baseline (#3199) is deferred, so this is a conservative placeholder to be replaced by the measured
    /// budget. A hold is only worth taking while it resumes quickly.
    /// </summary>
    public static readonly TimeSpan ResumeBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The speed ceiling while a fight is open once the gate has tripped: the clock keeps holding on the decision
    /// events but runs the fight no faster than this, so a slow resume costs fewer ticks. UNMEASURED, like
    /// <see cref="ResumeBudget"/>; Normal is the safest choice.
    /// </summary>
    public const PbClock.Speed FallbackSpeed = PbClock.Speed.Normal;

    /// <summary>The speed to ask for: <paramref name="target"/>, lowered to <paramref name="fallback"/> while the gate is <paramref name="tripped"/> in an open fight; never raised above the target.</summary>
    public static PbClock.Speed SpeedFor(PbClock.Speed target, PbClock.Speed fallback, bool tripped) =>
        tripped && fallback < target ? fallback : target;

    /// <summary>
    /// Names a stop for the metrics: the armed combat event, or the stop reason (<c>tick_budget</c> is the backstop).
    /// The name is the wire enum's C# name in lower snake case.
    /// </summary>
    public static string Kind(PbClock.Status held) =>
        held.CombatEvent is not null && held.CombatEvent.GetValueOrDefault() != PbClock.CombatEvent.Unspecified
            ? Snake(held.CombatEvent.GetValueOrDefault().ToString())
            : Snake(held.Reason.GetValueOrDefault().ToString());

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

    /// <summary>The nearest-rank percentile of <paramref name="values"/>, default when empty.</summary>
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
/// wall latency from a stop to the next Run, and the ticks between stops. A combat is the run of
/// consecutive combat runs; its summary is logged when a colony run follows. Touched only under the player
/// gate.
/// </summary>
internal sealed class CombatStopMetrics(TimeSpan resumeBudget)
{
    private bool _active;
    private bool _tripped;
    private Dictionary<string, int>? _byKind;
    private List<TimeSpan> _latencies = [];
    private List<long> _tickGaps = [];
    private long _lastStopTick;
    private bool _lastKnown;

    /// <summary>Whether a combat is in progress: the run of consecutive combat runs has not yet been closed by a colony run.</summary>
    public bool Active => _active;

    /// <summary>
    /// Whether the resume-latency gate has tripped in this combat: a combat resume took longer than the budget, so the
    /// fight runs at the fallback speed until the combat closes.
    /// </summary>
    public bool Tripped => _active && _tripped;

    /// <summary>
    /// Records a run: <paramref name="held"/> is the held status the run resumes from (null when the clock was not
    /// held), <paramref name="tick"/> its tick, <paramref name="now"/> the run's wall time and
    /// <paramref name="combat"/> the run's watch mode.
    /// </summary>
    public void Admitted(PbClock.Status? held, long tick, DateTimeOffset now, bool combat)
    {
        if (_active && held is not null)
        {
            var kind = ClockCombatStops.Kind(held);
            _byKind ??= new Dictionary<string, int>(StringComparer.Ordinal);
            _byKind[kind] = _byKind.GetValueOrDefault(kind) + 1;
            var attrs = new List<(string Key, object? Value)> { ("event", kind), ("tick", tick) };

            // The stop that ends the combat resumes into a colony run after the full review; its latency is
            // not a combat reaction.
            if (held.HeldAtUnixMs is not null && held.HeldAtUnixMs.GetValueOrDefault() > 0 && combat)
            {
                var latency = now - DateTimeOffset.FromUnixTimeMilliseconds(held.HeldAtUnixMs.GetValueOrDefault());
                _latencies.Add(latency);
                attrs.Add(("resume_latency_ms", (long)latency.TotalMilliseconds));
                if (latency > resumeBudget)
                {
                    _tripped = true;
                    attrs.Add(("over_budget", true));
                    attrs.Add(("resume_budget_ms", (long)resumeBudget.TotalMilliseconds));
                }
            }
            if (_lastKnown)
            {
                _tickGaps.Add(tick - _lastStopTick);
                attrs.Add(("ticks_since_stop", tick - _lastStopTick));
            }
            _lastStopTick = tick;
            _lastKnown = true;
            Tel.Event(TelemetryLevel.Info, "clock-scheduler", TelemetryKinds.ClockStopKind, "combat window stopped", attrs);
        }
        if (_active && !combat)
        {
            Log();
            _active = false;
            _tripped = false;
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
            TelemetryKinds.CombatSummaryKind,
            "combat ended",
            [
                ("stops", stops),
                ("by_event", byEvent),
                ("resume_latency_p50_ms", (long)ClockCombatStops.Percentile(_latencies, 50).TotalMilliseconds),
                ("resume_latency_p95_ms", (long)ClockCombatStops.Percentile(_latencies, 95).TotalMilliseconds),
                ("resume_budget_ms", (long)resumeBudget.TotalMilliseconds),
                ("gate_tripped", _tripped),
                ("ticks_between_stops_p50", ClockCombatStops.Percentile(_tickGaps, 50)),
                ("ticks_between_stops_p95", ClockCombatStops.Percentile(_tickGaps, 95)),
            ]);
    }
}
