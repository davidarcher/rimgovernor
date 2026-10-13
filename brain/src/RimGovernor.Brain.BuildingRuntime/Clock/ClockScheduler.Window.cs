namespace RimGovernor.Brain.BuildingRuntime;

// The scheduler state the window driver shares with the planning step: the pace measurement, the latched outcomes,
// the pace backoff and the latest step's clock status. In Go these were fields of the one ClockScheduler that also
// admitted windows; the step here only plans, so the driver reaches them through these internal members.

/// <summary>The pace the running window is measured at (Go <c>paceTick</c>, <c>paceAt</c>, <c>pacePerSecond</c>, <c>livePaceTicks</c>); touched only under the player gate.</summary>
internal sealed class ClockPaceMeasure
{
    public long Tick { get; set; }

    public DateTimeOffset At { get; set; }

    public bool Known { get; set; }

    /// <summary>The last pace measured under a running window, kept across stops so the next window starts with a pace.</summary>
    public double PerSecond { get; set; }

    /// <summary>The pace a step projects the tick by: <see cref="PerSecond"/> while a window runs, zero under a stopped clock.</summary>
    public double LiveTicks { get; set; }
}

public sealed partial class ClockScheduler
{
    /// <summary>The outcomes the native clock latched that the Worker has not reconciled; the poll writes them and the driver consults them.</summary>
    internal ClockLatched Latched { get; } = new();

    internal ClockFacts Facts => _facts;

    internal ClockPaceMeasure Pace { get; } = new();

    /// <summary>Player acceleration's backoff; null unless the window starts player-accelerated or follows the player's speed.</summary>
    internal PaceBackoff? Backoff { get; set; }

    /// <summary>The clock status the latest step read; the planner wave watches its running epoch.</summary>
    internal PbClock.Status? StepStatus { get; private set; }

    internal long LastTick => _lastTick;

    internal bool LastTickKnown => _lastTickKnown;

    internal void SetLastTick(long tick) => (_lastTick, _lastTickKnown) = (tick, true);

    internal void SetRunning(bool running) => _running = running;

    /// <summary>Stamps the full-step safety net as just run (Go <c>s.lastFull = s.clock.Now()</c>).</summary>
    internal void MarkFull() => _lastFull = _clock.Now();

    /// <summary>The ticks the running window's pace covers in the step's wall: what a step expects the tick to have moved by since its predecessor (Go <c>drift</c>).</summary>
    private long Drift() => (long)(Pace.LiveTicks * Config.MaxAge.TotalSeconds);

    /// <summary>Whether a stopped clock's game is running: the player un-paused after an external pause and drives the speed by hand (Go <c>clockPlayerRunning</c>).</summary>
    internal static bool PlayerRunning(PbClock.Status status) =>
        status.State is PbClock.StatusState.StoppedCase && status.ActualPaused is not null && !status.ActualPaused.GetValueOrDefault();

    /// <summary>
    /// Measures the running window's pace from the previous step's status tick: the pace the step's read validity
    /// widens its bounds by. A stopped, stopping or never-started clock, or a pace not yet measured, leaves the pace
    /// at zero so a paused step keeps the tick-exact bound (Go <c>livePace</c>).
    /// </summary>
    internal void LivePace(PbClock.Status status, DateTimeOffset readAt)
    {
        var tick = (long)(status.Context?.Tick).GetValueOrDefault();
        var pace = 0.0;
        // The pace is measured under a running window and, the same way, under a stopped clock the player runs by
        // hand; only the window widens the bounds.
        var running = status.State is PbClock.StatusState.RunningCase;
        if ((running || PlayerRunning(status)) && Pace.Known && tick > Pace.Tick && readAt > Pace.At)
        {
            Pace.PerSecond = (tick - Pace.Tick) / (readAt - Pace.At).TotalSeconds;
            if (running)
            {
                pace = Pace.PerSecond;
            }
        }
        Pace.LiveTicks = pace;
        (Pace.Tick, Pace.At, Pace.Known) = (tick, readAt, true);
    }

    /// <summary>
    /// Sets the pace for the window this step just started from the last pace <see cref="LivePace"/> measured, and
    /// restarts the pace measurement at the start tick: the step's own status read predates the start, and a pace
    /// measured from it would count the stop as running time (Go <c>seedLiveDrift</c>).
    /// </summary>
    internal void SeedLiveDrift(long startTick)
    {
        (Pace.Tick, Pace.At, Pace.Known) = (startTick, _clock.Now(), true);
        if (Pace.PerSecond <= 0)
        {
            return;
        }
        Pace.LiveTicks = Pace.PerSecond;
    }

    /// <summary>
    /// The loud half of the idle lend: reaching it is a defect (a planner waits on game time without saying how many
    /// ticks, so the stopped clock deadlocks), not routine. It logs at Warn an idle_stall row naming every goal's
    /// standing planner refusal or wait: those are the planners to teach a NativeWorkTicks hint (Go <c>warnIdleStall</c>).
    /// </summary>
    internal void WarnIdleStall(long tick)
    {
        var standing = new List<string>();
        foreach (var (goal, filed) in _plannerReasons.Standing)
        {
            var note = filed.Note;
            if (note.Cause == Cause.None || note.Cause == Cause.HeldOptIn)
            {
                continue;
            }
            var kind = note.Cause.Waiting() ? "waiting" : "refused";
            standing.Add($"{goal.Value} {kind}: {Wording(note.Cause, note.Subject)}");
        }
        standing.Sort(StringComparer.Ordinal);
        Tel.Decide(new Decision
        {
            Kind = "idle_stall",
            Component = "clock-scheduler",
            Level = TelemetryLevel.Warn,
            Verdict = "waiting",
            Reason = "no_work",
            Target = "colony",
            Attrs = new Dictionary<string, object?> { ["tick"] = tick, ["refusals"] = IdleRefusals, ["lend_ticks"] = IdleLendTicks, ["standing"] = standing },
        });
    }

    /// <summary>
    /// The clock_step row of one step: target the cause the step acted on, dur the step's wall, verdict what the
    /// step did (failed, deferred, idle) and reason why. The window driver files the admission verdicts (admitted,
    /// refused) on its own rows (Go <c>stepDecision</c>).
    /// </summary>
    internal static Decision StepDecision(ClockSchedulerResult result, Exception? error, StepCause cause, TimeSpan elapsed, Dictionary<string, object?> attrs)
    {
        var d = new Decision { Kind = "clock_step", Component = "clock-scheduler", Target = Wire.Name(cause), Dur = elapsed, Attrs = attrs };
        if (error is not null)
        {
            attrs["error"] = error.Message;
            return d with { Verdict = "failed", Reason = "step_error" };
        }
        return result.Deferred ? d with { Verdict = "deferred", Reason = "deferred" } : d with { Verdict = "idle", Reason = "no_window" };
    }
}
