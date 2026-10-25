using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Brain.Policy;

// The dining and rec room credits (#3803, #3916, epic #3650 P1). Confirmed with ilspycmd on Assembly-CSharp.dll (1.7):
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

/// <summary>Dining and recreation memory investment from observed meal nutrition, shared seats, bounded native joy sessions and the colony cycle's best-source allocation. Served rooms change the combined use profile; each receives only its marginal mood value.</summary>
public static class ForecastRoomThoughts
{
    /// <summary>The <see cref="NeedThoughtStage.Role"/> of AteInImpressiveDiningRoom's rows (Toils_Ingest.FinalizeIngest).</summary>
    public const string DiningRole = "ate_in_dining_room";

    /// <summary>The <see cref="NeedThoughtStage.Role"/> of JoyActivityInImpressiveRecRoom's rows (JoyUtility.TryGainRecRoomThought).</summary>
    public const string RecRole = "joy_in_rec_room";

    /// <summary>The <see cref="NeedThoughtStage.Role"/> of AteWithoutTable's row (Toils_Ingest.FinalizeIngest), which a dining room's tables lift.</summary>
    public const string AteWithoutTableRole = "ate_without_table";

    /// <summary>The named state of a planned dining room with no usable table seat: no meal is taken at it.</summary>
    public const string NoTable = "dining_no_table";

    /// <summary>The named state of a colony with no colonist that has a mood and eats: the dining room seats nobody.</summary>
    public const string NoDiner = "dining_no_diner";

    /// <summary>The named state of a planned rec room whose furniture has no recreation service.</summary>
    public const string NoRecreation = "rec_no_recreation";

    /// <summary>The named state of a colony with no colonist that has a mood and a joy need: the rec room serves nobody.</summary>
    public const string NoRecUser = "rec_no_user";

    /// <summary>The named state of a planned room whose ring closes no room (no closure prediction): no room stage is gained in it.</summary>
    public const string NotClosed = "room_thought_not_closed";

    private sealed record Kind(PlannedRole Planned, string NoRole, string NoUser);
    private static readonly Kind Dining = new(PlannedRole.Dining, NoTable, NoDiner);
    private static readonly Kind Rec = new(PlannedRole.Rec, NoRecreation, NoRecUser);
    /// <summary>The dining room credit (<see cref="RoomCredit"/>): one row per diner whose level the room changes, under the diner.</summary>
    public static IReadOnlyList<RoomGain> DiningCredit(ForecastRoomService room, ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon) =>
        DiningRows.Of(outcome, commitments, context, horizon).GetValueOrDefault(room.Room)?.Gains ?? [];

    /// <summary>The dining room findings (<see cref="RoomFindings"/>): the room's named states and what the credit leaves out.</summary>
    public static IEnumerable<string> DiningFindings(ForecastRoomService room, ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon) =>
        DiningRows.Of(outcome, commitments, context, horizon).GetValueOrDefault(room.Room)?.Findings ?? [];

    /// <summary>The rec room credit (<see cref="RoomCredit"/>): one row per joy user whose level the room changes, under the user.</summary>
    public static IReadOnlyList<RoomGain> RecCredit(ForecastRoomService room, ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon) =>
        RecRows.Of(outcome, commitments, context, horizon).GetValueOrDefault(room.Room)?.Gains ?? [];

    /// <summary>The rec room findings (<see cref="RoomFindings"/>): the room's named states and what the credit leaves out.</summary>
    public static IEnumerable<string> RecFindings(ForecastRoomService room, ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon) =>
        RecRows.Of(outcome, commitments, context, horizon).GetValueOrDefault(room.Room)?.Findings ?? [];

    /// <summary>Periodic native memory groups, including stack limits, group multipliers and the mean fading offset of all live memories.</summary>
    public static double Held(double offset, ThoughtLasting.Memory memory, double perDay)
    {
        DomainException.Require(double.IsFinite(memory.StackedMultiplier)
            && double.IsFinite(memory.DurationTicks) && memory.DurationTicks >= 0, "room memory mechanics");
        var alive = memory.DurationTicks / GameTime.TicksPerDay * perDay;
        if (!(alive > 0)) return 0;
        var whole = Math.Floor(alive);
        var fraction = alive - whole;
        // Over one inter-use period, the oldest memory expires at fraction(D * uses).
        double Segment(double start, double end, double count)
        {
            // TryMergeWithExistingMemory renews an existing group even when its limit is zero or negative.
            count = Math.Min(count, Math.Max(1, memory.StackLimit));
            if (count <= 0 || end <= start) return 0;
            var multiplier = memory.StackedMultiplier;
            var sum = multiplier == 1 ? count : (1 - Math.Pow(multiplier, count)) / (1 - multiplier);
            var mean = memory.Fades ? 1 - ((count - 1) / 2 + (start + end) / 2) / alive : 1;
            return (end - start) * offset * mean * sum;
        }
        return Segment(0, fraction, whole + 1) + Segment(fraction, 1, whole);
    }

    private sealed record User(string Pawn, double Now);

    private sealed record ClosureLevels(string? State);

    private sealed record Levels(User[] Users, IReadOnlyDictionary<(int Room, string Stuff), ClosureLevels> Closures)
    {
        // Extend the existing per-context derivation memo: schedules sharing furnished geometry share one use allocation.
        internal readonly System.Collections.Concurrent.ConcurrentDictionary<EquatableArray<ForecastPlannedClosure>, RoomUseRules.Levels> Profiles = new();
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ForecastCoupledContext, Levels> DiningLevels = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ForecastCoupledContext, Levels> RecLevels = new();

    private static Levels LevelsOf(ForecastCoupledContext context, Kind kind) =>
        (kind == Dining ? DiningLevels : RecLevels).GetValue(context, c => Compute(c, kind));

    // Per context: users' baseline service levels and the services present in each priced closure.
    private static Levels Compute(ForecastCoupledContext c, Kind kind)
    {
        var closures = c.Closures.Where(x => PlannedRoomCredits.Serves(c, x.Room, kind.Planned) && c.RoomCredits.ContainsKey(kind.Planned)).ToArray();
        if (closures.Length == 0) return new([], new Dictionary<(int, string), ClosureLevels>());
        var baseline = RoomUseRules.Of(c, kind == Dining, []);
        var levels = new Dictionary<(int, string), ClosureLevels>();
        foreach (var closure in closures)
        {
            var services = closure.Prediction.Services ?? throw new RequiredFactException("planned room services:" + closure.Room);
            var absent = kind == Dining ? services.Seats.Count == 0 : services.Recreation.Count == 0;
            levels[(closure.Room, closure.Stuff)] = new(absent ? kind.NoRole : null);
        }
        return new([.. baseline.Pawns.Select(p => new User(p.Key, p.Value))], levels);
    }
    private sealed record RoomRows(RoomGain[] Gains, string[] Findings);

    private static readonly PerOutcome<Dictionary<int, RoomRows>> DiningRows = new((o, cs, c, h) => Rows(Dining, o, c, h));
    private static readonly PerOutcome<Dictionary<int, RoomRows>> RecRows = new((o, cs, c, h) => Rows(Rec, o, c, h));

    // Each served room changes the combined use allocation; credit only that change from the preceding allocation.
    private static Dictionary<int, RoomRows> Rows(Kind kind, ForecastCoupledOutcome outcome, ForecastCoupledContext context, double horizon)
    {
        var days = context.InvestmentHorizonDays;
        var served = outcome.ServedRooms
            .Where(s => PlannedRoomCredits.Serves(context, s.Room, kind.Planned) && RingClosure.CreditDays(days, s.ServedAt) > 0)
            .OrderBy(s => s.ServedAt).ThenBy(s => s.Room).ToArray();
        var output = new Dictionary<int, RoomRows>();
        if (served.Length == 0) return output;
        var levels = LevelsOf(context, kind);
        var before = levels.Users.ToDictionary(u => u.Pawn, u => u.Now, StringComparer.Ordinal);
        var active = new List<ForecastPlannedClosure>();
        foreach (var room in served)
        {
            if (PlannedRoomCredits.Prediction(context, room.Room, room.Stuff) is not { } closure) { output[room.Room] = new([], [NotClosed]); continue; }
            var at = levels.Closures[(closure.Room, closure.Stuff)];
            if (at.State is { } state) { output[room.Room] = new([], [state]); continue; }
            if (levels.Users.Length == 0) { output[room.Room] = new([], [kind.NoUser]); continue; }
            active.Add(closure);
            var key = EquatableArray.From(active.OrderBy(r => r.Room).ThenBy(r => r.Stuff, StringComparer.Ordinal));
            var combined = levels.Profiles.GetOrAdd(key, k => RoomUseRules.Of(context, kind == Dining, k));
            var gains = new List<RoomGain>();
            foreach (var user in levels.Users)
            {
                var old = before[user.Pawn] - user.Now;
                var next = combined.Pawns[user.Pawn] - user.Now;
                before[user.Pawn] = combined.Pawns[user.Pawn];
                if (next != old) gains.Add(new(user.Pawn, user.Pawn,
                    PlannedRoomCredits.MoodGain(next, user.Pawn, outcome, context, horizon)
                    - PlannedRoomCredits.MoodGain(old, user.Pawn, outcome, context, horizon)));
            }

            output[room.Room] = new([.. gains], combined.Findings);
        }

        return output;
    }
}
