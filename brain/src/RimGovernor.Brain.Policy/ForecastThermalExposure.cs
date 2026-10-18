using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>Degree-ticks at modeled stationary work/need locations; idle and unobserved routes remain gaps.
/// This is exposure, not injury probability or a physiological capacity estimate.</summary>
public sealed partial record ForecastThermalExposure(string Pawn, double AssessedTicks, double UnknownTicks,
    double AssessedColdDegreeTicks, double AssessedHotDegreeTicks)
{
    public Fact<double> ColdDegreeTicks => UnknownTicks == 0 ? Known(AssessedColdDegreeTicks) : default;
    public Fact<double> HotDegreeTicks => UnknownTicks == 0 ? Known(AssessedHotDegreeTicks) : default;

    /// <summary>The assessed spans the degree-ticks integrate (#3327): temperature linear inside each, comfort range constant.</summary>
    public EquatableArray<ForecastThermalSpan> Spans { get; init; }
}

/// <summary>One assessed stationary span: the projected temperature runs linearly from Initial to Final, under the pawn's comfort range with its shifts applied.</summary>
public sealed record ForecastThermalSpan(double Start, double End, double Initial, double Final, double ComfortableMin, double ComfortableMax);

public sealed partial record ForecastThermalExposure
{
    // A coupled search evaluates many schedules against one thermal read, so each read's cells are keyed by
    // location once, not on every evaluation.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CoupledThermalRead, Dictionary<string, ThermalCell>> CellsByLocation = new();
    private static readonly Dictionary<string, ThermalCell> NoCells = [];

    public static EquatableArray<ForecastThermalExposure> ForecastThermalExposureOf(ForecastCoupledContext context,
        ForecastActivityProblem problem, ForecastActivityPlan schedule, EquatableArray<ForecastShelterOutcome> shelters, double horizon, ForecastActivityPlan? physicalSchedule = null,
        IReadOnlyDictionary<string, List<(double MinDelta, double MaxDelta, double Start)>>? comfort = null)
    {
        var tasks = problem.Activities.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var physical = context.Thermal is { } thermal ? CellsByLocation.GetValue(thermal, static t => t.Cells.ToDictionary(c => Methods.Generation.ForecastLocations.Of(c.Cell))) : NoCells;
        var results = new List<ForecastThermalExposure>();
        foreach (var pawn in context.Needs.Pawns)
        {
            double assessed = 0;
            var spans = new List<ForecastThermalSpan>();
            if (pawn.ComfortableMin.IsKnown && pawn.ComfortableMax.IsKnown)
                foreach (var allocation in schedule.Allocations.Where(a => a.Pawn == pawn.Pawn))
                {
                    var task = tasks[allocation.Activity];
                    // Actual transit is not at either endpoint. A moving service likewise needs a route temperature trace.
                    if (task.Location != task.EndLocation || !physical.TryGetValue(task.Location, out var cell)) continue;
                    var rooms = context.Shelters.Where(r => r.Interior.Contains(cell.Cell)).ToArray();
                    if (rooms.Length > 1) continue;
                    var room = rooms.SingleOrDefault();
                    var projection = room is null ? null : shelters.SingleOrDefault(s => s.Room == room.Room);
                    var from = allocation.Arrive; var end = Math.Min(horizon, allocation.End);
                    if (from >= end) continue;
                    // An untouched proposed room is still the observed physical location. Its absent
                    // future prerequisites must not erase known exposure from the no-investment baseline.
                    var untouched = room is not null && projection?.Findings.Contains("incomplete_shelter_prerequisites") == true
                        && !(physicalSchedule ?? schedule).Allocations.Any(a => room.Prerequisites.Contains(a.Activity)
                            || room.AlternativePrerequisites.Any(g => g.Contains(a.Activity)));
                    if ((room is null || untouched) && cell.Temperature.TryGetValue(out var ambient)) Integrate(from, end, ambient, ambient);
                    else if (projection is { TemperatureAtHorizon.IsKnown: true, CompleteAt.IsKnown: true })
                    {
                        if (from < projection.CompleteAt.Value && room!.Outdoors.TryGetValue(out var before))
                            Integrate(from, Math.Min(end, projection.CompleteAt.Value), before, before);
                        var points = projection.Trajectory;
                        for (var index = 1; index < points.Count; index++)
                        {
                            var a = points[index - 1]; var b = points[index];
                            var start = Math.Max(from, a.Tick); var stop = Math.Min(end, b.Tick);
                            if (stop <= start) continue;
                            var slope = (b.Temperature - a.Temperature) / (b.Tick - a.Tick);
                            Integrate(start, stop, a.Temperature + slope * (start - a.Tick), a.Temperature + slope * (stop - a.Tick));
                        }
                    }
                }
            results.Add(new(pawn.Pawn, assessed, Math.Max(0, horizon - assessed),
                spans.Sum(s => Area(s.ComfortableMin - s.Initial, s.ComfortableMin - s.Final, s.End - s.Start)),
                spans.Sum(s => Area(s.Initial - s.ComfortableMax, s.Final - s.ComfortableMax, s.End - s.Start)))
            { Spans = EquatableArray.From(spans) });
            void Integrate(double from, double to, double initial, double final)
            {
                assessed += to - from;
                var shifts = comfort?.GetValueOrDefault(pawn.Pawn) ?? [];
                // The comfort range is piecewise constant between shift starts; temperature stays linear within the span.
                var cuts = shifts.Select(s => s.Start).Where(t => t > from && t < to).Append(from).Append(to).Distinct().Order().ToArray();
                for (var i = 0; i < cuts.Length - 1; i++)
                {
                    var a = cuts[i]; var b = cuts[i + 1];
                    spans.Add(new(a, b, At(a), At(b), pawn.ComfortableMin.Value + shifts.Where(s => s.Start <= a).Sum(s => s.MinDelta),
                        pawn.ComfortableMax.Value + shifts.Where(s => s.Start <= a).Sum(s => s.MaxDelta)));
                }
                double At(double tick) => tick == from ? initial : tick == to ? final : initial + (final - initial) * (tick - from) / (to - from);
            }
        }
        return EquatableArray.From(results);
        static double Area(double a, double b, double duration)
        {
            if (a >= 0 && b >= 0) return (a + b) * duration / 2;
            if (a <= 0 && b <= 0) return 0;
            var positive = Math.Max(a, b);
            return positive * positive / (Math.Abs(a - b)) * duration / 2;
        }
    }
}
