using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// One housing move's bed-thought mood delta (#3655): <paramref name="Pawn"/> moved into a planned room; unknown when a
/// stage, the bed, the observed thoughts or the pawn is unread.
/// </summary>
public sealed record ForecastHousingDelta(string Pawn, Fact<double> Delta);

/// <summary>
/// The pricing of housing a colonist (#3655): the mood delta of a move, its capability-days gain per day, and the bedroom
/// investment credit. The priced merge and priced moves reuse the delta and the gain.
/// </summary>
public static class ForecastHousing
{
    /// <summary>A context's bedroom deltas and, per pawn, their break shifts; built once per context, as the search reads them per outcome.</summary>
    private sealed record Moves(IReadOnlyDictionary<(int Room, string Stuff), ForecastHousingDelta> Deltas, IReadOnlyDictionary<string, double[]> Shifts);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ForecastCoupledContext, Moves> Cache = new();

    /// <summary>The Investment key a closed bedroom credits its occupant under.</summary>
    public static string BedroomKey(int room, string pawn) => FormattableString.Invariant($"bedroom/{room}/{pawn}");

    /// <summary>
    /// The mood delta of moving <paramref name="pawn"/> into a bedroom at Impressiveness stage <paramref name="stage"/>. A
    /// non-ascetic gains slept_in_bedroom at that stage and loses its current bed thought: slept_in_bedroom or
    /// slept_in_barracks at its owned bed's room stage (none for a prisoner bed), plus slept_outside when that room is
    /// psychologically outdoors; none without a bed. An ascetic gains its Ascetic thought at that stage and loses the one it
    /// has now (observed: the stage of a room it owns alone, nothing in a shared barracks). Every offset is the game's read.
    /// </summary>
    public static Fact<double> BedroomMoodDelta(NeedsForecastPawn pawn, Fact<int> stage, Fact<EquatableArray<MoodThought>> observed)
    {
        if (!stage.TryGetValue(out var index)) return default;
        var stages = pawn.ThoughtStages;
        if (pawn.Ascetic)
        {
            var defs = stages.Where(r => r.Role == "ascetic").Select(r => r.Thought).Distinct(StringComparer.Ordinal).ToArray();
            if (defs.Length == 0) return default;
            if (!observed.TryGetValue(out var thoughts)) return default;
            double next = 0;
            foreach (var def in defs)
            {
                if (stages.FirstOrDefault(r => r.Thought == def && r.Role == "ascetic" && r.Stage == index) is not { } row) return default;
                next += row.Offset;
            }
            return Known(next - thoughts.Where(t => defs.Contains(t.Def, StringComparer.Ordinal)).Sum(t => t.Offset));
        }
        if (stages.FirstOrDefault(r => r.Role == "slept_in_bedroom" && r.Stage == index) is not { } gained) return default;
        double now = 0;
        if (pawn.OwnedBed.Length != 0)
        {
            if (pawn.Targets.FirstOrDefault(t => t.Id == pawn.OwnedBed && t.Bed is not null) is not { Bed: { } room } bed) return default;
            if (bed.Cell is null || !bed.Cell.PsychologicallyOutdoors.TryGetValue(out var outside)) return default;
            if (outside)
            {
                if (stages.FirstOrDefault(r => r.Role == "slept_outside" && r.Stage == 0) is not { } row) return default;
                now += row.Offset;
            }
            var thought = room.ForPrisoners ? "" : room.Role == ForecastRoofConsequences.BedroomRole ? "slept_in_bedroom" : room.Role == ForecastRoofConsequences.BarracksRole ? "slept_in_barracks" : "";
            if (thought.Length != 0)
            {
                if (!room.Stage.TryGetValue(out var at)) return default;
                if (stages.FirstOrDefault(r => r.Role == thought && r.Stage == at) is not { } row) return default;
                now += row.Offset;
            }
        }
        return Known(gained.Offset - now);
    }

    /// <summary>
    /// The capability-days a mood delta gains a day: WellbeingWeight times delta / 100, plus the break days a day the
    /// target mood shifted by delta / 100 loses fewer than the unshifted one over the tier-1 horizon
    /// (<see cref="ForecastMoodResult.ShiftedBreakTierTicks"/>). Null when the mood or a break result is unknown.
    /// </summary>
    public static double? GainPerDay(double delta, ForecastMoodResult? mood, MoodBreakModel breaks, double wellbeingWeight, double horizonDays)
    {
        if (horizonDays <= 0 || mood is not { BreakRisk.IsKnown: true } || breaks.DaysLost(mood.BreakTierTicks) is not { } before) return null;
        var after = before;
        if (delta != 0)
        {
            var shift = delta / 100;
            if (mood.ShiftedBreakTierTicks.FirstOrDefault(s => s.Shift == shift) is not { } shifted || breaks.DaysLost(shifted.TierTicks) is not { } lost) return null;
            after = lost;
        }
        return wellbeingWeight * delta / 100 + (before - after) / horizonDays;
    }

    /// <summary>
    /// Per priced planned bedroom with an occupant (<see cref="ForecastCoupledContext.BedroomOccupants"/>) and per stuff it
    /// closes in, the occupant's <see cref="BedroomMoodDelta"/> at that closure's predicted stage; cached per context.
    /// </summary>
    internal static IReadOnlyDictionary<(int Room, string Stuff), ForecastHousingDelta> BedroomDeltas(ForecastCoupledContext context)
        => Cache.GetValue(context, MovesOf).Deltas;

    /// <summary>Per pawn, the distinct known nonzero bedroom shifts (delta / 100) its mood timeline integrates breaks at.</summary>
    internal static IReadOnlyDictionary<string, double[]> BreakShifts(ForecastCoupledContext context) => Cache.GetValue(context, MovesOf).Shifts;

    private static Moves MovesOf(ForecastCoupledContext c)
    {
        var deltas = new Dictionary<(int, string), ForecastHousingDelta>();
        if (c.PricedRoomRoles.Contains(PlannedRole.Bedroom) && c.BedroomOccupants.Count != 0)
        {
            var pawns = c.Needs.Pawns.GroupBy(p => p.Pawn, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            foreach (var closure in c.Closures)
            {
                if (!c.PlannedRoomRoles.TryGetValue(closure.Room, out var role) || role != PlannedRole.Bedroom
                    || !c.BedroomOccupants.TryGetValue(closure.Room, out var occupant)) continue;
                deltas[(closure.Room, closure.Stuff)] = new(occupant, pawns.TryGetValue(occupant, out var pawn)
                    ? BedroomMoodDelta(pawn, closure.Prediction.Stage, c.ObservedThoughts.GetValueOrDefault(occupant)) : default);
            }
        }
        var shifts = deltas.Values.Where(d => d.Delta.TryGetValue(out var delta) && delta != 0).GroupBy(d => d.Pawn, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(d => d.Delta.Value / 100).Distinct().Order().ToArray(), StringComparer.Ordinal);
        return new(deltas, shifts);
    }

    /// <summary>
    /// The bedroom investment credit (#3655): per priced planned bedroom the outcome closes, its occupant's
    /// <see cref="GainPerDay"/> times the days from closure to tier 2's horizon, a gain (negative) under
    /// <see cref="BedroomKey"/>. An occupant whose delta or mood is unknown is named bedroom_credit_unassessed and credited nothing.
    /// </summary>
    internal static void BedroomCredits(ForecastCoupledOutcome outcome, ForecastCoupledContext context, double horizon,
        List<ForecastInvestmentContribution> rows, SortedSet<string> findings)
    {
        if (!context.InvestmentHorizonDays.TryGetValue(out var days)) return;
        if (outcome.ClosedRooms.Count == 0) return;
        var deltas = BedroomDeltas(context);
        foreach (var closed in outcome.ClosedRooms)
        {
            if (!deltas.TryGetValue((closed.Room, closed.Stuff), out var move)) continue;
            var gain = !move.Delta.TryGetValue(out var delta) ? null
                : GainPerDay(delta, outcome.Needs.GetValueOrDefault(move.Pawn)?.Mood, context.MoodBreaks, context.Value.WellbeingWeight, horizon / GameTime.TicksPerDay);
            if (gain is not { } perDay) { findings.Add("bedroom_credit_unassessed:" + move.Pawn); continue; }
            var units = days - closed.ClosedAt / GameTime.TicksPerDay;
            if (units > 0) rows.Add(new("", move.Pawn, BedroomKey(closed.Room, move.Pawn), units, -perDay * units));
        }
    }
}
