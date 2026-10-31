using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>What an observed construction stall leaves undone: the cause names the one exact order that can answer it.</summary>
public enum ConstructionStallCause
{
    /// <summary>The site still owes materials and no colonist's job targeted it for a whole window without its delivered count changing.</summary>
    [WireName("supply_unstarted")]
    SupplyUnstarted,

    /// <summary>The site holds its whole cost and no colonist's job targeted it for a whole window without its work done changing.</summary>
    [WireName("finish_unstarted")]
    FinishUnstarted,
}

/// <summary>One placed site's observed stall (task 5.4 of <c>native-led-labor-execution</c>): native work never targeted it over
/// <see cref="ConstructionStalls.WindowTicks"/> while its observed progress stayed unchanged. It is the review's observation of two
/// or more native progress readings, never inferred from elapsed ticks alone or from a forecast.</summary>
public sealed record ConstructionStall(string Site, ConstructionStallCause Cause, long ClosedTick);

/// <summary>One placed site's open progress window: the progress last read (<see cref="Delivered"/> of <see cref="Required"/>,
/// <see cref="WorkDone"/>), the tick that progress was first read (<see cref="Opened"/>) and the tick native work last targeted
/// the site, or <see cref="Opened"/> when it never did (<see cref="Quiet"/>).</summary>
public sealed record ConstructionProgressWindow(string Site, int Delivered, int Required, double WorkDone, long Opened, long Quiet);

/// <summary>The review's observed construction stalls and the open progress windows behind them, or why none was observed.</summary>
public abstract record ConstructionCalibration
{
    public const string UnobservedReason = "construction_progress_unobserved";

    private ConstructionCalibration()
    {
    }

    /// <summary>This review's progress windows, by site, and the stalls among them.</summary>
    public sealed record Observed(EquatableArray<ConstructionProgressWindow> Windows, EquatableArray<ConstructionStall> Stalls) : ConstructionCalibration;

    /// <summary>No construction progress was read this world, so no window is known.</summary>
    public sealed record Unobserved(string Reason) : ConstructionCalibration;
}

/// <summary>
/// The one pure place a construction stall is observed (task 5.4 of <c>native-led-labor-execution</c>). Each placed site's native
/// progress reading (<see cref="ConstructionSiteProgress"/>) is compared with the window the previous review kept: a changed
/// delivered count or work done, a first reading, or an earlier tick (a rewind or reload) opens a new window; a colonist's job
/// targeting the site restarts its quiet span. A site whose quiet span lasts <see cref="WindowTicks"/> while its current reading
/// shows no native work is a stall, supply-unstarted while it owes materials, otherwise finish-unstarted.
/// </summary>
public static class ConstructionStalls
{
    /// <summary>The quiet span that makes a stall: one game day, the work dispatcher's throughput window
    /// (<see cref="WorkRules.ThroughputWindowTicks"/>), so a stall is never read from a single reading.</summary>
    public const long WindowTicks = WorkRules.ThroughputWindowTicks;

    /// <summary>This review's calibration from the windows the previous review kept and the current construction read; a missing
    /// read is <see cref="ConstructionCalibration.Unobserved"/> and leaves the kept windows to the caller.</summary>
    public static ConstructionCalibration Observe(EquatableArray<ConstructionProgressWindow> previous, ConstructionActivityRead? read)
    {
        if (read is null) return new ConstructionCalibration.Unobserved(ConstructionCalibration.UnobservedReason);
        var tick = read.Context.Tick;
        var kept = previous.ToDictionary(w => w.Site, StringComparer.Ordinal);
        var windows = new List<ConstructionProgressWindow>();
        var stalls = new List<ConstructionStall>();
        foreach (var progress in read.Progress.OrderBy(p => p.Site, StringComparer.Ordinal))
        {
            var window = kept.GetValueOrDefault(progress.Site) is { } open && tick >= open.Opened && tick >= open.Quiet
                && open.Delivered == progress.Delivered && open.Required == progress.Required && open.WorkDone.Equals(progress.WorkDone)
                    ? open
                    : new ConstructionProgressWindow(progress.Site, progress.Delivered, progress.Required, progress.WorkDone, tick, tick);
            if (progress.Worked) window = window with { Quiet = tick };
            windows.Add(window);
            if (!progress.Worked && tick - window.Quiet >= WindowTicks)
                stalls.Add(new(progress.Site,
                    progress.Delivered < progress.Required ? ConstructionStallCause.SupplyUnstarted : ConstructionStallCause.FinishUnstarted, tick));
        }
        return new ConstructionCalibration.Observed(EquatableArray.From(windows), EquatableArray.From(stalls));
    }
}
