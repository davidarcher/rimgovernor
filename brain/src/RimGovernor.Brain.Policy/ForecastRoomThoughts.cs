using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Brain.Policy;

// The dining and rec room credits (#3803, epic #3650 P1). Confirmed with ilspycmd on Assembly-CSharp.dll (1.6):
// - Toils_Ingest.FinalizeIngest: a pawn with a mood need finishing a nutritious ingestible whose chairSearchRadius > 10 gains
//   AteWithoutTable when it ate standing with no eat surface ahead and the food wants a table (not a wild man), then
//   AteInImpressiveDiningRoom at RoomStatDefOf.Impressiveness.GetScoreStageIndex of the room it ate in, unless that stage is
//   null. The room's role is not read: the thought follows the room a pawn eats in.
// - JoyUtility.TryGainRecRoomThought, which the joy drivers call as a joy job ends (PlayBilliards, PlayGoldenCube, Reading,
//   SitFacingBuilding, SocialRelax, VisitJoyThing, WatchBuilding), gains JoyActivityInImpressiveRecRoom the same way at the
//   stage of the pawn's room.
// - Thought_Memory.TryMergeWithExistingMemory renews the held memory once its group is at stackLimit; a memory of another
//   stage is another group (Thought.GroupsWith without stagesStack), held beside it until it expires.
// - RoomRoleWorker_DiningRoom scores 12 per eat surface, RoomRoleWorker_RecRoom 7 per countsForRecRoom joy building: a planned
//   room whose furniture scores neither is not that role.

/// <summary>
/// The Dining and Rec room credits (#3803): the impressive-room memory a planned dining room (AteInImpressiveDiningRoom, at its
/// meals) or rec room (JoyActivityInImpressiveRecRoom, at its joy activities) gives each colonist with a mood, at the room's
/// predicted Impressiveness stage. A user's level in a room is the mood the memory holds at its uses a day (a meal or joy
/// activity per refill of the need, <see cref="UsesPerDay"/>; <see cref="Held"/>); its level now is the offset of the memory it
/// holds now (observed), and for dining, of AteWithoutTable too, which a room's tables lift. The served rooms of a role take the
/// users in service order, each crediting its level less the one before (the user's level now, then the earlier room's), so
/// the sum over the horizon is each room's level from the day it serves. Each nonzero delta is priced through
/// <see cref="PlannedRoomCredits.MoodGain"/>, under the user. The thought follows the room a pawn eats or relaxes in, never its
/// role, so <c>LayoutMoodRules.MoodJudgment.RoleThoughts</c> keeps no row for it: no reader would read one.
/// </summary>
public static class ForecastRoomThoughts
{
    /// <summary>The <see cref="NeedThoughtStage.Role"/> of AteInImpressiveDiningRoom's rows (Toils_Ingest.FinalizeIngest).</summary>
    public const string DiningRole = "ate_in_dining_room";

    /// <summary>The <see cref="NeedThoughtStage.Role"/> of JoyActivityInImpressiveRecRoom's rows (JoyUtility.TryGainRecRoomThought).</summary>
    public const string RecRole = "joy_in_rec_room";

    /// <summary>The <see cref="NeedThoughtStage.Role"/> of AteWithoutTable's row (Toils_Ingest.FinalizeIngest), which a dining room's tables lift.</summary>
    public const string AteWithoutTableRole = "ate_without_table";

    /// <summary>The named state of a planned dining room whose furniture has no eat surface (its predicted role is not DiningRoom): no meal is taken at it.</summary>
    public const string NoTable = "dining_no_table";

    /// <summary>The named state of a colony with no colonist that has a mood and eats: the dining room seats nobody.</summary>
    public const string NoDiner = "dining_no_diner";

    /// <summary>The named state of a planned rec room whose furniture has no recreation building (its predicted role is not RecRoom).</summary>
    public const string NoRecreation = "rec_no_recreation";

    /// <summary>The named state of a colony with no colonist that has a mood and a joy need: the rec room serves nobody.</summary>
    public const string NoRecUser = "rec_no_user";

    /// <summary>The named state of a planned room whose ring closes no room (no closure prediction): no room stage is gained in it.</summary>
    public const string NotClosed = "room_thought_not_closed";

    /// <summary>The not-modelled finding of a room memory whose def stacks more than once (stackLimit other than 1): the room earns nothing.</summary>
    public const string Stacked = "room_thought_stacked";

    /// <summary>The mechanics the dining credit leaves out, named with each served dining room.</summary>
    public static readonly EquatableArray<string> DiningNotModelled =
    [
        "dining_table_reach", // every meal is taken at the room's tables (the nearest table within the food's chairSearchRadius, a free seat)
        "dining_meal_size", // one meal per refill of the food need, from its hungry threshold to full
        "room_thought_level_now", // the room memory a colonist holds now stands for the one it keeps without the room
    ];

    /// <summary>The mechanics the rec credit leaves out, named with each served rec room.</summary>
    public static readonly EquatableArray<string> RecNotModelled =
    [
        "rec_joy_kind", // every joy activity is taken at the room's buildings (none walking, skygazing or elsewhere)
        "rec_activity_length", // one joy activity per refill of the joy need, from its threshold to full: a lower bound
        "room_thought_level_now",
    ];

    private sealed record Kind(PlannedRole Planned, RoomRole Role, string Need, string Thought, string Without, string NoRole, string NoUser, EquatableArray<string> NotModelled);

    private static readonly Kind Dining = new(PlannedRole.Dining, RoomRole.DiningRoom, "food", DiningRole, AteWithoutTableRole, NoTable, NoDiner, DiningNotModelled);
    private static readonly Kind Rec = new(PlannedRole.Rec, RoomRole.RecRoom, "joy", RecRole, "", NoRecreation, NoRecUser, RecNotModelled);

    /// <summary>The dining room credit (<see cref="RoomCredit"/>): one row per diner whose level the room changes, under the diner.</summary>
    public static IReadOnlyList<RoomGain> DiningCredit(ForecastRoomService room, ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon) =>
        DiningRows.Of(outcome, commitments, context, horizon).GetValueOrDefault(room.Room)?.Gains ?? [];

    /// <summary>The dining room findings (<see cref="RoomFindings"/>): the room's named states and what the credit leaves out.</summary>
    public static IEnumerable<string> DiningFindings(ForecastRoomService room, ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon) =>
        DiningRows.Of(outcome, commitments, context, horizon).GetValueOrDefault(room.Room)?.Findings ?? [.. DiningNotModelled];

    /// <summary>The rec room credit (<see cref="RoomCredit"/>): one row per joy user whose level the room changes, under the user.</summary>
    public static IReadOnlyList<RoomGain> RecCredit(ForecastRoomService room, ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon) =>
        RecRows.Of(outcome, commitments, context, horizon).GetValueOrDefault(room.Room)?.Gains ?? [];

    /// <summary>The rec room findings (<see cref="RoomFindings"/>): the room's named states and what the credit leaves out.</summary>
    public static IEnumerable<string> RecFindings(ForecastRoomService room, ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon) =>
        RecRows.Of(outcome, commitments, context, horizon).GetValueOrDefault(room.Room)?.Findings ?? [.. RecNotModelled];

    /// <summary>
    /// The uses a day of a need (#3803): its fall a day over the refill one use gives, from its threshold (food: hungry, joy: its
    /// second threshold) to full. A need that never falls has none.
    /// </summary>
    public static double UsesPerDay(NeedRate need)
    {
        DomainException.Require(double.IsFinite(need.FallPerTick) && need.FallPerTick >= 0 && need.Threshold is >= 0 and < 1, "need " + need.Kind + " rate");
        return need.FallPerTick * GameTime.TicksPerDay / (1 - need.Threshold);
    }

    /// <summary>
    /// The mood a memory of <paramref name="offset"/> holds while it is gained <paramref name="perDay"/> times a day, each gain
    /// renewing it (stackLimit 1): a memory lasting <paramref name="durationDays"/> is held min(1, D × perDay) of the time at full;
    /// one that fades (lerpMoodToZero) averages 1 − 1/(2 D perDay) of the offset between gains when renewed before it expires,
    /// else D × perDay / 2.
    /// </summary>
    public static double Held(double offset, double durationDays, bool fades, double perDay)
    {
        if (!(perDay > 0) || !(durationDays > 0)) return 0;
        if (!fades) return new MemoryThought(offset, durationDays, 1, 1).Mood(perDay);
        var held = durationDays * perDay;
        return offset * (held >= 1 ? 1 - 1 / (2 * held) : held / 2);
    }

    /// <summary>
    /// Per pawn, the shifts (delta / 100) the dining and rec credits price (<see cref="PlannedRoomCredits.BreakShifts"/>): each
    /// closure's level less the user's level now, and less its level in each other room of the role, the orders the served rooms
    /// can take it in.
    /// </summary>
    internal static IReadOnlyDictionary<string, double[]> Shifts(ForecastCoupledContext context)
    {
        var shifts = new Dictionary<string, SortedSet<double>>(StringComparer.Ordinal);
        foreach (var kind in new[] { Dining, Rec })
        {
            var levels = LevelsOf(context, kind);
            foreach (var user in levels.Users)
            {
                var at = levels.Closures.Where(c => c.Value.State is null).Select(c => (c.Key.Room, Level: c.Value.Levels[user.Pawn])).ToArray();
                foreach (var (room, level) in at)
                    foreach (var before in at.Where(o => o.Room != room).Select(o => o.Level).Append(user.Now))
                        if (level != before)
                        {
                            if (!shifts.TryGetValue(user.Pawn, out var set)) shifts[user.Pawn] = set = [];
                            set.Add((level - before) / 100);
                        }
            }
        }

        return shifts.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal);
    }

    private sealed record User(string Pawn, double Now);

    private sealed record ClosureLevels(string? State, IReadOnlyDictionary<string, double> Levels);

    private sealed record Levels(User[] Users, IReadOnlyDictionary<(int Room, string Stuff), ClosureLevels> Closures);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ForecastCoupledContext, Levels> DiningLevels = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ForecastCoupledContext, Levels> RecLevels = new();

    private static Levels LevelsOf(ForecastCoupledContext context, Kind kind) =>
        (kind == Dining ? DiningLevels : RecLevels).GetValue(context, c => Compute(c, kind));

    // Per context: the users, with their level now, and each priced closure of the role's level per user.
    private static Levels Compute(ForecastCoupledContext c, Kind kind)
    {
        var closures = c.Closures.Where(x => c.PlannedRoomRoles.GetValueOrDefault(x.Room) == kind.Planned && c.RoomCredits.ContainsKey(kind.Planned)).ToArray();
        if (closures.Length == 0) return new([], new Dictionary<(int, string), ClosureLevels>());
        var users = new List<(NeedsForecastPawn Pawn, double Uses, User User)>();
        foreach (var pawn in c.Needs.Pawns.DistinctBy(p => p.Pawn))
        {
            if (pawn.Mood is null || pawn.Needs.FirstOrDefault(n => n.Kind == kind.Need) is not { } need) continue;
            var uses = UsesPerDay(need);
            if (uses <= 0) continue;
            var thoughts = c.ColonistThoughts.TryGetValue(pawn.Pawn, out var read) ? read : throw new RequiredFactException("observed thoughts:" + pawn.Pawn);
            var now = HeldNow(pawn, kind.Thought, thoughts);
            if (kind.Without.Length != 0) now += HeldNow(pawn, kind.Without, thoughts);
            users.Add((pawn, uses, new(pawn.Pawn, now)));
        }

        var levels = new Dictionary<(int, string), ClosureLevels>();
        foreach (var closure in closures)
        {
            var key = (closure.Room, closure.Stuff);
            if (!Wire.TryParse<RoomRole>(closure.Prediction.Role, out var role) || role != kind.Role) { levels[key] = new(kind.NoRole, new Dictionary<string, double>()); continue; }
            var at = new Dictionary<string, double>(StringComparer.Ordinal);
            string? state = null;
            foreach (var (pawn, uses, user) in users)
            {
                var rows = pawn.ThoughtStages.Where(r => r.Role == kind.Thought && r.Stage == closure.Prediction.Stage).DistinctBy(r => r.Thought).ToArray();
                if (rows.Length == 0) throw new RequiredFactException(kind.Thought + " thought stage " + closure.Prediction.Stage.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + pawn.Pawn);
                var level = 0d;
                foreach (var row in rows)
                {
                    if (row.Lasting is not ThoughtLasting.Memory memory) throw new RequiredFactException(kind.Thought + " thought memory:" + pawn.Pawn);
                    if (memory.StackLimit != 1) { state = Stacked; break; }
                    level += Held(row.Offset, memory.DurationTicks / GameTime.TicksPerDay, memory.Fades, uses);
                }

                if (state is not null) break;
                at[user.Pawn] = level;
            }

            levels[key] = state is null ? new(null, at) : new(state, new Dictionary<string, double>());
        }

        return new([.. users.Select(u => u.User)], levels);
    }

    /// <summary>The offset now of the memories of the role's defs <paramref name="pawn"/> holds (observed); its defs unread (no stage row) throws.</summary>
    private static double HeldNow(NeedsForecastPawn pawn, string role, EquatableArray<MoodThought> thoughts)
    {
        var defs = pawn.ThoughtStages.Where(r => r.Role == role).Select(r => r.Thought).ToHashSet(StringComparer.Ordinal);
        if (defs.Count == 0) throw new RequiredFactException(role + " thought:" + pawn.Pawn);
        return thoughts.Where(t => defs.Contains(t.Def)).Sum(t => t.Offset);
    }

    private sealed record RoomRows(RoomGain[] Gains, string[] Findings);

    private static readonly PerOutcome<Dictionary<int, RoomRows>> DiningRows = new((o, cs, c, h) => Rows(Dining, o, c, h));
    private static readonly PerOutcome<Dictionary<int, RoomRows>> RecRows = new((o, cs, c, h) => Rows(Rec, o, c, h));

    // Every served room of the role in the outcome, in service order: each takes every user from its level before.
    private static Dictionary<int, RoomRows> Rows(Kind kind, ForecastCoupledOutcome outcome, ForecastCoupledContext context, double horizon)
    {
        var days = context.InvestmentHorizonDays;
        var served = outcome.ServedRooms
            .Where(s => context.PlannedRoomRoles.GetValueOrDefault(s.Room) == kind.Planned && RingClosure.CreditDays(days, s.ServedAt) > 0)
            .OrderBy(s => s.ServedAt).ThenBy(s => s.Room).ToArray();
        var output = new Dictionary<int, RoomRows>();
        if (served.Length == 0) return output;
        var levels = LevelsOf(context, kind);
        var before = levels.Users.ToDictionary(u => u.Pawn, u => u.Now, StringComparer.Ordinal);
        foreach (var room in served)
        {
            if (PlannedRoomCredits.Prediction(context, room.Room, room.Stuff) is not { } closure) { output[room.Room] = new([], [.. kind.NotModelled, NotClosed]); continue; }
            var at = levels.Closures[(closure.Room, closure.Stuff)];
            if (at.State is { } state) { output[room.Room] = new([], [.. kind.NotModelled, state]); continue; }
            if (levels.Users.Length == 0) { output[room.Room] = new([], [.. kind.NotModelled, kind.NoUser]); continue; }
            var gains = new List<RoomGain>();
            foreach (var user in levels.Users)
            {
                var delta = at.Levels[user.Pawn] - before[user.Pawn];
                before[user.Pawn] = at.Levels[user.Pawn];
                if (delta != 0) gains.Add(new(user.Pawn, user.Pawn, PlannedRoomCredits.MoodGain(delta, user.Pawn, outcome, context, horizon)));
            }

            output[room.Room] = new([.. gains], [.. kind.NotModelled]);
        }

        return output;
    }
}
