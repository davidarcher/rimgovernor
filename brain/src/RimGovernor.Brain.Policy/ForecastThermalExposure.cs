namespace RimGovernor.Brain.Policy;

/// <summary>Degree-ticks at modeled stationary work/need locations; idle and unobserved routes remain gaps.
/// This is exposure, not injury probability or a physiological capacity estimate.</summary>
public sealed record ForecastThermalExposure(string Pawn, double AssessedTicks, double UnknownTicks,
    double AssessedColdDegreeTicks, double AssessedHotDegreeTicks)
{
    public Fact<double> ColdDegreeTicks => UnknownTicks == 0 ? Known(AssessedColdDegreeTicks) : default;
    public Fact<double> HotDegreeTicks => UnknownTicks == 0 ? Known(AssessedHotDegreeTicks) : default;
}

public static partial class PolicyFunctions
{
    public static EquatableArray<ForecastThermalExposure> ForecastThermalExposureOf(ForecastCoupledContext context,
        ForecastActivityProblem problem, ForecastActivityPlan schedule, EquatableArray<ForecastShelterOutcome> shelters, double horizon, ForecastActivityPlan? physicalSchedule = null)
    {
        var tasks = problem.Activities.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var physical = context.Thermal?.Cells.ToDictionary(c => Methods.Generation.ForecastLocations.Of(c.Cell)) ?? [];
        var results = new List<ForecastThermalExposure>();
        foreach (var pawn in context.Needs.Pawns)
        {
            double assessed = 0, cold = 0, hot = 0;
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
            results.Add(new(pawn.Pawn, assessed, Math.Max(0, horizon - assessed), cold, hot));
            void Integrate(double from, double to, double initial, double final)
            {
                assessed += to - from;
                cold += Area(pawn.ComfortableMin.Value - initial, pawn.ComfortableMin.Value - final, to - from);
                hot += Area(initial - pawn.ComfortableMax.Value, final - pawn.ComfortableMax.Value, to - from);
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
