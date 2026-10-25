namespace RimGovernor.Brain.Policy;

public enum ForecastOccupancyState { Active, Untimed, Completed, Cancelled }

/// <summary>Relative ticks, including travel. Active intervals protect useful work or needs; no sunk work is charged.</summary>
public sealed record ForecastOccupancy(string Id, string Pawn, double Start, double End, ForecastOccupancyState State);

/// <summary>
/// The pawn a capacity forecast times: whether admission evidence establishes availability, unavailability or remains unread,
/// and its timetable, one TimeAssignmentDef per hour (hour 0 first; empty for a pawn without one).
/// </summary>
public enum ForecastPawnCapacityState { Unread, Unavailable, Available }

public sealed record ForecastCapacityPawn
{
    public required PawnId Id { get; init; }
    public ForecastPawnCapacityState State { get; init; }
    public EquatableArray<string> Schedule { get; init; } = [];
}

public sealed record ForecastCapacityInput
{
    public bool ProtectTimetable { get; init; } = true;
    public required ForecastCapacityPawn Pawn { get; init; }
    public required string Location { get; init; }
    public double HorizonTicks { get; init; }
    public double LocalHour { get; init; }
    public required double OpportunityCostPerTick { get; init; }
    /// <summary>The pawn's current job: occupied or free, with its remaining useful time (ForecastCurrentWork.ForecastCurrentWorkOf).</summary>
    public required ForecastCurrentWork CurrentWork { get; init; }
    /// <summary>Explicit endpoint assumption for gaps after protected activity. Null (unread) endpoints block route evaluation.</summary>
    public string? LocationAfterProtectedTime { get; init; }
    /// <summary>A complete empty census means no additional protected work. A truncated census is not a free day.</summary>
    public ForecastProtection Occupancy { get; init; } = new ForecastProtection.Truncated();
}

/// <summary>Complete protected intervals, or a journal census truncated before every owner was read.</summary>
public abstract record ForecastProtection
{
    private ForecastProtection() { }
    public sealed record Read(EquatableArray<ForecastOccupancy> Intervals) : ForecastProtection;
    public sealed record Truncated : ForecastProtection;
}

public sealed record ForecastCapacityResult(ForecastPawnCapacity Capacity, string[] Findings);

public static class ForecastCapacity
{
    /// <summary>Interpret the production-admission observation once for roster and coupled capacity. Unread admission evidence cannot create free time.</summary>
    public static ForecastCapacityInput InputOf(string pawn, WorkPawnAvailability? observed, double horizon, ForecastProtection occupancy,
        bool protectTimetable = true)
    {
        var state = observed is null ? ForecastPawnCapacityState.Unread
            : observed.Available && observed.Applies ? ForecastPawnCapacityState.Available : ForecastPawnCapacityState.Unavailable;
        return new()
        {
            Pawn = new() { Id = new(pawn), State = state, Schedule = observed?.Schedule ?? [] },
            Location = "start", HorizonTicks = horizon, ProtectTimetable = protectTimetable,
            LocalHour = observed?.LocalHour ?? 0, OpportunityCostPerTick = 1,
            CurrentWork = new(new(pawn), observed?.Occupied == true, WorkType.None, new ForecastWorkRemaining.Untimed()),
            Occupancy = occupancy,
        };
    }
    /// <summary>One pawn timeline for all work types; overlapping obligations consume time only once.</summary>
    public static ForecastCapacityResult ForecastPawnCapacityOf(ForecastCapacityInput input)
    {
        if (!double.IsFinite(input.HorizonTicks) || input.HorizonTicks < 0 || input.HorizonTicks > 7 * GameTime.TicksPerDay)
            throw new DomainException("capacity horizon must be between zero and seven game days");
        ForecastCapacityResult Result(ForecastCapacityWindows windows, params string[] findings) =>
            new(new(input.Pawn.Id.Value, input.Location, windows, input.OpportunityCostPerTick), findings);
        var pawn = input.Pawn;
        if (!Enum.IsDefined(pawn.State)) throw new DomainException("invalid pawn capacity state");
        if (pawn.State == ForecastPawnCapacityState.Unavailable)
            return Result(new ForecastCapacityWindows.Read([]), "pawn_unavailable");
        var current = input.CurrentWork;
        if (current.Pawn != pawn.Id) throw new DomainException("capacity current work names another pawn");
        if (pawn.State == ForecastPawnCapacityState.Unread || (input.ProtectTimetable && PawnRules.WorkHours(pawn.Schedule) is null)
            || input.Occupancy is not ForecastProtection.Read(var occupancy))
            return Result(new ForecastCapacityWindows.Unread(), "unknown_capacity_inputs");
        var hour = input.LocalHour;
        if (!double.IsFinite(hour) || hour < 0 || hour >= 24) throw new DomainException("invalid local hour");
        var blocked = new List<ForecastTimeWindow>();
        if (current.Occupied)
        {
            if (current.Remaining is not ForecastWorkRemaining.Timed(var remaining)) return Result(new ForecastCapacityWindows.Unread(), "unknown_current_job_remaining");
            if (!double.IsFinite(remaining) || remaining < 0) throw new DomainException("invalid current job remaining time");
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
            if (row.Pawn != pawn.Id.Value || row.State is ForecastOccupancyState.Completed or ForecastOccupancyState.Cancelled) continue;
            if (row.State == ForecastOccupancyState.Untimed)
                return Result(new ForecastCapacityWindows.Unread(), "unknown_active_occupancy");
            var (start, end) = (row.Start, row.End);
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
            var assignment = pawn.Schedule[slot];
            if (assignment is not (PawnRules.ScheduleWork or PawnRules.ScheduleAnything)) blocked.Add(new(tick, end));
        }
        // Equal blocks are one block, so the sort's instability changes nothing.
        blocked.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));
        var windows = new List<ForecastTimeWindow>();
        double cursor = 0;
        foreach (var block in blocked)
        {
            if (block.Start > cursor) windows.Add(new(cursor, block.Start) { StartLocation = cursor == 0 ? input.Location : input.LocationAfterProtectedTime });
            cursor = Math.Max(cursor, block.End);
        }
        if (cursor < input.HorizonTicks) windows.Add(new(cursor, input.HorizonTicks) { StartLocation = cursor == 0 ? input.Location : input.LocationAfterProtectedTime });
        return Result(new ForecastCapacityWindows.Read(EquatableArray.Wrap(windows.ToArray())));
    }
}
