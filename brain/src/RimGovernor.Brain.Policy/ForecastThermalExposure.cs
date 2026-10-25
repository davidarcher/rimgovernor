using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>Degree-ticks at modeled stationary work/need locations; a room still to be finished is as it stands (#3534); idle and unobserved ticks take the last located
/// temperature, else outdoors (#3673), and remain gaps only with neither known.
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
        var (min, max) = (pawn.ComfortableMin, pawn.ComfortableMax);
        {
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
                if (room is not null && projection is ForecastShelterOutcome.Incomplete)
                {
                    untouched = true;
                    foreach (var a in physical)
                    {
                        var touches = room.Prerequisites.Contains(a.Activity);
                        foreach (var group in room.AlternativePrerequisites) touches |= group.Contains(a.Activity);
                        if (touches) { untouched = false; break; }
                    }
                }
                // A room still to be finished is as it stands (#3534): it trends to outdoors at its open cells' rate.
                EquatableArray<ForecastTemperaturePoint> standing = room is null ? [] : ForecastShelterPolicy.Standing(room, horizon);
                if (untouched && standing.Count > 0) Trajectory(spans, ref assessed, standing, from, end, min, max, shifts);
                else if (room is null || untouched) Integrate(spans, ref assessed, from, end, cell.Temperature, cell.Temperature, min, max, shifts);
                else if (projection is ForecastShelterOutcome.Projected projected)
                {
                    // A projected room is estimated: before its shell completes it is as it stands, else open air at outdoors.
                    if (from < projected.CompleteAt && standing.Count > 0)
                        Trajectory(spans, ref assessed, standing, from, Math.Min(end, projected.CompleteAt), min, max, shifts);
                    else if (from < projected.CompleteAt && room!.Thermal is ForecastShelterThermal.Estimated { Outdoors: var before })
                        Integrate(spans, ref assessed, from, Math.Min(end, projected.CompleteAt), before, before, min, max, shifts);
                    Trajectory(spans, ref assessed, projected.Trajectory, from, end, min, max, shifts);
                }
            }
            // A tick at no located allocation (idle, transit, an unread cell) is at the last located temperature, else
            // outdoors (#3673); with neither known it stays unknown.
            if (spans.Count > 0 || context.Thermal is not null)
            {
                var located = spans.OrderBy(s => s.Start).ToArray();
                double cursor = 0;
                double? last = context.Thermal?.Outdoors;
                foreach (var s in located)
                {
                    if (s.Start > cursor && last is { } before) Integrate(spans, ref assessed, cursor, Math.Min(s.Start, horizon), before, before, min, max, shifts);
                    if (s.End > cursor) { cursor = s.End; last = s.Final; }
                }
                if (cursor < horizon && last is { } after) Integrate(spans, ref assessed, cursor, horizon, after, after, min, max, shifts);
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

    // The part of a piecewise-linear trajectory inside [from, end).
    private static void Trajectory(List<ForecastThermalSpan> spans, ref double assessed, EquatableArray<ForecastTemperaturePoint> points,
        double from, double end, double min, double max, (double MinDelta, double MaxDelta, double Start)[] shifts)
    {
        for (var index = 1; index < points.Count; index++)
        {
            var a = points[index - 1]; var b = points[index];
            var start = Math.Max(from, a.Tick); var stop = Math.Min(end, b.Tick);
            if (stop <= start) continue;
            var slope = (b.Temperature - a.Temperature) / (b.Tick - a.Tick);
            Integrate(spans, ref assessed, start, stop, a.Temperature + slope * (start - a.Tick), a.Temperature + slope * (stop - a.Tick), min, max, shifts);
        }
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


    private static double Area(double a, double b, double duration)
    {
        if (a >= 0 && b >= 0) return (a + b) * duration / 2;
        if (a <= 0 && b <= 0) return 0;
        var positive = Math.Max(a, b);
        return positive * positive / (Math.Abs(a - b)) * duration / 2;
    }
}
