using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

// The classroom credit (#3809, epic #3650 P1, D2). Confirmed with ilspycmd on Assembly-CSharp.dll (1.7):
// - Need_Learning.NeedInterval lowers a child's learning need BaseFallPerInterval (BaseFallPerTick a tick) asleep or awake.
//   JobGiver_Learn starts a learning job below LearningUtility.StartJobMaxLearning, trying the pawn's active desires in random
//   order and taking the first whose LearningGiver.CanDo holds; LearningUtility.LearningTickCheckEnd refills the need
//   NeedSatisfiedPerTick × LearningRateFactor a tick and ends the job once it is satisfied. Every job ends at the same need, so
//   each job a desire wins is the same refill: a desire's share of the refill is its share of the jobs.
// - Pawn_LearningTracker keeps at most two active desires (AddNewLearningDesire's literal 2): every 20000–40000 ticks it draws,
//   by selectionWeight, a def not active whose worker CanGiveDesire, drops the oldest and appends it.
// - LearningGiver_Lessontaking.CanDo needs SchoolUtility.FindTeacher and a free ThingDefOf.SchoolDesk: without a desk the other
//   active desire wins every job, with one each wins half (the random order).
// - JobDriver_Lessongiving teaches the student Lessontaking.xpPerTick × LearningRateFactor a tick in a skill ChooseSkill draws at
//   random from the teacher's top TopTeachableSkillCount, through Pawn_SkillTracker.Learn. LearningRateFactor is the child's
//   LearningRateFactor stat × SchoolDeskLearningRate, so a lesson's XP per unit of refill is xpPerTick / NeedSatisfiedPerTick:
//   the factor cancels, and only the teacher's ticks change with it.
// - Growth points (NeedInterval) follow the need's level, which the other desires refill without a classroom: no growth term (D3).

/// <summary>
/// The Classroom room credit (#3809, D2): the lessons a planned classroom seats. A child with a learning need and no school desk
/// takes no lesson; the room's planned desks each seat one, over the outcome's served classrooms in service order. A seated
/// child's lessons are <see cref="LessonShare"/> of its learning refill, which teaches it <c>share × fall × xpPerTick / refill</c>
/// raw XP a day, split evenly over the skills its teacher teaches; each skill's XP is projected through the skill arithmetic
/// (<see cref="ForecastSkillPolicy.ForecastSkill"/>) and priced at the stock's value per XP (<c>Investment[skill/child/skill]</c>,
/// the path #3852 gives practice sessions; a skill without a marginal adds nothing). The teacher's lesson ticks are charged as
/// lost work: the shortfall rows they would fill (<see cref="PawnValueRules.WorkAdded"/>, against the outcome's shortfall).
/// </summary>
public static class ForecastClassroom
{
    /// <summary>The named state of a colony with no child with a learning need: the room seats nobody.</summary>
    public const string NoChild = "classroom_no_child";

    /// <summary>The named state of a colony no colonist of which can teach (<see cref="SchoolRead.Teacher"/> ""): no lesson is given.</summary>
    public const string NoTeacher = "classroom_no_teacher";

    /// <summary>The named state of a colony whose Lessontaking desire no child is ever given (not loaded, or its worker refuses): no lesson.</summary>
    public const string NoLesson = "classroom_no_lesson";

    /// <summary>The named state of a colony whose standing desks already seat every child taught: the room seats nobody more.</summary>
    public const string Seated = "classroom_children_seated";

    /// <summary>The named state of a child its teacher teaches no skill (ChooseSkill finds none): its lesson ends before it starts.</summary>
    public const string NothingTaught = "classroom_nothing_taught";

    /// <summary>The named state of a served classroom with no planned desk: it seats nobody.</summary>
    public const string NoDesk = "classroom_no_desk";

    /// <summary>The named state of a taught skill tier 2 gives no value per XP (no Investment marginal): its lessons add nothing.</summary>
    public const string SkillUnvalued = "classroom_skill_unvalued";

    /// <summary>The active learning desires a child keeps (Pawn_LearningTracker.AddNewLearningDesire's literal).</summary>
    internal const int ActiveDesires = 2;

    /// <summary>The mechanics the credit leaves out, named with each served classroom.</summary>
    public static readonly EquatableArray<string> NotModelled =
    [
        "classroom_displaced_desire_xp", // the XP of the desire a lesson displaces (Workwatching, Reading) is not charged
        "classroom_teacher_social_xp", // the teacher's Social XP a lesson gives
        "classroom_teacher_availability", // the teacher held free whenever a child wants a lesson (FindTeacher's passing clauses)
        "classroom_desk_sharing", // a desk seats one child, never several by turns
        "classroom_blackboard_link_limit", // a blackboard's maxSimultaneous desks
        "classroom_lesson_memories", // the GaveLesson and WasTaught memories
    ];

    /// <summary>The credit (<see cref="RoomCredit"/>): one row per child the room seats, under the child.</summary>
    public static IReadOnlyList<RoomGain> ClassroomCredit(ForecastRoomService room, ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon) =>
        Of(outcome, commitments, context, horizon).GetValueOrDefault(room.Room)?.Gains ?? [];

    /// <summary>The findings (<see cref="RoomFindings"/>): the room's named states and what the credit leaves out.</summary>
    public static IEnumerable<string> Findings(ForecastRoomService room, ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon) =>
        Of(outcome, commitments, context, horizon).GetValueOrDefault(room.Room)?.Findings ?? [.. NotModelled];

    /// <summary>
    /// The share of a seated child's learning refill its lessons take (#3809, D2): the stationary chance that
    /// <see cref="SchoolRead.Lessontaking"/> is one of its active desires times the half of the jobs it then wins. The desires are
    /// the chain <see cref="SchoolRead.Desires"/> the tracker draws (every <see cref="ActiveDesires"/> pair of givable defs; the
    /// new one by selectionWeight among those not active, the oldest dropped). 0 when Lessontaking cannot be given; a colony
    /// with no more givable defs than <see cref="ActiveDesires"/> keeps them all.
    /// </summary>
    public static double LessonShare(SchoolRead read) => Shares.GetValue(read, r => new(ShareOf(r))).Value;

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SchoolRead, System.Runtime.CompilerServices.StrongBox<double>> Shares = new();

    private static double ShareOf(SchoolRead read)
    {
        var givable = read.Desires.Where(d => d.CanGive).ToArray();
        DomainException.Require(givable.All(d => double.IsFinite(d.SelectionWeight) && d.SelectionWeight > 0), "learning desire weights");
        if (!givable.Any(d => d.Def == read.Lessontaking)) return 0;
        if (givable.Length <= ActiveDesires) return 1d / givable.Length;
        // The ordered pairs (older, newer); a step drops the older and draws the next among the defs not active.
        var states = Enumerable.Range(0, givable.Length).SelectMany(i => Enumerable.Range(0, givable.Length).Select(j => (i, j))).Where(e => e.i != e.j).ToArray();
        var index = states.Select((s, k) => (s, k)).ToDictionary(e => e.s, e => e.k);
        var law = Enumerable.Repeat(1d / states.Length, states.Length).ToArray();
        for (var step = 0; step < 10000; step++)
        {
            // The lazy chain (half stays) has the same stationary law and no period.
            var next = law.Select(x => x / 2).ToArray();
            for (var k = 0; k < states.Length; k++)
            {
                var (a, b) = states[k];
                var total = givable.Where((_, c) => c != a && c != b).Sum(d => d.SelectionWeight);
                for (var c = 0; c < givable.Length; c++)
                    if (c != a && c != b) next[index[(b, c)]] += law[k] / 2 * givable[c].SelectionWeight / total;
            }

            var moved = next.Zip(law, (x, y) => Math.Abs(x - y)).Max();
            law = next;
            if (moved < 1e-15) break;
        }

        var lesson = Array.FindIndex(givable, d => d.Def == read.Lessontaking);
        return states.Select((s, k) => s.i == lesson || s.j == lesson ? law[k] : 0).Sum() / ActiveDesires;
    }

    /// <summary>
    /// The raw lesson XP a day a seated child is taught (#3809): <paramref name="share"/> of its refill a day (the learning need's
    /// fall, which every day's jobs refill) at the lesson's XP per unit of refill.
    /// </summary>
    public static double LessonXpPerDay(SchoolRead read, double share)
    {
        DomainException.Require(read.RefillPerTick > 0 && read.FallPerTick >= 0 && read.LessonXpPerTick >= 0, "learning rates");
        return share * read.FallPerTick * GameTime.TicksPerDay * read.LessonXpPerTick / read.RefillPerTick;
    }

    /// <summary>
    /// The teacher's lesson ticks a day for a seated child (#3809): the child's lesson refill a day over the refill a tick at its
    /// <paramref name="learningRate"/> (its LearningRateFactor stat × the desk's SchoolDeskLearningRate).
    /// </summary>
    public static double LessonTicksPerDay(SchoolRead read, double share, double learningRate)
    {
        DomainException.Require(read.RefillPerTick > 0 && learningRate > 0, "lesson refill rate");
        return share * read.FallPerTick * GameTime.TicksPerDay / (read.RefillPerTick * learningRate);
    }

    /// <summary>
    /// The XP a day <paramref name="rawPerDay"/> of lessons applies to the child's skill (#3809): spread over the read's validity
    /// (at most a day, as practice sessions are, <see cref="ForecastMaintenancePolicy.SessionXp"/>) through the skill arithmetic,
    /// scaled to a day. A read valid for no tick, or a projection the arithmetic cannot make, throws <see cref="RequiredFactException"/>.
    /// </summary>
    public static double AppliedXpPerDay(string child, TaughtSkillRead skill, double rawPerDay)
    {
        var fact = "lesson xp " + ProjectGraph.SkillKey(child, skill.Skill);
        var span = Math.Min(GameTime.TicksPerDay, skill.Learning.ValidForTicks - 1);
        if (span < 1) throw new RequiredFactException(fact + ": learning read valid for no tick");
        var count = Math.Min(24, span);
        var interval = span / count;
        var forecast = ForecastSkillPolicy.ForecastSkill(skill.Learning,
            [new SkillXpEvent(fact, interval, rawPerDay * span / GameTime.TicksPerDay / count, SkillXpPath.NormalTracker, Count: count, Interval: interval)], span);
        return forecast is SkillForecast.Projected { Projection.AppliedGain: var xp } ? xp * GameTime.TicksPerDay / span
            : throw new RequiredFactException(fact + ": " + string.Join(", ", ForecastSkillPolicy.FindingsOf(forecast)));
    }

    private sealed record RoomRows(RoomGain[] Gains, string[] Findings);

    private sealed record Cached(ForecastCoupledContext Context, string Chosen, double Horizon, IReadOnlyDictionary<int, RoomRows> Rooms);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ForecastCoupledOutcome, Cached> Cache = new();

    // Every served classroom's rows of the outcome, cached per outcome for its context, chosen roots and horizon.
    private static IReadOnlyDictionary<int, RoomRows> Of(ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon)
    {
        var chosen = string.Join('\n', commitments.Select(c => c.Id).Order(StringComparer.Ordinal));
        if (Cache.TryGetValue(outcome, out var cached) && ReferenceEquals(cached.Context, context) && cached.Chosen == chosen && cached.Horizon == horizon)
            return cached.Rooms;
        var rooms = Compute(outcome, commitments, context, horizon);
        Cache.AddOrUpdate(outcome, new(context, chosen, horizon, rooms));
        return rooms;
    }

    private static Dictionary<int, RoomRows> Compute(ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon)
    {
        var days = context.InvestmentHorizonDays;
        var served = outcome.ServedRooms
            .Where(s => context.PlannedRoomRoles.GetValueOrDefault(s.Room) == PlannedRole.Classroom && RingClosure.CreditDays(days, s.ServedAt) > 0)
            .OrderBy(s => s.ServedAt).ThenBy(s => s.Room).ToArray();
        var output = new Dictionary<int, RoomRows>();
        void All(params string[] states)
        {
            foreach (var s in served) output[s.Room] = new([], [.. NotModelled, .. states]);
        }

        if (served.Length == 0) return output;
        var read = context.Needs.School;
        if (read.Children.Count == 0) { All(NoChild); return output; }
        if (read.Teacher.Length == 0) { All(NoTeacher); return output; }
        var share = LessonShare(read);
        if (share <= 0) { All(NoLesson); return output; }

        var states = new SortedSet<string>(StringComparer.Ordinal);
        var raw = LessonXpPerDay(read, share);
        var taught = new List<(SchoolChildRead Child, double Value)>();
        foreach (var child in read.Children)
        {
            if (child.Taught.Count == 0) { states.Add(NothingTaught); continue; }
            var value = 0d;
            foreach (var skill in child.Taught)
            {
                if (!context.Investment.TryGetValue(ProjectGraph.SkillKey(child.Pawn, skill.Skill), out var perXp)) { states.Add(SkillUnvalued); continue; }
                value += AppliedXpPerDay(child.Pawn, skill, raw / child.Taught.Count) * perXp;
            }

            taught.Add((child, value));
        }

        // The standing desks seat the children the lessons are worth most to; the room seats the rest, the most valuable first.
        var queue = new Queue<(SchoolChildRead Child, double Value)>(taught.OrderByDescending(t => t.Value).ThenBy(t => t.Child.Pawn, StringComparer.Ordinal).Skip(read.StandingDesks));
        if (queue.Count == 0) { All([Seated, .. states]); return output; }
        var teacher = context.Needs.Pawns.FirstOrDefault(p => p.Pawn == read.Teacher) ?? throw new RequiredFactException("teacher upkeep:" + read.Teacher);
        var speeds = teacher.Upkeep.Speeds.ToDictionary(s => s.Stat, s => s.Value, StringComparer.Ordinal);
        var shortfall = JoinerMoodRules.ShortfallOf(outcome, commitments, context, horizon);
        foreach (var room in served)
        {
            var roomStates = new SortedSet<string>(states, StringComparer.Ordinal);
            var desks = PlannedRoomCredits.Prediction(context, room.Room, room.Stuff)?.Prediction.Desks ?? [];
            if (desks.Count == 0) roomStates.Add(NoDesk);
            var gains = new List<RoomGain>();
            foreach (var desk in desks)
            {
                if (queue.Count == 0) break;
                var (child, value) = queue.Dequeue();
                var ticks = LessonTicksPerDay(read, share, child.LearningRateFactor * desk.LearningRate);
                var lost = PawnValueRules.WorkAdded(new PawnValueFacts(read.Teacher) { Speeds = speeds, Incapable = teacher.Upkeep.IncapableWork, FreeTicksPerDay = ticks }, shortfall)
                    .Sum(s => s.TicksPerDay) / GameTime.TicksPerDay;
                gains.Add(new(child.Pawn, "", value - lost));
            }

            output[room.Room] = new([.. gains], [.. NotModelled, .. roomStates]);
        }

        return output;
    }
}
