using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// The colonist a planned throne room or suite would serve (#3807, #3826) and its title's requirement of that room: the
/// RoomRequirement_Area cells and the RoomRequirement_Impressiveness (0: none) of its most senior title's throneRoomRequirements,
/// or of its bedroomRequirements less those its precepts waive. An untitled suite occupant has no requirement (0, 0): it has
/// no title room thought, and its suite relieves only its trait bedroom thoughts.
/// </summary>
public sealed record RoyalRoomNeed(string Pawn, int MinArea, int MinImpressiveness);

/// <summary>
/// A sleeping bed def in a stuff (""), as the catalog reads it at Normal quality (#3840): <see cref="Ground"/> when it costs
/// nothing (ApplyBedThoughts' SleptOnGround), its rest effectiveness (the BedRestEffectiveness stat, its valueIfMissing for a
/// def without the stat base, as the rest tick reads it) and its Comfort stat.
/// </summary>
public sealed record SleepingBedFacts(string Def, string Stuff, bool Ground, double RestEffectiveness, double Comfort);

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
        Thoughts = (facts.MoodPawns.TryGetValue(out var moods) ? moods : [])
            .GroupBy(m => m.Id.Value, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count() == 1 ? g.Single().Thoughts : default, StringComparer.Ordinal),
        Breaks = facts.MoodBreak,
        WellbeingWeight = ForecastValuePolicy.For(facts.Preferences).WellbeingWeight,
        PlannedStages = facts.PlannedRoomPredictions
            .Where(r => int.TryParse(r.Room, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _))
            .GroupBy(r => int.Parse(r.Room, System.Globalization.CultureInfo.InvariantCulture))
            .ToDictionary(g => g.Key, g => g.Select(r => r.Stage).Distinct().Count() == 1 ? Known(g.First().Stage) : default),
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
        && row.Targets.FirstOrDefault(t => t.Id == bed && t.Bed is not null) is { Bed: { } room } ? Known(room.Stage) : default;
}

/// <summary>
/// The pricing of housing a colonist (#3655): the mood delta of a move, its capability-days gain per day, and the bedroom
/// investment credit. The priced merge and priced moves reuse the delta and the gain.
/// </summary>
public static class ForecastHousing
{
    /// <summary>
    /// A context's bedroom moves (per closure, each occupant's <see cref="Sleeper"/>), its royal rooms'
    /// (<see cref="RoyalRoomCredit"/>: per assessed throne room or suite closure, its colonist and the mood the room relieves, 0
    /// for none) and, per pawn, their break shifts; built once per context, as the search reads them per outcome.
    /// </summary>
    private sealed record Moves(IReadOnlyDictionary<(int Room, string Stuff), Sleeper?[]> Deltas,
        IReadOnlyDictionary<(int Room, string Stuff), (string Pawn, double Delta)> Royal, IReadOnlyDictionary<string, double[]> Shifts);

    /// <summary>
    /// An occupant's move into a planned bedroom or barracks (#3840), null when its room delta (<see cref="BedroomMoodDelta"/>)
    /// is unread: <see cref="Mood"/> every mood term but the planned room's sleep temperature (the room delta,
    /// <see cref="BedMoodDelta"/> and the cold or heat memory it sleeps with now relieved), <see cref="Rest"/> its
    /// <see cref="RestDaysFreed"/>, and the temperature memory it would get in the room below its sleep range
    /// (<see cref="Cold"/>, slept_in_cold) or above it (<see cref="Heat"/>, slept_in_heat), null for a pawn without the row.
    /// </summary>
    private sealed record Sleeper(NeedsForecastPawn Pawn, double Mood, double Rest, NeedThoughtStage? Cold, NeedThoughtStage? Heat)
    {
        /// <summary>The mood delta at the room's sleep <paramref name="temperature"/>; throws when the memory it gets there is unread.</summary>
        public double At(double temperature) => !Sleeps ? Mood
            : temperature < Pawn.SleepComfortableMin ? With(Cold ?? throw new RequiredFactException("slept_in_cold thought stage"))
            : temperature > Pawn.SleepComfortableMax ? With(Heat ?? throw new RequiredFactException("slept_in_heat thought stage")) : Mood;

        /// <summary>Every mood delta the room can give, at any temperature (the break shifts the timeline integrates).</summary>
        public IEnumerable<double> Deltas() => new[] { Cold, Heat }.OfType<NeedThoughtStage>().Select(With).Prepend(Mood);

        private double With(NeedThoughtStage memory) => Mood + memory.Offset;

        /// <summary>False for a pawn without a rest need, which never lies down to get a temperature memory.</summary>
        public bool Sleeps { get; init; } = true;
    }

    /// <summary>The <see cref="NeedThoughtStage.Role"/> of the title thought an unmet bedroom requirement gives (ThoughtWorker_BedroomRequirementsNotMet).</summary>
    public const string TitleBedroomUnmetRole = "title_bedroom_unmet";

    /// <summary>The role of the title thought a colonist without a personal bedroom has (ThoughtWorker_NoPersonalBedroom).</summary>
    public const string TitleNoBedroomRole = "title_no_bedroom";

    /// <summary>The role of the title thought an unmet throne-room requirement gives (ThoughtWorker_ThroneRoomRequirementsNotMet).</summary>
    public const string TitleThroneUnmetRole = "title_throne_unmet";

    /// <summary>The role of the title thought a colonist without an assigned throne has (ThoughtWorker_NoThroneroom).</summary>
    public const string TitleNoThroneRole = "title_no_throne";

    /// <summary>The role of the Greedy trait's own-room thought (ThoughtWorker_Greedy: stage 0 without an owned room, else its Impressiveness stage + 1).</summary>
    public const string GreedyRole = "greedy";

    /// <summary>The role of the Jealous trait's bedroom thought (ThoughtWorker_BedroomJealous).</summary>
    public const string JealousRole = "jealous";

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ForecastCoupledContext, Moves> Cache = new();


    /// <summary>The <see cref="NeedThoughtStage.Role"/> of the comfort need's thought (ThoughtWorker_NeedComfort, #3840).</summary>
    public const string NeedComfortRole = "need_comfort";

    /// <summary>Where a pawn sleeps now (#3840, <see cref="NeedsForecastPawn.Sleep"/>) and the offset of the room thought it gets there (0 for none).</summary>
    internal sealed record SleepPlace(NeedSleep Read, double RoomThought);

    /// <summary>
    /// Where <paramref name="pawn"/> sleeps now (#3840): its <see cref="NeedsForecastPawn.Sleep"/> read and the room thought
    /// ApplyBedThoughts gives in that bed, which the pawn owns (its owned bed, or the one FindBedFor gave it, claimed by
    /// Toils_Bed.ClaimBedIfNonMedical): slept_in_bedroom or slept_in_barracks at the bed's room stage, none on the ground, for
    /// a prisoner bed or an ascetic. Null when the read, the bed's rest target or room, its stage or the thought row is unread.
    /// </summary>
    internal static SleepPlace? SleepNow(NeedsForecastPawn pawn)
    {
        if (pawn.Sleep is not { } read) return null;
        if (read.Bed.Length == 0) return new(read, 0);
        if (pawn.Targets.FirstOrDefault(t => t.Id == read.Bed && t.Bed is not null) is not { Bed: { } room }) return null;
        if (pawn.Ascetic) return new(read, 0);
        return OwnedRoomThought(room, pawn.ThoughtStages) is { } thought ? new(read, thought) : null;
    }
    private static NeedRate? RestRate(NeedsForecastPawn pawn) => pawn.Needs.FirstOrDefault(n => n.Kind == "rest");

    /// <summary>
    /// The mood delta of moving <paramref name="pawn"/> into a bedroom at Impressiveness stage <paramref name="stage"/>, its room
    /// terms: a non-ascetic gains slept_in_bedroom at that stage and loses the room thought it sleeps with now
    /// (<see cref="SleepNow"/>); an ascetic gains its Ascetic thought at that stage and loses the one it has now (observed: the
    /// stage of a room it owns alone, nothing in a shared barracks). With <paramref name="barracks"/> the room is a barracks
    /// (#3658): a non-ascetic gains slept_in_barracks at the stage instead, and an ascetic gains nothing and loses any Ascetic
    /// thought it has now. Either loses slept_outside when it sleeps psychologically outdoors now (the closed room is not).
    /// Every offset is the game's read. The bed's own terms are <see cref="BedMoodDelta"/>'s.
    /// </summary>
    public static Fact<double> BedroomMoodDelta(NeedsForecastPawn pawn, Fact<int> stage, Fact<EquatableArray<MoodThought>> observed, bool barracks = false)
    {
        if (!stage.TryGetValue(out var index) || SleepNow(pawn) is not { } now) return default;
        var stages = pawn.ThoughtStages;
        double outside = 0;
        if (now.Read.Outside)
        {
            if (stages.FirstOrDefault(r => r.Role == "slept_outside" && r.Stage == 0) is not { } row) return default;
            outside = -row.Offset;
        }
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
            return Known(next - thoughts.Where(t => defs.Contains(t.Def, StringComparer.Ordinal)).Sum(t => t.Offset) + outside);
        }
        if (stages.FirstOrDefault(r => r.Role == (barracks ? "slept_in_barracks" : "slept_in_bedroom") && r.Stage == index) is not { } gained) return default;
        return Known(gained.Offset - now.RoomThought + outside);
    }

    /// <summary>
    /// The bed's mood terms of a move (#3840): SleptOnGround lost (or gained) as the planned bed is costed (or not) against where
    /// the pawn sleeps now, and the comfort need's thought over the day in the planned bed less in the bed now
    /// (<see cref="ComfortMood"/>). Throws when a needed row or the curve is unread.
    /// </summary>
    internal static double BedMoodDelta(NeedsForecastPawn pawn, SleepPlace now, bool ground, double comfort, NeedCurve curve)
    {
        double delta = 0;
        if (now.Read.Ground != ground)
        {
            var row = Memory(pawn, "slept_on_ground") ?? throw new RequiredFactException("slept_on_ground thought stage");
            delta += now.Read.Ground ? -row.Offset : row.Offset;
        }
        return now.Read.Comfort == comfort ? delta : delta + ComfortMood(pawn, comfort, curve) - ComfortMood(pawn, now.Read.Comfort, curve);
    }

    private static NeedThoughtStage? Memory(NeedsForecastPawn pawn, string role) => pawn.ThoughtStages.FirstOrDefault(r => r.Role == role && r.Stage == 0);

    /// <summary>
    /// The time-averaged mood of the comfort need's thought (#3840) for a pawn that lies at <paramref name="level"/> through its
    /// sleep: the need (Need_Seeker over its NeedDef curve) run over the layout occupancy day (LayoutComfortRules.EnvDay, awake
    /// at an instant level of 0) to its steady day, each hour priced at the pawn's need_comfort row of the stage it is in. 0 for
    /// a pawn without the rows (no comfort need); throws when the curve or a reached stage's row is unread.
    /// </summary>
    internal static double ComfortMood(NeedsForecastPawn pawn, double level, NeedCurve curve)
    {
        var rows = pawn.ThoughtStages.Where(r => r.Role == NeedComfortRole).ToArray();
        if (rows.Length == 0) return 0;
        var (run, ok) = new SeekerNeed(curve, LayoutComfortRules.SeekerGame().Comfort).Run(LayoutComfortRules.EnvDay(level, []));
        if (!ok) throw new RequiredFactException("comfort need curve");
        double mood = 0;
        for (var i = 1; i < run!.StageHours.Count; i++)
        {
            if (run.StageHours[i] == 0) continue;
            var at = rows.Where(r => r.Stage == i - 1).DistinctBy(r => r.Thought).ToArray();
            if (at.Length == 0) throw new RequiredFactException("need_comfort thought stage " + (i - 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            mood += run.StageHours[i] * at.Sum(r => r.Offset);
        }
        return mood / 24;
    }

    /// <summary>
    /// The capability-days a day a pawn's sleep frees moving from a bed gaining <paramref name="now"/> rest a tick to one gaining
    /// <paramref name="planned"/> (#3840): asleep, rest rises at the gain and awake falls at the rest need's FallPerTick, so
    /// the steady day spends fall / (fall + gain) of itself asleep, a whole capability loss as RestLoss counts a collapse. 0
    /// for the same gain; throws when the fall is unread or a rate is not positive.
    /// </summary>
    internal static double RestDaysFreed(NeedRate rest, double now, double planned)
    {
        if (now == planned) return 0;
        var fall = rest.FallPerTick;
        if (fall < 0 || now <= 0 || planned <= 0) throw new RequiredFactException("rest fall and gain per tick");
        return fall / (fall + now) - fall / (fall + planned);
    }

    /// <summary>
    /// The sleep temperature memory ApplyBedThoughts gives at <paramref name="temperature"/> (#3840): slept_in_cold below the
    /// pawn's <see cref="NeedsForecastPawn.SleepComfortableMin"/>, slept_in_heat above its max, else none (0). Throws when the
    /// memory's row is unread.
    /// </summary>
    internal static double SleepTemperatureThought(NeedsForecastPawn pawn, double temperature)
    {
        var role = temperature < pawn.SleepComfortableMin ? "slept_in_cold" : temperature > pawn.SleepComfortableMax ? "slept_in_heat" : "";
        return role.Length == 0 ? 0 : (Memory(pawn, role) ?? throw new RequiredFactException(role + " thought stage")).Offset;
    }

    /// <summary>
    /// Per planned room, the sleeping facts of its planned beds (#3840, <see cref="ForecastCoupledContext.PlannedBeds"/>): each
    /// Build site of a sleeping bed def, in site id order, one per anchor; null for a stuff without a row.
    /// </summary>
    public static IReadOnlyDictionary<int, IReadOnlyList<SleepingBedFacts?>> PlannedBedsOf(IEnumerable<PlannedBuildSite> sites, IReadOnlyList<SleepingBedFacts> beds)
    {
        var defs = beds.Select(b => b.Def).ToHashSet(StringComparer.Ordinal);
        return sites.Where(s => s.Room > 0 && s.Zone.Length == 0 && s.Op == OpKind.Build && defs.Contains(s.Definition))
            .GroupBy(s => s.Room)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<SleepingBedFacts?>)[.. g.OrderBy(s => s.Id, StringComparer.Ordinal)
                .SelectMany(s => s.Positions.Select(_ => beds.FirstOrDefault(b => b.Def == s.Definition && b.Stuff == s.Stuff)))]);
    }

    /// <summary>
    /// The room thought ApplyBedThoughts gives a non-ascetic in the bed it owns, from <paramref name="room"/>: slept_in_bedroom or
    /// slept_in_barracks at the room's Impressiveness stage, none in a prisoner bed or another room. Null when the stage or its row is unread.
    /// </summary>
    internal static double? OwnedRoomThought(NeedBedRoom room, EquatableArray<NeedThoughtStage> stages)
    {
        var thought = room.ForPrisoners ? "" : room.Role == ForecastRoofConsequences.BedroomRole ? "slept_in_bedroom" : room.Role == ForecastRoofConsequences.BarracksRole ? "slept_in_barracks" : "";
        if (thought.Length == 0) return 0;
        return stages.FirstOrDefault(r => r.Role == thought && r.Stage == room.Stage)?.Offset;
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
            || pawn.Mood is not { UnclampedTarget: var target, BreakThreshold: var threshold } || !double.IsFinite(target)
            || !QuestCapacityForecast.FinitePositive(threshold)) return null;
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

    /// <summary>Per pawn, the distinct known nonzero bedroom and garment (<see cref="ForecastCoupledContext.ApparelShifts"/>) shifts
    /// (delta / 100) its mood timeline integrates breaks at.</summary>
    internal static IReadOnlyDictionary<string, double[]> BreakShifts(ForecastCoupledContext context) => Cache.GetValue(context, MovesOf).Shifts;

    private static Moves MovesOf(ForecastCoupledContext c)
    {
        // Per priced planned bedroom or barracks with occupants and per stuff it closes in, each occupant's Sleeper at
        // that closure's predicted stage, in its planned bed.
        var deltas = new Dictionary<(int, string), Sleeper?[]>();
        var pawns = c.Needs.Pawns.GroupBy(p => p.Pawn, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var closure in c.Closures)
        {
            if (!c.PlannedRoomRoles.TryGetValue(closure.Room, out var role) || role is not (PlannedRole.Bedroom or PlannedRole.Barracks) || !c.RoomCredits.ContainsKey(role)) continue;
            var barracks = role == PlannedRole.Barracks;
            // The bedroom step's occupants, else the colonists owning a bed in the room: a bedroom's one owner (a standing
            // bedroom's wall upgrade, #3657: its delta is the stage the swap predicts against the stage its owner sleeps at
            // now), every owner of a barracks (#3658).
            var owners = pawns.Values.Where(p => p.OwnedBed.Length != 0 && closure.Prediction.Beds.Contains(p.OwnedBed)).Select(p => p.Pawn).ToArray();
            string[] occupants = c.BedroomOccupants.TryGetValue(closure.Room, out var moved) && moved.Count != 0 ? [.. moved]
                : barracks ? owners : owners is [var owner] ? [owner] : [];
            if (occupants.Length == 0) continue;
            var beds = c.PlannedBeds.GetValueOrDefault(closure.Room) ?? [];
            deltas[(closure.Room, closure.Stuff)] = [.. occupants.Select((occupant, i) => pawns.TryGetValue(occupant, out var pawn)
                ? SleeperOf(pawn, i < beds.Count ? beds[i] : null, closure, barracks, c) : null)];
        }
        var royal = RoyalDeltasOf(c, pawns);
        var gains = new List<(string Pawn, double Delta)>(royal.Values);
        foreach (var sleeper in deltas.Values.SelectMany(d => d).OfType<Sleeper>())
            foreach (var delta in sleeper.Deltas()) gains.Add((sleeper.Pawn.Pawn, delta));
        var shifts = gains.Where(m => m.Delta != 0).GroupBy(m => m.Pawn, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(m => m.Delta / 100).Distinct().Order().ToArray(), StringComparer.Ordinal);
        // A garment's apparel mood delta is priced by the same break days (#3816).
        foreach (var (pawn, apparel) in c.ApparelShifts)
            shifts[pawn] = shifts.TryGetValue(pawn, out var own) ? [.. own.Concat(apparel).Distinct().Order()] : apparel;
        return new(deltas, royal, shifts);
    }

    /// <summary>
    /// <paramref name="pawn"/>'s move into <paramref name="closure"/>'s room (#3840), sleeping in <paramref name="planned"/> (its
    /// planned bed in occupant order), else in the bed it sleeps in now when that is in the room (a standing room's upgrade:
    /// its bed terms are 0). A pawn without a rest need never lies down: none of its bed thoughts change. Null when its room
    /// delta (<see cref="BedroomMoodDelta"/>) or where it sleeps now is unread; throws when a bed term's fact is.
    /// </summary>
    private static Sleeper? SleeperOf(NeedsForecastPawn pawn, SleepingBedFacts? planned, ForecastPlannedClosure closure, bool barracks, ForecastCoupledContext c)
    {
        if (RestRate(pawn) is not { } rate) return new(pawn, 0, 0, null, null) { Sleeps = false };
        if (SleepNow(pawn) is not { } now
            || !BedroomMoodDelta(pawn, Known(closure.Prediction.Stage), c.ObservedThoughts.GetValueOrDefault(pawn.Pawn), barracks).TryGetValue(out var room)) return null;
        var (ground, comfort, gain) = planned is not null ? (planned.Ground, planned.Comfort, rate.GainPerTickPerEffectiveness * planned.RestEffectiveness)
            : now.Read.Bed.Length != 0 && closure.Prediction.Beds.Contains(now.Read.Bed) ? (now.Read.Ground, now.Read.Comfort, now.Read.GainPerTick)
            : throw new RequiredFactException("planned bed of room " + closure.Room.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var mood = room + BedMoodDelta(pawn, now, ground, comfort, c.ComfortCurve) - SleepTemperatureThought(pawn, now.Read.Temperature);
        return new(pawn, mood, RestDaysFreed(rate, now.Read.GainPerTick, gain), Memory(pawn, "slept_in_cold"), Memory(pawn, "slept_in_heat"));
    }
    /// <summary>
    /// ThoughtWorker_BedroomJealous's margin, an inline literal of the game's: another owned room at least this fraction of the
    /// own room's Impressiveness (its absolute value) better makes a Jealous pawn jealous. Kept constant (kept-constants.md).
    /// </summary>
    private const double JealousMargin = 0.1;

    /// <summary>
    /// Per throne room or suite closure with a colonist (<see cref="ForecastCoupledContext.RoyalRooms"/>) whose role is priced,
    /// the mood the room relieves (#3807, #3826, epic #3650 P7), the sum of every thought it changes, as the game applies each:
    /// <list type="bullet">
    /// <item>The title room thoughts: when the closed room's cell count and its predicted Impressiveness, rounded as
    /// RoomRequirement_Impressiveness rounds it, meet the title's requirement, minus the offset of each title room thought the
    /// colonist has now (observed) for that room kind: TitleBedroomRequirementsNotMet and TitleNoPersonalBedroom for a suite,
    /// TitleThroneroomRequirementsNotMet and TitleNoThroneRoom for a throne room, each the game's stage offset for the pawn
    /// (<see cref="NeedsForecastPawn.ThoughtStages"/>). A room short of either requirement relieves none.</item>
    /// <item>In a suite, the Greedy thought (ThoughtWorker_Greedy) at the predicted Impressiveness stage + 1 less the one the
    /// colonist has now.</item>
    /// <item>In a suite, the Jealous thought (ThoughtWorker_BedroomJealous), held when a room another pawn owns
    /// (<see cref="NeedsForecastPawn.RivalRooms"/>) beats the predicted Impressiveness by <see cref="JealousMargin"/> of it,
    /// less the one the colonist has now.</item>
    /// </list>
    /// A thought the colonist cannot get (its rows all 0: no title, no trait) changes nothing, and a colonist with none of them
    /// is 0 without its thoughts read. For a Greedy or Jealous colonist, unread observed thoughts or stage row throws
    /// <see cref="RequiredFactException"/>; for a title thought alone, unread thoughts leave the closure out (unassessed, the
    /// #3807 path #3835 retires).
    /// </summary>
    private static Dictionary<(int, string), (string, double)> RoyalDeltasOf(ForecastCoupledContext c, Dictionary<string, NeedsForecastPawn> pawns)
    {
        var royal = new Dictionary<(int, string), (string, double)>();
        foreach (var closure in c.Closures)
        {
            if (!c.RoyalRooms.TryGetValue(closure.Room, out var need) || !c.PlannedRoomRoles.TryGetValue(closure.Room, out var role)
                || role is not (PlannedRole.Throne or PlannedRole.Suite) || !c.RoomCredits.ContainsKey(role) || !pawns.TryGetValue(need.Pawn, out var pawn)) continue;
            var key = (closure.Room, closure.Stuff);
            string[] roles = role == PlannedRole.Throne ? [TitleThroneUnmetRole, TitleNoThroneRole] : [TitleBedroomUnmetRole, TitleNoBedroomRole];
            var rows = pawn.ThoughtStages;
            bool Gets(string r) => rows.Any(s => s.Role == r && s.Offset != 0);
            var greedy = role == PlannedRole.Suite && Gets(GreedyRole);
            var jealous = role == PlannedRole.Suite && Gets(JealousRole);
            var traits = greedy || jealous;
            if (!traits && !roles.Any(Gets)) { royal[key] = (need.Pawn, 0); continue; }
            // A trait thought's colonist needs its thoughts read (#3650 P10 as revised: no unassessed in new
            // code); the title path's unassessed is #3835's to remove.
            if (!c.ObservedThoughts.GetValueOrDefault(need.Pawn).TryGetValue(out var thoughts))
            {
                if (traits) throw new RequiredFactException("observed thoughts");
                continue;
            }
            string[] Defs(string r) => [.. rows.Where(s => s.Role == r).Select(s => s.Thought).Distinct(StringComparer.Ordinal)];
            double Now(string r) => thoughts.Where(t => Defs(r).Contains(t.Def, StringComparer.Ordinal)).Sum(t => t.Offset);
            var relieved = -rows.Where(r => r.Stage == 0 && roles.Contains(r.Role, StringComparer.Ordinal) && thoughts.Any(t => t.Def == r.Thought))
                .DistinctBy(r => r.Thought).Sum(r => r.Offset);
            if (relieved == 0 && !traits) { royal[key] = (need.Pawn, 0); continue; }
            var impressiveness = closure.Prediction.Impressiveness;
            var delta = closure.Prediction.Cells >= need.MinArea && Math.Round(impressiveness) >= need.MinImpressiveness ? relieved : 0;
            if (greedy)
            {
                var stage = closure.Prediction.Stage;
                var next = Defs(GreedyRole).Select(d => rows.FirstOrDefault(s => s.Thought == d && s.Role == GreedyRole && s.Stage == stage + 1)).ToArray();
                if (next.Any(s => s is null)) throw new RequiredFactException("greedy thought stage");
                delta += next.Sum(s => s!.Offset) - Now(GreedyRole);
            }
            if (jealous)
            {
                var held = pawn.RivalRooms.Any(r => r - impressiveness >= Math.Abs(impressiveness * JealousMargin));
                var next = held ? rows.Where(s => s.Role == JealousRole && s.Stage == 0).DistinctBy(s => s.Thought).Sum(s => s.Offset) : 0;
                delta += next - Now(JealousRole);
            }
            royal[key] = (need.Pawn, delta);
        }
        return royal;
    }

    /// <summary>
    /// The throne room and suite credit (#3807, #3826, on the room credit seam from #3796): the served room's colonist's
    /// <see cref="GainPerDay"/> of the mood the room relieves in its stuff (<see cref="RoyalDeltasOf"/>: its title room thoughts
    /// and, in a suite, its Greedy and Jealous thoughts, summed), under its own pawn id. None for a room serving nobody, or one
    /// that changes none of its colonist's thoughts; null (unassessed) when a title colonist's thoughts or closed room, or the
    /// mood, is unread (a Greedy or Jealous colonist's unread facts throw, <see cref="RoyalDeltasOf"/>).
    /// </summary>
    internal static IReadOnlyList<RoomGain>? RoyalRoomCredit(ForecastRoomService room, ForecastCoupledOutcome outcome, ForecastCoupledContext context, double horizon)
    {
        if (!context.RoyalRooms.ContainsKey(room.Room)) return [];
        if (!Cache.GetValue(context, MovesOf).Royal.TryGetValue((room.Room, room.Stuff), out var move)) return null;
        if (move.Delta == 0) return [];
        return GainPerDay(move.Delta, outcome.Needs.GetValueOrDefault(move.Pawn)?.Mood, context.MoodBreaks, context.Value.WellbeingWeight, horizon / GameTime.TicksPerDay)
            is { } perDay ? [new(move.Pawn, move.Pawn, perDay)] : null;
    }

    /// <summary>
    /// The bedroom and barracks credit (#3655, #3658, on the room credit seam since #3796; every bed term since #3840): per
    /// occupant of the served room in its stuff, the <see cref="GainPerDay"/> of its <see cref="Sleeper"/> mood delta at the
    /// room's sleep temperature (<see cref="PlannedSleepTemperature"/>) plus the capability-days its sleep frees
    /// (<see cref="Sleeper.Rest"/>), under its own pawn id. None without occupants; null (unassessed) when any occupant's room
    /// delta or mood is unknown; throws when the room's temperature or a memory row it needs is unread.
    /// </summary>
    internal static IReadOnlyList<RoomGain>? BedroomCredit(ForecastRoomService room, ForecastCoupledOutcome outcome, ForecastCoupledContext context, double horizon)
    {
        if (!Cache.GetValue(context, MovesOf).Deltas.TryGetValue((room.Room, room.Stuff), out var moves)) return [];
        if (moves.Any(m => m is null)) return null;
        var temperature = moves.Length == 0 ? 0 : PlannedSleepTemperature(room.Room, outcome, context);
        var gains = new List<RoomGain>(moves.Length);
        foreach (var sleeper in moves.OfType<Sleeper>())
        {
            var pawn = sleeper.Pawn.Pawn;
            if (GainPerDay(sleeper.At(temperature), outcome.Needs.GetValueOrDefault(pawn)?.Mood, context.MoodBreaks, context.Value.WellbeingWeight, horizon / GameTime.TicksPerDay)
                is not { } perDay) return null;
            gains.Add(new(pawn, pawn, perDay + sleeper.Rest));
        }
        return gains;
    }

    /// <summary>
    /// The temperature a planned room's sleepers lie at in an outcome (#3840): its shelter's forecast temperature at the horizon
    /// (the shelter plan that completes the room's roof), else the map's outdoor temperature (the open air of a room not
    /// closed by then, or one no shelter plan heats); throws when neither is read.
    /// </summary>
    private static double PlannedSleepTemperature(int room, ForecastCoupledOutcome outcome, ForecastCoupledContext context)
    {
        if (context.Shelters.FirstOrDefault(s => s.PlannedRoom == room) is { } plan
            && outcome.Shelters.FirstOrDefault(s => s.Room == plan.Room) is { } shelter && shelter.TemperatureAtHorizon.TryGetValue(out var held)) return held;
        return context.Thermal?.Outdoors ?? throw new RequiredFactException("sleep temperature of planned room " + room.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}