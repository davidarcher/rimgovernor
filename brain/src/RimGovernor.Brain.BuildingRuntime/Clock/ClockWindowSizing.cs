namespace RimGovernor.Brain.BuildingRuntime;

// Port of internal/buildingruntime/clock_window_sizing.go.

// ClockWindowSize (the colony window one step admitted, published as the clock_step row's window_ticks) lives with
// ClockSchedulerResult; a routine window runs the whole budget unless a native work allowance or the combat bound
// narrows it: the planners review and the Worker dispatches under the running window, so nothing routine waits for
// a stop, and the stop tier (danger, coupled orders, player input) ends a window early.
internal static class ClockWindowSizing
{
    /// <summary>
    /// Discards stop spans that cannot be one review's wait: a clock the service left stopped for a long time, or
    /// skew between the native wall clock and the scheduler's (Go <c>clockStopSpanLimit</c>).
    /// </summary>
    public static readonly TimeSpan StopSpanLimit = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The wall time since the status's native stop, zero when it carries no usable stop; the admitting step
    /// publishes it as the stop-to-readmit pause (<c>stop_pause_s</c>) (Go <c>clockStopSpan</c>).
    /// </summary>
    public static TimeSpan StopSpan(PbClock.Status status, DateTimeOffset now)
    {
        if (status.StateCase != PbClock.Status.StateOneofCase.Stopped || !status.Stopped.HasStoppedAtUnixMs)
        {
            return TimeSpan.Zero;
        }
        var span = now - DateTimeOffset.FromUnixTimeMilliseconds(status.Stopped.StoppedAtUnixMs);
        return span <= TimeSpan.Zero || span > StopSpanLimit ? TimeSpan.Zero : span;
    }
}
