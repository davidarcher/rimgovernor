namespace RimGovernor.Brain.Policy;

public enum ForecastOccupancyState { Active, Completed, Cancelled }

/// <summary>Relative ticks, including travel. Active intervals protect useful work or needs; no sunk work is charged.</summary>
public sealed record ForecastOccupancy(string Id, string Pawn, Fact<double> Start, Fact<double> End, ForecastOccupancyState State);

public sealed record ForecastCapacityInput
{
    public bool ProtectTimetable { get; init; } = true;
    public required WorkPawn Pawn { get; init; }
    public required string Location { get; init; }
    public double HorizonTicks { get; init; }
    public Fact<double> LocalHour { get; init; }
    public Fact<double> OpportunityCostPerTick { get; init; }
    /// <summary>Remaining useful current-job time, not total duration or sunk effort; required when a job is observed.</summary>
    public Fact<double> CurrentJobRemainingTicks { get; init; }
    public ForecastCurrentWork? CurrentWork { get; init; }
    /// <summary>Explicit endpoint assumption for gaps after protected activity. Missing endpoints block route evaluation.</summary>
    public Fact<string> LocationAfterProtectedTime { get; init; }
    /// <summary>Known empty means no additional needs/protected work. Unknown is not a free day.</summary>
    public Fact<EquatableArray<ForecastOccupancy>> Occupancy { get; init; }
}

public sealed record ForecastCapacityResult(ForecastPawnCapacity Capacity, Fact<double> FreeTicks, string[] Findings);

public static class ForecastCapacity
{
    /// <summary>One pawn timeline for all work types; overlapping obligations consume time only once.</summary>
    public static ForecastCapacityResult ForecastPawnCapacityOf(ForecastCapacityInput input)
    {
        if (!double.IsFinite(input.HorizonTicks) || input.HorizonTicks < 0 || input.HorizonTicks > 7 * GameTime.TicksPerDay)
            throw new DomainException("capacity horizon must be between zero and seven game days");
        ForecastCapacityResult Result(Fact<EquatableArray<ForecastTimeWindow>> windows, Fact<double> free, params string[] findings) =>
            new(new(input.Pawn.Id.Value, input.Location, windows, input.OpportunityCostPerTick), free, findings);
        var pawn = input.Pawn;
        if ((pawn.Available.TryGetValue(out var available) && !available) || (pawn.Applies.TryGetValue(out var applies) && !applies))
            return Result(Known(default(EquatableArray<ForecastTimeWindow>)), Known(0.0), "pawn_unavailable");
        var current = input.CurrentWork ?? ForecastCurrentWork.ForecastCurrentWorkOf(pawn, input.CurrentJobRemainingTicks);
        if (current.Pawn != pawn.Id) throw new DomainException("capacity current work names another pawn");
        if (!pawn.Available.IsKnown || !pawn.Applies.IsKnown || !current.Occupied.TryGetValue(out var occupied) || (input.ProtectTimetable && !pawn.WorkHoursPerDay().IsKnown)
            || !input.LocalHour.TryGetValue(out var hour) || !input.Occupancy.TryGetValue(out var occupancy))
            return Result(default, default, "unknown_capacity_inputs");
        if (!double.IsFinite(hour) || hour < 0 || hour >= 24) throw new DomainException("invalid local hour");
        var blocked = new List<ForecastTimeWindow>();
        if (occupied)
        {
            if (!current.RemainingTicks.TryGetValue(out var remaining)) return Result(default, default, "unknown_current_job_remaining");
            if (remaining > 0) blocked.Add(new(0, Math.Min(remaining, input.HorizonTicks)));
        }
        // Each identity (pawn and id) is read once, at its first row, which every later row of it must equal.
        for (var i = 0; i < occupancy.Count; i++)
        {
            var row = occupancy[i];
            var first = true;
            for (var j = 0; j < i && first; j++) first = occupancy[j].Pawn != row.Pawn || occupancy[j].Id != row.Id;
            if (!first) continue;
            var conflicting = false;
            for (var j = i + 1; j < occupancy.Count && !conflicting; j++)
                conflicting = occupancy[j].Pawn == row.Pawn && occupancy[j].Id == row.Id && occupancy[j] != row;
            if (string.IsNullOrWhiteSpace(row.Id) || conflicting || !Enum.IsDefined(row.State))
                throw new DomainException("conflicting occupancy identity");
            if (row.Pawn != pawn.Id.Value || row.State != ForecastOccupancyState.Active) continue;
            if (!row.Start.TryGetValue(out var start) || !row.End.TryGetValue(out var end))
                return Result(default, default, "unknown_active_occupancy");
            if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end < start)
                throw new DomainException("invalid occupancy interval");
            if (start < input.HorizonTicks && end > start) blocked.Add(new(start, Math.Min(end, input.HorizonTicks)));
        }
        // Protect every non-work timetable slot. Needs estimates can reserve more time; they never add free capacity.
        for (var offset = 0; input.ProtectTimetable && offset <= Math.Ceiling(input.HorizonTicks / GameTime.TicksPerHour); offset++)
        {
            var local = Math.Floor(hour) + offset;
            var tick = Math.Max(0, (local - hour) * GameTime.TicksPerHour);
            if (tick >= input.HorizonTicks) break;
            var slot = (int)local % 24;
            var end = Math.Min(input.HorizonTicks, (local + 1 - hour) * GameTime.TicksPerHour);
            var assignment = pawn.Schedule.Value[slot];
            if (assignment is not (PawnRules.ScheduleWork or PawnRules.ScheduleAnything)) blocked.Add(new(tick, end));
        }
        // Equal blocks are one block, so the sort's instability changes nothing.
        blocked.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));
        var windows = new List<ForecastTimeWindow>();
        double cursor = 0;
        foreach (var block in blocked)
        {
            if (block.Start > cursor) windows.Add(new(cursor, block.Start) { StartLocation = cursor == 0 ? Known(input.Location) : input.LocationAfterProtectedTime });
            cursor = Math.Max(cursor, block.End);
        }
        if (cursor < input.HorizonTicks) windows.Add(new(cursor, input.HorizonTicks) { StartLocation = cursor == 0 ? Known(input.Location) : input.LocationAfterProtectedTime });
        double free = 0;
        foreach (var w in windows) free += w.End - w.Start;
        return Result(Known(EquatableArray.Wrap(windows.ToArray())), Known(free));
    }
}
