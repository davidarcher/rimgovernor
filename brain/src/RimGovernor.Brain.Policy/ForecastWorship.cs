using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Brain.Policy;

/// <summary>
/// The Worship role's credit (#3808, epic #3650 P1): the mood a planned worship room gives each believer
/// (<see cref="WorshipFacts.Believers"/>), through <see cref="ForecastHousing.GainPerDay"/>. Two parts, summed:
/// <list type="bullet">
/// <item>Each building precept's presence demand that applies (Precept_Building.SituationThoughtsToAdd): the believer's
/// thought now (IdeoBuildingMissing without the building, IdeoBuildingDisrespected while no room of it meets every
/// requirement, none otherwise) is replaced by none when the planned room is predicted to meet every requirement
/// (<see cref="Meets"/>) or a standing one already does, by Disrespected otherwise.</item>
/// <item>Each ritual the room lets the colony hold (<see cref="WorshipFacts.Rituals"/>): the whole expected outcome memory
/// at the room's quality (<see cref="Quality"/>), held every IntervalDaysMin days (RitualRules.RitualDue's free start), so
/// a day earns the expected mood × min(duration, interval) / interval.</item>
/// </list>
/// No ideoligion or no believer earns nothing. A demand whose requirement the plan cannot predict, and a ritual whose outcome
/// effect is not drawn from quality, are mechanics the credit leaves out (#3835): they add nothing, and are named
/// (<see cref="Unmodelled"/>).
/// </summary>
internal static class ForecastWorship
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ForecastCoupledContext, Dictionary<(int Room, string Stuff), double>> Cache = new();

    /// <summary>The finding of an applying demand's requirement the plan cannot predict: <c>worship_requirement_unmodelled:{requirement}</c>.</summary>
    public const string RequirementUnmodelled = "worship_requirement_unmodelled:";

    /// <summary>The finding of a ritual whose outcome effect is not drawn from quality: <c>ritual_outcome_unmodelled:{worker}</c>.</summary>
    public const string RitualUnmodelled = "ritual_outcome_unmodelled:";

    /// <summary>
    /// The worship room's gains per believer (<see cref="RoomCredit"/>), in the prediction the room is served in
    /// (<see cref="PlannedRoomCredits.Prediction"/>). None without a believer or an owed worship room. Throws
    /// <see cref="RequiredFactException"/> (#3835) when the room's prediction or layout room, the filth model, the demand
    /// moods or a believer's mood is unread.
    /// </summary>
    internal static IReadOnlyList<RoomGain> WorshipCredit(ForecastRoomService room, ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon)
    {
        var worship = context.Worship;
        if (worship.Believers.Count == 0 || worship.Need is null) return [];
        if (PlannedRoomCredits.Prediction(context, room.Room, room.Stuff) is not { Stuff: var stuff }) return [];
        if (!Deltas(context).TryGetValue((room.Room, stuff), out var delta) || delta == 0) return [];
        return [.. worship.Believers.Select(b => new RoomGain(b.Value, b.Value, PlannedRoomCredits.MoodGain(delta, b.Value, outcome, context, horizon)))];
    }

    /// <summary>The worship mechanics the credit leaves out (<see cref="PlannedRoomCredits.Unmodelled"/>), named per served room.</summary>
    internal static IEnumerable<string> Unmodelled(ForecastRoomService room, ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon)
    {
        var worship = context.Worship;
        if (worship.Believers.Count == 0 || worship.Need is null) return [];
        return worship.Demands.Where(d => d.Applies).SelectMany(d => d.Unread).Select(r => RequirementUnmodelled + r)
            .Concat(worship.Rituals.Select(r => r.Outcome?.Unread ?? "").Where(u => u.Length != 0).Select(u => RitualUnmodelled + u))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
    }

    /// <summary>
    /// Per priced planned worship room and per stuff it closes in, each believer's mood delta (<see cref="Delta"/>). Cached per context.
    /// </summary>
    internal static IReadOnlyDictionary<(int Room, string Stuff), double> Deltas(ForecastCoupledContext context) => Cache.GetValue(context, DeltasOf);

    /// <summary>Per believer, the distinct nonzero worship shifts (delta / 100) its mood timeline integrates breaks at.</summary>
    internal static IReadOnlyDictionary<string, double[]> Shifts(ForecastCoupledContext context)
    {
        var shifts = Deltas(context).Values.Where(v => v != 0).Select(v => v / 100).Distinct().Order().ToArray();
        return shifts.Length == 0 ? new Dictionary<string, double[]>()
            : context.Worship.Believers.ToDictionary(b => b.Value, _ => shifts, StringComparer.Ordinal);
    }

    private static Dictionary<(int Room, string Stuff), double> DeltasOf(ForecastCoupledContext c)
    {
        var output = new Dictionary<(int, string), double>();
        if (c.Worship.Believers.Count == 0 || c.Worship.Need is null || !c.RoomCredits.ContainsKey(PlannedRole.Worship)) return output;
        foreach (var closure in c.Closures)
        {
            if (!c.PlannedRoomRoles.TryGetValue(closure.Room, out var role) || role != PlannedRole.Worship) continue;
            var planned = c.LayoutRooms.TryGetValue(closure.Room, out var layout) ? layout
                : throw new RequiredFactException("planned_room_layout:" + closure.Room.ToString(System.Globalization.CultureInfo.InvariantCulture));
            output[(closure.Room, closure.Stuff)] = Delta(closure.Prediction, planned, c.Worship, c.Filth);
        }
        return output;
    }

    /// <summary>
    /// A believer's mood delta from the worship room <paramref name="prediction"/> predicts (see <see cref="ForecastWorship"/>);
    /// 0 without an owed worship room. A demand or ritual the credit leaves out (<see cref="Unmodelled"/>) adds nothing. Throws
    /// when the prediction, the demand moods or the filth model is unread.
    /// </summary>
    internal static double Delta(PlannedRoomPrediction prediction, PlannedRoom planned, WorshipFacts worship, FilthModel filth)
    {
        if (worship.Need is not { } need) return 0;
        double delta = 0;
        foreach (var demand in worship.Demands.Where(d => d.Applies && d.Unread.Count == 0))
        {
            var moods = worship.Moods ?? throw new RequiredFactException("ideo building demand moods");
            var now = !demand.Present ? moods.Missing : !demand.Satisfied ? moods.Disrespected : 0;
            // A building already standing in a room that meets the demand keeps meeting it.
            var after = demand.Satisfied || Meets(demand, prediction, need) ? 0 : moods.Disrespected;
            delta += after - now;
        }
        var rituals = worship.Rituals.Where(r => r.Outcome is { Unread.Length: 0 }).ToArray();
        if (rituals.Length > 0)
        {
            var cleanliness = PlannedRoomCleanlinessRules.Steady(prediction, planned.FilthRuns.Values, filth).Dirty;
            foreach (var ritual in rituals)
            {
                var model = ritual.Outcome!;
                var quality = Quality(model, worship.Believers.Count, prediction.Impressiveness, cleanliness);
                delta += ExpectedMood(model, quality, ritual.IntervalDaysMin);
            }
        }
        return delta;
    }

    /// <summary>
    /// Whether the planned room meets every requirement of <paramref name="demand"/> (RoomRequirement_*.Met): its cells reach
    /// the area, its rounded impressiveness the impressiveness, every terrain it is floored with carries a demanded tag, and
    /// the room's furniture (<paramref name="need"/>, which the reconcile places and clears forbidden buildings beside) holds
    /// every counted thing and no forbidden building.
    /// </summary>
    internal static bool Meets(BuildingDemand demand, PlannedRoomPrediction prediction, ChildRoomNeed need)
    {
        var furniture = need.Furniture.Items.SelectMany(f => f.Defs.Items.Select(d => (Def: d, f.Count))).ToList();
        return prediction.Cells >= demand.MinArea
            && Math.Round(prediction.Impressiveness) >= demand.MinImpressiveness
            && (demand.FloorTags.Count == 0 || (prediction.Terrains.Count > 0 && prediction.Terrains.All(t => t.Tags.Any(demand.FloorTags.Contains))))
            && demand.Counts.All(c => furniture.Any(f => f.Def == c.Def && f.Count >= c.Count))
            && !furniture.Any(f => demand.ForbiddenDefs.Contains(f.Def));
    }

    /// <summary>
    /// The ritual's quality held in the room with every believer attending, at full progress
    /// (RitualOutcomeEffectWorker_FromQuality.GetQuality): startingQuality plus each comp's curve at its input (the
    /// participant count capped at the curve's last x; the room stat capped there and rounded to 0.1), clamped.
    /// </summary>
    internal static double Quality(RitualOutcomeModel model, int participants, double impressiveness, double cleanliness)
    {
        var quality = model.StartingQuality;
        foreach (var comp in model.Comps)
        {
            if (comp.Curve.Count == 0) continue;
            var last = comp.Curve[^1].X;
            var x = comp.Input == RitualQualityInput.Participants ? Math.Floor(Math.Min(participants, last))
                : Math.Round(Math.Min(comp.Input == RitualQualityInput.Impressiveness ? impressiveness : cleanliness, last) * 10) / 10;
            quality += LayoutFilthRules.EvaluateCurve(comp.Curve, x);
        }
        return Math.Clamp(quality, model.MinQuality, Math.Max(model.MinQuality, model.MaxQuality));
    }

    /// <summary>
    /// The expected memory mood a day of a ritual held every <paramref name="intervalDays"/> at <paramref name="quality"/>: each
    /// outcome drawn with weight chance × quality (positive) or chance, its memory's mood over min(duration, interval) / interval.
    /// </summary>
    internal static double ExpectedMood(RitualOutcomeModel model, double quality, double intervalDays)
    {
        var weights = model.Chances.Select(o => o.Positive ? Math.Max(o.Chance * quality, 0) : o.Chance).ToArray();
        var total = weights.Sum();
        if (!(total > 0) || !(intervalDays > 0)) return 0;
        return model.Chances.Select((o, i) => weights[i] / total * o.Mood * Math.Min(o.DurationDays, intervalDays) / intervalDays).Sum();
    }
}
