using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>One observed tend demand nobody served (task 5.5 of <c>native-led-labor-execution</c>): no colonist's current job served
/// it (<see cref="SupportDemand.Served"/>) at any reading over <see cref="CareStalls.WindowTicks"/>. It is the review's observation of
/// two or more native readings of the patient's demand, never inferred from elapsed ticks alone or from a forecast.</summary>
public sealed record CareStall(string Demand, string Patient, long ClosedTick);

/// <summary>One tend demand's open unserved window: the tick it was first read with no colonist's current job serving it.</summary>
public sealed record UnservedCareWindow(string Demand, long Opened);

/// <summary>The review's observed unserved care and the open windows behind it, or why none was observed.</summary>
public abstract record CareCalibration
{
    public const string UnobservedReason = "care_service_unobserved";

    private CareCalibration()
    {
    }

    /// <summary>This review's tend demands read (<see cref="Demands"/>), the unserved windows among them and the stalls among those.</summary>
    public sealed record Observed(EquatableArray<string> Demands, EquatableArray<UnservedCareWindow> Windows, EquatableArray<CareStall> Stalls) : CareCalibration;

    /// <summary>No support read was taken this review, so no window is known.</summary>
    public sealed record Unobserved(string Reason) : CareCalibration;
}

/// <summary>
/// The one pure place unserved care is observed (task 5.5 of <c>native-led-labor-execution</c>). Each tend demand of the support
/// read (a patient native wants tended now) is compared with the window the previous review kept: a reading in which a colonist's
/// current job serves it closes its window, and a first unserved reading or an earlier tick (a rewind or reload) opens a new one.
/// A demand unserved at every reading across <see cref="WindowTicks"/> is a stall. Surgery and bench demands are native bills and
/// are never tracked here.
/// </summary>
public static class CareStalls
{
    /// <summary>The unserved span that makes a stall: one game hour, four or more 600-tick reviews, so a stall is never read from a
    /// single reading. It is the observation window of the outcome (nobody tended the patient), not a materiality threshold: the
    /// premise is that outcome, and the comparator still ranks the order.</summary>
    public const long WindowTicks = (long)GameTime.TicksPerHour;

    /// <summary>The support demand kind a tend serves.</summary>
    public const string TendKind = "care";

    /// <summary>This review's calibration from the windows the previous review kept and the current coupled read; a missing read is
    /// <see cref="CareCalibration.Unobserved"/> and leaves the kept windows to the caller.</summary>
    public static CareCalibration Observe(EquatableArray<UnservedCareWindow> previous, CoupledForecastRead? read)
    {
        if (read is null) return new CareCalibration.Unobserved(CareCalibration.UnobservedReason);
        var tick = read.Tick;
        var kept = previous.ToDictionary(w => w.Demand, StringComparer.Ordinal);
        var demands = new List<string>();
        var windows = new List<UnservedCareWindow>();
        var stalls = new List<CareStall>();
        foreach (var demand in read.Support.Demands.Where(d => d.Kind == TendKind).OrderBy(d => d.Id, StringComparer.Ordinal))
        {
            demands.Add(demand.Id);
            if (demand.Served) continue;
            var window = kept.GetValueOrDefault(demand.Id) is { } open && tick >= open.Opened ? open : new UnservedCareWindow(demand.Id, tick);
            windows.Add(window);
            if (tick - window.Opened >= WindowTicks) stalls.Add(new(demand.Id, demand.Target, tick));
        }
        return new CareCalibration.Observed(EquatableArray.From(demands), EquatableArray.From(windows), EquatableArray.From(stalls));
    }
}
