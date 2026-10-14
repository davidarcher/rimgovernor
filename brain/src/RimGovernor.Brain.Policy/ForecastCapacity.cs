namespace RimGovernor.Brain.Policy;

public enum ForecastOccupancyState { Active, Completed, Cancelled }

/// <summary>Relative ticks, including travel. Active intervals protect useful work or needs; no sunk work is charged.</summary>
public sealed record ForecastOccupancy(string Id, string Pawn, Fact<double> Start, Fact<double> End, ForecastOccupancyState State);

public sealed record ForecastCapacityInput
{
    public required WorkPawn Pawn { get; init; }
    public required string Location { get; init; }
    public double HorizonTicks { get; init; }
    public Fact<double> LocalHour { get; init; }
    public Fact<double> OpportunityCostPerTick { get; init; }
    /// <summary>Remaining useful current-job time, not total duration or sunk effort; required when a job is observed.</summary>
    public Fact<double> CurrentJobRemainingTicks { get; init; }
    /// <summary>Explicit endpoint assumption for gaps after protected activity. Missing endpoints block route evaluation.</summary>
    public Fact<string> LocationAfterProtectedTime { get; init; }
    /// <summary>Known empty means no additional needs/protected work. Unknown is not a free day.</summary>
    public Fact<EquatableArray<ForecastOccupancy>> Occupancy { get; init; }
}

public sealed record ForecastCapacityResult(ForecastPawnCapacity Capacity, Fact<double> FreeTicks, string[] Findings);

public static partial class PolicyFunctions
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
        if (!pawn.Available.IsKnown || !pawn.Applies.IsKnown || !pawn.Job.TryGetValue(out var job) || !pawn.WorkHoursPerDay().IsKnown
            || !input.LocalHour.TryGetValue(out var hour) || !input.Occupancy.TryGetValue(out var occupancy))
            return Result(default, default, "unknown_capacity_inputs");
        if (!double.IsFinite(hour) || hour < 0 || hour >= 24) throw new DomainException("invalid local hour");
        var blocked = new List<ForecastTimeWindow>();
        if (job.Def.Length != 0)
        {
            if (!input.CurrentJobRemainingTicks.TryGetValue(out var remaining)) return Result(default, default, "unknown_current_job_remaining");
            if (!double.IsFinite(remaining) || remaining < 0) throw new DomainException("invalid current job remaining time");
            if (remaining > 0) blocked.Add(new(0, Math.Min(remaining, input.HorizonTicks)));
        }
        foreach (var group in occupancy.GroupBy(o => (o.Pawn, o.Id)))
        {
            var row = group.First();
            if (string.IsNullOrWhiteSpace(row.Id) || group.Any(o => o != row) || !Enum.IsDefined(row.State))
                throw new DomainException("conflicting occupancy identity");
            if (row.Pawn != pawn.Id.Value || row.State != ForecastOccupancyState.Active) continue;
            if (!row.Start.TryGetValue(out var start) || !row.End.TryGetValue(out var end))
                return Result(default, default, "unknown_active_occupancy");
            if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end < start)
                throw new DomainException("invalid occupancy interval");
            if (start < input.HorizonTicks && end > start) blocked.Add(new(start, Math.Min(end, input.HorizonTicks)));
        }
        // Protect every non-work timetable slot. Needs estimates can reserve more time; they never add free capacity.
        for (var offset = 0; offset <= Math.Ceiling(input.HorizonTicks / GameTime.TicksPerHour); offset++)
        {
            var local = Math.Floor(hour) + offset;
            var tick = Math.Max(0, (local - hour) * GameTime.TicksPerHour);
            if (tick >= input.HorizonTicks) break;
            var slot = (int)local % 24;
            var end = Math.Min(input.HorizonTicks, (local + 1 - hour) * GameTime.TicksPerHour);
            var assignment = pawn.Schedule.Value[slot];
            if (assignment is not (PolicyConstants.ScheduleWork or PolicyConstants.ScheduleAnything)) blocked.Add(new(tick, end));
        }
        var windows = new List<ForecastTimeWindow>();
        double cursor = 0;
        foreach (var block in blocked.OrderBy(b => b.Start).ThenBy(b => b.End))
        {
            if (block.Start > cursor) windows.Add(new(cursor, block.Start));
            cursor = Math.Max(cursor, block.End);
        }
        if (cursor < input.HorizonTicks) windows.Add(new(cursor, input.HorizonTicks));
        var located = windows.Select(w => w with
        { StartLocation = w.Start == 0 ? Known(input.Location) : input.LocationAfterProtectedTime });
        return Result(Known(EquatableArray.From(located)), Known(windows.Sum(w => w.End - w.Start)));
    }
}
