using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>Degree-ticks at modeled stationary work/need locations; idle and unobserved routes remain gaps.
/// This is exposure, not injury probability or a physiological capacity estimate.</summary>
public sealed partial record ForecastThermalExposure(string Pawn, double AssessedTicks, double UnknownTicks,
    double AssessedColdDegreeTicks, double AssessedHotDegreeTicks)
{
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
    // Likewise each context's shelters by interior cell, in shelter order (#3468).
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ForecastCoupledContext, Dictionary<Cell, List<ForecastShelterPlan>>> RoomsByCell = new();
    private static Dictionary<Cell, List<ForecastShelterPlan>> RoomsOf(ForecastCoupledContext context)
    {
        var byCell = new Dictionary<Cell, List<ForecastShelterPlan>>();
        foreach (var room in context.Shelters)
            foreach (var cell in room.Interior.Distinct())
            {
                if (!byCell.TryGetValue(cell, out var rooms)) byCell.Add(cell, rooms = []);
                rooms.Add(room);
            }
        return byCell;
    }

    /// <summary>One pawn's exposure over <paramref name="allocations"/> (that pawn's, in schedule order): each stationary
    /// allocation at a read cell integrates the cell's temperature, or its room's projection, against the pawn's comfort
    /// range with <paramref name="shifts"/> applied. An untouched proposed room (a projection waiting on prerequisites none
    /// of <paramref name="physical"/> schedules) is still the observed physical location.</summary>
    internal static ForecastThermalExposure PawnExposureOf(ForecastCoupledContext context, Dictionary<string, ForecastActivity> tasks, NeedsForecastPawn pawn,
        ForecastActivityAllocation[] allocations, ForecastActivityAllocation[] physical, EquatableArray<ForecastShelterOutcome> shelters, double horizon,
        (double MinDelta, double MaxDelta, double Start)[] shifts, List<ForecastThermalSpan> spans)
    {
        var roomsByCell = RoomsByCell.GetValue(context, RoomsOf);
        var cells = context.Thermal is { } thermal ? CellsByLocation.GetValue(thermal, static t => t.Cells.ToDictionary(c => Methods.Generation.ForecastLocations.Of(c.Cell))) : NoCells;
        double assessed = 0;
        spans.Clear();
        if (pawn.ComfortableMin.TryGetValue(out var min) && pawn.ComfortableMax.TryGetValue(out var max))
            foreach (var allocation in allocations)
            {
                var task = tasks[allocation.Activity];
                // Actual transit is not at either endpoint. A moving service likewise needs a route temperature trace.
                if (task.Location != task.EndLocation || !cells.TryGetValue(task.Location, out var cell)) continue;
                var shared = roomsByCell.TryGetValue(cell.Cell, out var rooms);
                var room = shared && rooms!.Count == 1 ? rooms[0] : null;
                if (room is null && shared) continue;
                ForecastShelterOutcome? projection = null;
                if (room is not null)
                    foreach (var s in shelters)
                        if (s.Room == room.Room) { if (projection is not null) throw new InvalidOperationException("Sequence contains more than one matching element"); projection = s; }
                var from = allocation.Arrive; var end = Math.Min(horizon, allocation.End);
                if (from >= end) continue;
                // An untouched proposed room is still the observed physical location. Its absent
                // future prerequisites must not erase known exposure from the no-investment baseline.
                var untouched = false;
                if (room is not null && projection is not null && Incomplete(projection))
                {
                    untouched = true;
                    foreach (var a in physical)
                    {
                        var touches = room.Prerequisites.Contains(a.Activity);
                        foreach (var group in room.AlternativePrerequisites) touches |= group.Contains(a.Activity);
                        if (touches) { untouched = false; break; }
                    }
                }
                if ((room is null || untouched) && cell.Temperature.TryGetValue(out var ambient)) Integrate(spans, ref assessed, from, end, ambient, ambient, min, max, shifts);
                else if (projection is { TemperatureAtHorizon.IsKnown: true, CompleteAt.IsKnown: true })
                {
                    if (from < projection.CompleteAt.Value && room!.Outdoors.TryGetValue(out var before))
                        Integrate(spans, ref assessed, from, Math.Min(end, projection.CompleteAt.Value), before, before, min, max, shifts);
                    var points = projection.Trajectory;
                    for (var index = 1; index < points.Count; index++)
                    {
                        var a = points[index - 1]; var b = points[index];
                        var start = Math.Max(from, a.Tick); var stop = Math.Min(end, b.Tick);
                        if (stop <= start) continue;
                        var slope = (b.Temperature - a.Temperature) / (b.Tick - a.Tick);
                        Integrate(spans, ref assessed, start, stop, a.Temperature + slope * (start - a.Tick), a.Temperature + slope * (stop - a.Tick), min, max, shifts);
                    }
                }
            }
        double cold = 0, hot = 0;
        foreach (var s in spans)
        {
            cold += Area(s.ComfortableMin - s.Initial, s.ComfortableMin - s.Final, s.End - s.Start);
            hot += Area(s.Initial - s.ComfortableMax, s.Final - s.ComfortableMax, s.End - s.Start);
        }
        return new(pawn.Pawn, assessed, Math.Max(0, horizon - assessed), cold, hot)
        { Spans = EquatableArray.Create<ForecastThermalSpan>(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(spans)) };
    }

    private static void Integrate(List<ForecastThermalSpan> spans, ref double assessed, double from, double to, double initial, double final,
        double min, double max, (double MinDelta, double MaxDelta, double Start)[] shifts)
    {
        assessed += to - from;
        if (shifts.Length == 0) { spans.Add(new(from, to, initial, final, min, max)); return; }
        // The comfort range is piecewise constant between shift starts; temperature stays linear within the span.
        var cuts = shifts.Select(s => s.Start).Where(t => t > from && t < to).Append(from).Append(to).Distinct().Order().ToArray();
        for (var i = 0; i < cuts.Length - 1; i++)
        {
            var a = cuts[i]; var b = cuts[i + 1];
            double minDelta = 0, maxDelta = 0;
            foreach (var s in shifts) if (s.Start <= a) { minDelta += s.MinDelta; maxDelta += s.MaxDelta; }
            spans.Add(new(a, b, At(a), At(b), min + minDelta, max + maxDelta));
        }
        double At(double tick) => tick == from ? initial : tick == to ? final : initial + (final - initial) * (tick - from) / (to - from);
    }

    private static bool Incomplete(ForecastShelterOutcome projection)
    {
        foreach (var finding in projection.Findings) if (finding == "incomplete_shelter_prerequisites") return true;
        return false;
    }

    private static double Area(double a, double b, double duration)
    {
        if (a >= 0 && b >= 0) return (a + b) * duration / 2;
        if (a <= 0 && b <= 0) return 0;
        var positive = Math.Max(a, b);
        return positive * positive / (Math.Abs(a - b)) * duration / 2;
    }
}
