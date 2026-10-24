using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// One housing move's bed-thought mood delta (#3655): <paramref name="Pawn"/> moved into a planned room; unknown when a
/// stage, the bed, the observed thoughts or the pawn is unread.
/// </summary>
public sealed record ForecastHousingDelta(string Pawn, Fact<double> Delta);

/// <summary>
/// The station facts a bedroom move's priced gain reads before the search (#3656): per pawn its needs-forecast row and
/// observed thoughts (a pawn with two rows has neither), the break curve, the pace's wellbeing weight and each planned
/// room's predicted Impressiveness stage. The bedroom step, the planned occupants and the clustering's bed merge read
/// gains through it, with no coupled context.
/// </summary>
public sealed record BedroomGainFacts
{
    public IReadOnlyDictionary<string, NeedsForecastPawn> Pawns { get; init; } = new Dictionary<string, NeedsForecastPawn>();
    public IReadOnlyDictionary<string, Fact<EquatableArray<MoodThought>>> Thoughts { get; init; } = new Dictionary<string, Fact<EquatableArray<MoodThought>>>();
    public MoodBreakModel Breaks { get; init; } = new();
    public double WellbeingWeight { get; init; }

    /// <summary>Per planned room, its closed stage when every stuff's prediction reads the same one; unknown otherwise.</summary>
    public IReadOnlyDictionary<int, Fact<int>> PlannedStages { get; init; } = new Dictionary<int, Fact<int>>();

    public static BedroomGainFacts Of(RoundsFacts facts) => new()
    {
        Pawns = (facts.ForecastObserved.Needs?.Pawns ?? []).GroupBy(p => p.Pawn, StringComparer.Ordinal).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal),
        Thoughts = facts.MoodPawns.TryGetValue(out var moods)
            ? moods.GroupBy(m => m.Id.Value, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count() == 1 ? g.Single().Thoughts : default, StringComparer.Ordinal)
            : new Dictionary<string, Fact<EquatableArray<MoodThought>>>(),
        Breaks = facts.MoodBreak,
        WellbeingWeight = ForecastValuePolicy.For(facts.Preferences).WellbeingWeight,
        PlannedStages = facts.PlannedRoomPredictions.TryGetValue(out var rows)
            ? rows.Where(r => int.TryParse(r.Room, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _))
                .GroupBy(r => int.Parse(r.Room, System.Globalization.CultureInfo.InvariantCulture))
                .ToDictionary(g => g.Key, g => g.All(r => r.Stage.IsKnown) && g.Select(r => r.Stage.Value).Distinct().Count() == 1 ? g.First().Stage : default)
            : new Dictionary<int, Fact<int>>(),
    };

    /// <summary>The <see cref="ForecastHousing.BedroomGain"/> of moving <paramref name="pawn"/> into a bedroom (or, with <paramref name="barracks"/>, a barracks) at <paramref name="stage"/>; null when unread.</summary>
    public double? Gain(string pawn, Fact<int> stage, bool barracks = false) => Pawns.TryGetValue(pawn, out var row)
        ? ForecastHousing.BedroomGain(row, stage, Thoughts.GetValueOrDefault(pawn), Breaks, WellbeingWeight, barracks) : null;

    /// <summary>
    /// The bed merge's facts of the sleepers (#3658, #3748): per pawn in id order, the capability-days a day it gains in a
    /// bedroom and in a barracks at each of the game's Impressiveness stages (the clustering computes a room's stage from
    /// its size); and the stages, known when the catalog gave them (a positive dull stage). The gains are unknown while any
    /// gain of any pawn at any stage is unread (#3747), so the chain waits for it.
    /// </summary>
    public BedMergeFacts BedMerge(ImpressivenessLevels levels)
    {
        var known = levels.Dull > 0 ? Known(levels) : default;
        var sleepers = new List<SleeperGains>();
        foreach (var pawn in Pawns.Keys.Order(StringComparer.Ordinal))
        {
            var bedroom = new double[levels.StageCount];
            var barracks = new double[levels.StageCount];
            for (var s = 0; s < levels.StageCount; s++)
            {
                if (Gain(pawn, Known(s)) is not { } b || Gain(pawn, Known(s), barracks: true) is not { } k)
                {
                    return new BedMergeFacts { Levels = known };
                }
                (bedroom[s], barracks[s]) = (b, k);
            }
            sleepers.Add(new SleeperGains(EquatableArray.From(bedroom), EquatableArray.From(barracks)));
        }
        return new BedMergeFacts { Sleepers = Known(EquatableArray.From(sleepers)), Levels = known };
    }

    /// <summary>A standing bed's room stage now, as <paramref name="pawn"/>'s rest target for it reads it; unknown without one.</summary>
    public Fact<int> BedStage(string pawn, string bed) => Pawns.TryGetValue(pawn, out var row)
        && row.Targets.FirstOrDefault(t => t.Id == bed && t.Bed is not null) is { Bed: { } room } ? room.Stage : default;
}

/// <summary>
/// The pricing of housing a colonist (#3655): the mood delta of a move, its capability-days gain per day, and the bedroom
/// investment credit. The priced merge and priced moves reuse the delta and the gain.
/// </summary>
public static class ForecastHousing
{
    /// <summary>A context's bedroom deltas and, per pawn, their break shifts; built once per context, as the search reads them per outcome.</summary>
    private sealed record Moves(IReadOnlyDictionary<(int Room, string Stuff), ForecastHousingDelta[]> Deltas, IReadOnlyDictionary<string, double[]> Shifts);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ForecastCoupledContext, Moves> Cache = new();

    /// <summary>The Investment key a closed bedroom credits its occupant under.</summary>
    public static string BedroomKey(int room, string pawn) => FormattableString.Invariant($"bedroom/{room}/{pawn}");

    /// <summary>
    /// The mood delta of moving <paramref name="pawn"/> into a bedroom at Impressiveness stage <paramref name="stage"/>. A
    /// non-ascetic gains slept_in_bedroom at that stage and loses its current bed thought: slept_in_bedroom or
    /// slept_in_barracks at its owned bed's room stage (none for a prisoner bed), plus slept_outside when that room is
    /// psychologically outdoors; none without a bed. An ascetic gains its Ascetic thought at that stage and loses the one it
    /// has now (observed: the stage of a room it owns alone, nothing in a shared barracks). With <paramref name="barracks"/>
    /// the room is a barracks (#3658): a non-ascetic gains slept_in_barracks at the stage instead, and an ascetic gains
    /// nothing and loses any Ascetic thought it has now. Every offset is the game's read.
    /// </summary>
    public static Fact<double> BedroomMoodDelta(NeedsForecastPawn pawn, Fact<int> stage, Fact<EquatableArray<MoodThought>> observed, bool barracks = false)
    {
        if (!stage.TryGetValue(out var index)) return default;
        var stages = pawn.ThoughtStages;
        if (pawn.Ascetic)
        {
            var defs = stages.Where(r => r.Role == "ascetic").Select(r => r.Thought).Distinct(StringComparer.Ordinal).ToArray();
            if (defs.Length == 0) return default;
            if (!observed.TryGetValue(out var thoughts)) return default;
            double next = 0;
            foreach (var def in barracks ? Array.Empty<string>() : defs)
            {
                if (stages.FirstOrDefault(r => r.Thought == def && r.Role == "ascetic" && r.Stage == index) is not { } row) return default;
                next += row.Offset;
            }
            return Known(next - thoughts.Where(t => defs.Contains(t.Def, StringComparer.Ordinal)).Sum(t => t.Offset));
        }
        if (stages.FirstOrDefault(r => r.Role == (barracks ? "slept_in_barracks" : "slept_in_bedroom") && r.Stage == index) is not { } gained) return default;
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
    /// The priced gain a day of moving <paramref name="pawn"/> into a bedroom (or a <paramref name="barracks"/>) at
    /// <paramref name="stage"/>, before the search (#3656): its <see cref="BedroomMoodDelta"/> through <see cref="GainPerDay"/>,
    /// over a flat mood that holds the observed unclamped target and break threshold constant across tier 1's horizon. Null
    /// when the delta, the target, the threshold or the break curve is unread.
    /// </summary>
    public static double? BedroomGain(NeedsForecastPawn pawn, Fact<int> stage, Fact<EquatableArray<MoodThought>> observed, MoodBreakModel breaks, double wellbeingWeight, bool barracks = false)
    {
        if (!BedroomMoodDelta(pawn, stage, observed, barracks).TryGetValue(out var delta)
            || !pawn.MoodUnclampedTarget.TryGetValue(out var target) || !double.IsFinite(target)
            || !pawn.BreakThreshold.TryGetValue(out var threshold) || !QuestCapacityForecast.FinitePositive(threshold)) return null;
        var model = new MoodBreakModel { Threshold = threshold };
        var horizon = ForecastCoupled.TierOneHorizonDays * GameTime.TicksPerDay;
        double[] TierTicks(double level)
        {
            var ticks = new double[LayoutMoodRules.MoodTierCount];
            level = Math.Clamp(level, 0, 1);
            model.AddTierTicks(level, level, horizon, ticks);
            return ticks;
        }
        var flat = TierTicks(target);
        var mood = new ForecastMoodResult([], default, default, default, default, default, [])
        {
            BreakRisk = Known(MoodBreakModel.Breaks(flat)), BreakTierTicks = EquatableArray.From(flat),
            ShiftedBreakTierTicks = [new(delta / 100, EquatableArray.From(TierTicks(target + delta / 100)))],
        };
        return GainPerDay(delta, mood, breaks, wellbeingWeight, ForecastCoupled.TierOneHorizonDays);
    }

    /// <summary>
    /// <paramref name="pawns"/> (in id order) with the largest <paramref name="gain"/> first (#3656), a stable sort so ties keep
    /// id order. When any pawn's gain is unread the order is kept and each such pawn is named bedroom_order_unpriced.
    /// </summary>
    public static List<T> LargestGainFirst<T>(IReadOnlyList<T> pawns, Func<T, string> pawn, Func<T, double?> gain, ISet<string>? findings)
    {
        if (pawns.Count < 2) return [.. pawns];
        var gains = pawns.Select(gain).ToArray();
        if (gains.Any(g => g is null))
        {
            for (var i = 0; i < pawns.Count; i++)
                if (gains[i] is null) findings?.Add("bedroom_order_unpriced:" + pawn(pawns[i]));
            return [.. pawns];
        }
        return [.. pawns.Select((p, i) => (p, i)).OrderByDescending(x => gains[x.i]!.Value).Select(x => x.p)];
    }

    /// <summary>
    /// Per priced planned bedroom or barracks with occupants (<see cref="ForecastCoupledContext.BedroomOccupants"/>, else the
    /// one colonist owning a bed in the predicted bedroom, every one owning a bed in the barracks) and per stuff it closes in,
    /// each occupant's <see cref="BedroomMoodDelta"/> at that closure's predicted stage; cached per context.
    /// </summary>
    internal static IReadOnlyDictionary<(int Room, string Stuff), ForecastHousingDelta[]> BedroomDeltas(ForecastCoupledContext context)
        => Cache.GetValue(context, MovesOf).Deltas;

    /// <summary>Per pawn, the distinct known nonzero bedroom shifts (delta / 100) its mood timeline integrates breaks at.</summary>
    internal static IReadOnlyDictionary<string, double[]> BreakShifts(ForecastCoupledContext context) => Cache.GetValue(context, MovesOf).Shifts;

    private static Moves MovesOf(ForecastCoupledContext c)
    {
        var deltas = new Dictionary<(int, string), ForecastHousingDelta[]>();
        var pawns = c.Needs.Pawns.GroupBy(p => p.Pawn, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var closure in c.Closures)
        {
            if (!c.PlannedRoomRoles.TryGetValue(closure.Room, out var role) || role is not (PlannedRole.Bedroom or PlannedRole.Barracks) || !c.PricedRoomRoles.Contains(role)) continue;
            var barracks = role == PlannedRole.Barracks;
            // The bedroom step's occupants, else the colonists owning a bed in the room: a bedroom's one owner (a standing
            // bedroom's wall upgrade, #3657: its delta is the stage the swap predicts against the stage its owner sleeps at
            // now), every owner of a barracks (#3658).
            var owners = pawns.Values.Where(p => p.OwnedBed.Length != 0 && closure.Prediction.Beds.Contains(p.OwnedBed)).Select(p => p.Pawn).ToArray();
            string[] occupants = c.BedroomOccupants.TryGetValue(closure.Room, out var moved) && moved.Count != 0 ? [.. moved]
                : barracks ? owners : owners is [var owner] ? [owner] : [];
            if (occupants.Length == 0) continue;
            deltas[(closure.Room, closure.Stuff)] = [.. occupants.Select(occupant => new ForecastHousingDelta(occupant, pawns.TryGetValue(occupant, out var pawn)
                ? BedroomMoodDelta(pawn, closure.Prediction.Stage, c.ObservedThoughts.GetValueOrDefault(occupant), barracks) : default))];
        }
        var shifts = deltas.Values.SelectMany(d => d).Where(d => d.Delta.TryGetValue(out var delta) && delta != 0).GroupBy(d => d.Pawn, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(d => d.Delta.Value / 100).Distinct().Order().ToArray(), StringComparer.Ordinal);
        return new(deltas, shifts);
    }

    /// <summary>
    /// The bedroom investment credit (#3655): per priced planned bedroom (or barracks, #3658) the outcome closes and per occupant, its
    /// <see cref="GainPerDay"/> times the days from closure to tier 2's horizon (RingClosure.CreditDays, #3744: the scheduled
    /// closure, or the projected one of a ring the schedule leaves open; none at or past the horizon), a gain (negative) under
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
            if (!deltas.TryGetValue((closed.Room, closed.Stuff), out var moves)) continue;
            foreach (var move in moves)
            {
                var gain = !move.Delta.TryGetValue(out var delta) ? null
                    : GainPerDay(delta, outcome.Needs.GetValueOrDefault(move.Pawn)?.Mood, context.MoodBreaks, context.Value.WellbeingWeight, horizon / GameTime.TicksPerDay);
                if (gain is not { } perDay) { findings.Add("bedroom_credit_unassessed:" + move.Pawn); continue; }
                var units = RingClosure.CreditDays(days, closed.ClosedAt);
                if (units > 0) rows.Add(new("", move.Pawn, BedroomKey(closed.Room, move.Pawn), units, -perDay * units));
            }
        }
    }
}
