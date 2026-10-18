namespace RimGovernor.Brain.BuildingRuntime;

// The scheduler state the run driver shares with the planning step: the pace measurement. The step only plans, so the driver reaches them through these internal members.

/// <summary>The pace the running clock is measured at; touched only under the player gate.</summary>
internal sealed class ClockPaceMeasure
{
    public long Tick { get; set; }

    public DateTimeOffset At { get; set; }

    public bool Known { get; set; }

    /// <summary>The last pace measured under a running clock, kept across holds so the next run starts with a pace.</summary>
    public double PerSecond { get; set; }

    /// <summary>The pace a step projects the tick by: <see cref="PerSecond"/> while the clock runs, zero while it is held.</summary>
    public double LiveTicks { get; set; }
}

public sealed partial class ClockScheduler
{
    internal ClockFacts Facts => _facts;

    internal ClockPaceMeasure Pace { get; } = new();

    internal long LastTick => _lastTick;

    internal bool LastTickKnown => _lastTickKnown;

    /// <summary>
    /// Measures the running clock's pace from the previous step's status tick: the pace the step's read validity
    /// widens its bounds by. A held or never-run clock, or a pace not yet measured, leaves the pace at zero so a
    /// paused step keeps the tick-exact bound.
    /// </summary>
    internal void LivePace(PbClock.Status status, DateTimeOffset readAt)
    {
        var tick = (long)(status.Context?.Tick).GetValueOrDefault();
        var pace = 0.0;
        if (status.State == PbClock.ClockState.Running && Pace.Known && tick > Pace.Tick && readAt > Pace.At)
        {
            Pace.PerSecond = (tick - Pace.Tick) / (readAt - Pace.At).TotalSeconds;
            pace = Pace.PerSecond;
        }
        Pace.LiveTicks = pace;
        (Pace.Tick, Pace.At, Pace.Known) = (tick, readAt, true);
    }

    internal void SetRunning(bool running) => _running = running;

    /// <summary>Stamps the full-step safety net as just run.</summary>
    internal void MarkFull() => _lastFull = _clock.Now();

    /// <summary>
    /// The clock_step row of one step: target the cause the step acted on, dur the step's wall, verdict what the
    /// step did (failed, deferred, idle) and reason why. The run driver files the clock_run verdicts on its own rows.
    /// </summary>
    internal static Decision StepDecision(ClockSchedulerResult result, Exception? error, StepCause cause, TimeSpan elapsed, Dictionary<string, object?> attrs)
    {
        var d = new Decision { Kind = TelemetryKinds.ClockStepKind, Component = "clock-scheduler", Target = Wire.Name(cause), Dur = elapsed, Attrs = attrs };
        if (error is not null)
        {
            attrs["error"] = error.Message;
            return d with { Verdict = "failed", Reason = "step_error" };
        }
        return result.Deferred ? d with { Verdict = "deferred", Reason = "deferred" } : d with { Verdict = "planned", Reason = "step" };
    }
}
