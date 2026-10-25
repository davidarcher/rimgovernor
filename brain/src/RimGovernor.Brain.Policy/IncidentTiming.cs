using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>A recurring MTB stream or the native cycle's observed occurrence times, with this incident's draw chance.</summary>
public abstract record IncidentTiming
{
    private IncidentTiming() { }
    public sealed record Mtb : IncidentTiming;
    public sealed record Scheduled(IncidentCycleTimes Cycle, double Chance) : IncidentTiming
    {
        public double From { get; init; }
        public double Until { get; init; } = double.PositiveInfinity;
        public double OriginDay { get; init; }
    }
    public sealed record Process(RandomMainProcess Program, int CompIndex, string Incident) : IncidentTiming
    {
        public double OriginDay { get; init; }
    }

    public int CompKey => this switch { Scheduled s => s.Cycle.CompIndex, Process p => p.CompIndex, Mtb => -1,
        var invalid => throw new ArgumentOutOfRangeException(nameof(CompKey), invalid, "Invalid incident timing.") };

    public IEnumerable<(double Day, double Chance)> Events(double days) => this switch
    {
        Scheduled s => Covered(s.Cycle, days + s.OriginDay).Where(d => d >= Math.Max(s.From, s.OriginDay) && d < Math.Min(days + s.OriginDay, s.Until))
            .Select(d => (d - s.OriginDay, s.Chance)),
        Process p => p.Program.Emissions(p.CompIndex, p.Incident, days + p.OriginDay).Where(e => e.Day >= p.OriginDay)
            .Select(e => (e.Day - p.OriginDay, e.Chance)),
        Mtb => [],
        var invalid => throw new ArgumentOutOfRangeException(nameof(days), invalid, "Invalid incident timing."),
    };

    public double PerDay(double recurring, double days) => this switch
    {
        Mtb => recurring,
        Scheduled scheduled => days > 0 ? Covered(scheduled.Cycle, days + scheduled.OriginDay).Count(d => d >= Math.Max(scheduled.From, scheduled.OriginDay) && d < Math.Min(days + scheduled.OriginDay, scheduled.Until)) * scheduled.Chance / days : 0,
        Process => days > 0 ? Events(days).Sum(e => e.Chance) / days : 0,
        var invalid => throw new ArgumentOutOfRangeException(nameof(days), invalid, "Invalid incident timing."),
    };

    public static IReadOnlyList<double> Covered(IncidentCycleTimes cycle, double days)
    {
        if (!double.IsFinite(days) || days < 0 || days > cycle.ThroughDays)
            throw new RequiredFactException("incident_cycle_window_unobserved");
        return cycle.Days;
    }

    public static IncidentCycleTimes RequireCycle(IReadOnlyList<IncidentCycleTimes> rows, int comp) =>
        rows.FirstOrDefault(r => r.CompIndex == comp) ?? throw new RequiredFactException("incident_cycle_unobserved:" + comp);
}
