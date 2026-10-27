using RimGovernor.Brain.GameFacts.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>A preference under current mechanics, never a prediction of service or production.</summary>
public abstract record NativeTimetable
{
    private NativeTimetable() { }
    public sealed record Planned(EquatableArray<string> Slots) : NativeTimetable;
    public sealed record Unassessed : NativeTimetable;
}

public static partial class PawnRules
{
    // Policy choices: a regular night, and two flexible hours for meals, travel and other native choices.
    private const int OrdinarySleepStart = 22;
    private const int FlexibleHours = 2;

    public static NativeTimetable RoutineTimetable(NeedsForecastPawn pawn)
    {
        if (!ForecastNeeds.Voluntary(pawn.State) || pawn.SleepBlocked)
            return new NativeTimetable.Unassessed();
        var rest = ServiceHours(pawn, "rest");
        var joy = ServiceHours(pawn, "joy");
        if (rest is ForecastTerm.Unassessed || joy is ForecastTerm.Unassessed) return new NativeTimetable.Unassessed();
        // Remove arithmetic noise at whole-hour boundaries before rounding a preference upward.
        var sleepHours = Math.Clamp((int)Math.Ceiling(Math.Round(((ForecastTerm.Assessed)rest).Value, 9)), 0, 24);
        var joyHours = Math.Clamp((int)Math.Ceiling(Math.Round(((ForecastTerm.Assessed)joy).Value, 9)), 0, 24 - sleepHours);
        // Evaluate the observed hour-conditional thoughts, rather than guessing a trait's preferred hours.
        // Equal scores retain the regular night. Unmodeled mood does not become a claimed mood benefit.
        var bestStart = OrdinarySleepStart;
        double bestScore = double.NegativeInfinity;
        for (var shift = 0; shift < 24; shift++)
        {
            var start = (OrdinarySleepStart + shift) % 24;
            double score = 0;
            if (pawn.Mood?.Conditional is ConditionalMood.Modeled modeled)
                foreach (var thought in modeled.Thoughts.Where(t => t.AwakeOnly))
                    for (var hour = 0; hour < 24; hour++)
                    {
                        var midpoint = hour + .5;
                        var active = thought.StartHour <= thought.EndHour
                            ? midpoint >= thought.StartHour && midpoint < thought.EndHour
                            : midpoint >= thought.StartHour || midpoint < thought.EndHour;
                        if (active && (hour - start + 24) % 24 >= sleepHours) score += thought.Offset;
                    }
            if (score > bestScore) { bestScore = score; bestStart = start; }
        }
        var slots = Enumerable.Repeat(ScheduleWork, 24).ToArray();
        for (var i = 0; i < sleepHours; i++) slots[(bestStart + i) % 24] = ScheduleSleep;
        for (var i = 1; i <= joyHours; i++) slots[(bestStart - i + 24) % 24] = ScheduleJoy;
        for (var i = 0; i < Math.Min(FlexibleHours, 24 - sleepHours - joyHours); i++)
            slots[(bestStart + sleepHours + i) % 24] = ScheduleAnything;
        return new NativeTimetable.Planned(EquatableArray.From(slots));
    }

    private static ForecastTerm ServiceHours(NeedsForecastPawn pawn, string kind)
    {
        var need = pawn.Needs.SingleOrDefault(n => n.Kind == kind);
        // A complete census without this need is a pawn who does not require this service.
        if (need is null || need.FallPerTick == 0) return new ForecastTerm.Assessed(0);
        DomainException.Require(double.IsFinite(need.FallPerTick) && need.FallPerTick > 0, "timetable need fall");
        var targets = pawn.Targets.Where(t => t.Kind == kind && t.Recovery is NeedRecovery.Rate).ToArray();
        var gain = targets.Select(t => ((NeedRecovery.Rate)t.Recovery).GainPerTick).DefaultIfEmpty(0).Max();
        var unserved = need.UnservedGainPerTick;
        if (kind == "joy")
        {
            if (need.OutdoorShareByTwelfth.Count != 12)
                return new ForecastTerm.Unassessed("timetable_outdoor_recreation_unread:" + pawn.Pawn);
            unserved *= need.OutdoorShareByTwelfth[0];
        }
        gain = Math.Max(gain, unserved);
        if (!(gain > 0)) return new ForecastTerm.Unassessed("timetable_service_unavailable:" + pawn.Pawn + ":" + kind);
        // Current category/tolerance rates size a preference only. Their native Unsupported markers
        // forbid an exact future trajectory, not use of the observed rate for this conditional budget.
        var approach = gain <= unserved ? 0 : targets.Where(t => ((NeedRecovery.Rate)t.Recovery).GainPerTick == gain).Min(t => t.TravelTicks);
        return new ForecastTerm.Assessed(NeedServiceRules.ContinuousHours(need.FallPerTick, gain) + approach / GameTime.TicksPerHour);
    }

    public static IReadOnlyList<WorkAssignment> TimetableChanges(IReadOnlyList<WorkPawn> pawns, NeedsForecastRead? read)
    {
        if (read is null) return [];
        var needs = read.Pawns.ToDictionary(p => p.Pawn, StringComparer.Ordinal);
        var changes = new List<WorkAssignment>();
        foreach (var pawn in pawns)
            if (pawn.Available && pawn.Work is PawnWorkTable.Works { Manual: true }
                && needs.TryGetValue(pawn.Id.Value, out var current)
                && RoutineTimetable(current) is NativeTimetable.Planned plan && !pawn.Schedule.SequenceEqual(plan.Slots))
                changes.Add(WorkAssignment.NewScheduleAssignment(pawn.Id, [], plan.Slots));
        return changes;
    }
}

public static partial class WorkRules
{
    /// <summary>One settings action per pawn; timetable-only changes use the same admission/Hands path.</summary>
    public static IReadOnlyList<WorkAssignment> NativeSettingsChanges(IReadOnlyList<WorkPawn> pawns,
        IReadOnlyList<PawnWorkAssignment> assignments, NeedsForecastRead? needs)
    {
        var schedules = PawnRules.TimetableChanges(pawns, needs).ToDictionary(a => a.Pawn);
        var byId = pawns.ToDictionary(p => p.Id);
        var changes = new List<WorkAssignment>();
        foreach (var assignment in assignments)
        {
            var pawn = byId[assignment.Pawn];
            if (pawn.Work is not PawnWorkTable.Works { Manual: true }) continue;
            var (priorities, ok) = WorkChanges(pawn, assignment);
            if (!ok) throw new RequiredFactException("native work settings readback:" + assignment.Pawn);
            if (schedules.TryGetValue(assignment.Pawn, out var schedule))
                changes.Add(WorkAssignment.NewScheduleAssignment(assignment.Pawn, priorities, schedule.Schedule));
            else if (priorities.Count > 0) changes.Add(WorkAssignment.NewWorkAssignment(assignment.Pawn, priorities));
        }
        return changes;
    }
}
