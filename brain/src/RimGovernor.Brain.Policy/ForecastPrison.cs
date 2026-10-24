using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Policy;

// The prison room credit (#3805, epic #3650 P1). Confirmed with ilspycmd on Assembly-CSharp.dll (1.7):
// - InteractionWorker_RecruitAttempt.Interacted: while the recipient's resistance is above 0, a chat lowers it by the warden's
//   NegotiationAbility × ResistanceImpactFactorCurve_Mood(CurInstantLevelPercentage, 1 without a mood need) ×
//   ResistanceImpactFactorCurve_Opinion(OpinionOf(warden)), at most to 0; at 0 the next chat recruits (DoRecruit). A wild man
//   (AnimalOrWildMan) is tamed by a chance instead. An initiator Inspired_Recruitment skips the resistance.
// - Pawn_GuestTracker.Recruitable is false only for the unwaveringly loyal; ScheduledForInteraction lets a prisoner be chatted
//   with at most MaxInteractionsPerDay (2) times a day.
// - RoomRoleWorker_PrisonCell scores a room with exactly one non-medical prison bed, PrisonBarracks one with two or more; the
//   PrisonCell and PrisonBarracks thoughts (ThoughtWorker_RoomImpressiveness) stage by the room's Impressiveness score stage.
// - RecruitUtility.Recruit and Pawn.SetFaction clear no memories: the recruit keeps them as a colonist.

/// <summary>
/// The Prison room credit (#3805): the recruitment a planned prison enables. A held prisoner without a prison bed is never
/// recruited (a warden chats with a prisoner in its bed); the room gives a bed to each, up to its beds, the most valuable first,
/// over the outcome's served prisons in service order. A prisoner it beds is recruited <c>d</c> days after the room serves
/// (<see cref="RecruitDays"/>), and from then adds its value as a colonist a day (<see cref="JoinerMoodRules.ValueOf"/>, #3825,
/// against the work the outcome leaves short): the credit is the value × the served days less <c>d</c>, the days the room saves.
/// </summary>
public static class ForecastPrison
{
    /// <summary>The named state of a colony holding no prisoner: the room earns nothing.</summary>
    public const string NoPrisoner = "prison_no_prisoner";

    /// <summary>The named state of a prison no held prisoner without a bed can be recruited from: the room earns nothing.</summary>
    public const string NoRecruit = "prison_no_recruit";

    /// <summary>The named state of an unwaveringly loyal prisoner (Recruitable false): never recruited, it earns nothing.</summary>
    public const string Unrecruitable = "prison_unrecruitable";

    /// <summary>The named state of a recruit whose value as a colonist is not positive: recruiting it earns nothing.</summary>
    public const string CostsMore = "prison_joiner_costs_more";

    /// <summary>The named state of a colony no colonist of which can do Warden work: no prisoner is recruited.</summary>
    public const string NoWarden = "prison_no_warden";

    /// <summary>The named state of a served prison that closes no room with a bed: it beds nobody.</summary>
    public const string NoBed = "prison_no_bed";

    /// <summary>The named state before the colony's first layout plan (#3857): a joiner's food walks are the plan's, so no recruit is valued.</summary>
    public const string AwaitingPlan = "prison_awaiting:" + PawnValueRules.AwaitingLayoutPlan;

    /// <summary>The not-modelled finding of a held wild man: it is tamed by a chance, not by resistance.</summary>
    public const string WildMan = "prison_wildman_taming";

    /// <summary>The not-modelled finding of a recruit's trait thought whose worker reads its situation (JoinerMoodRead.SituationalTraits).</summary>
    public const string SituationalTraits = "joiner_situational_trait_thoughts";

    /// <summary>The mechanics the credit leaves out, named with each served prison.</summary>
    public static readonly EquatableArray<string> NotModelled =
    [
        "prison_warden_chat_labour", // the warden's chat time is not charged against its work
        "prison_recruitment_inspiration", // Inspired_Recruitment's instant recruit
        "prison_mood_held_static", // the prisoner's mood, memories and opinion held at now's over the recruitment
    ];

    /// <summary>The credit (<see cref="RoomCredit"/>): one row per prisoner the room beds, under the prisoner.</summary>
    public static IReadOnlyList<RoomGain> PrisonCredit(ForecastRoomService room, ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon) =>
        Rows.Of(outcome, commitments, context, horizon).GetValueOrDefault(room.Room)?.Gains ?? [];

    /// <summary>The findings (<see cref="RoomFindings"/>): the room's named states and what the credit leaves out.</summary>
    public static IEnumerable<string> Findings(ForecastRoomService room, ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon) =>
        Rows.Of(outcome, commitments, context, horizon).GetValueOrDefault(room.Room)?.Findings ?? [.. NotModelled];

    /// <summary>
    /// The days from a prison serving to its prisoner's recruitment (#3805): the chats to bring <paramref name="resistance"/> to 0
    /// at <paramref name="perChat"/> a chat, plus the chat that recruits, at <paramref name="perDay"/> chats a day. Infinite when a
    /// chat lowers nothing.
    /// </summary>
    public static double RecruitDays(double resistance, double perChat, int perDay)
    {
        DomainException.Require(perDay > 0, "recruit chats a day");
        if (resistance <= 0) return 1d / perDay;
        if (!(perChat > 0)) return double.PositiveInfinity;
        return (Math.Ceiling((resistance / perChat) - 1e-9) + 1) / perDay;
    }

    /// <summary>
    /// The resistance one chat of <paramref name="warden"/> lowers (#3805): its NegotiationAbility × the mood curve at the
    /// prisoner's instant <paramref name="mood"/> (null: 1, no mood need) × the opinion curve at its <paramref name="opinion"/>.
    /// </summary>
    public static double PerChat(PrisonRecruitRead read, WardenRead warden, double? mood, int opinion) =>
        warden.NegotiationAbility * Curve(read.MoodCurve, mood ?? 1, "recruit mood curve") * Curve(read.OpinionCurve, opinion, "recruit opinion curve");

    private static double Curve(EquatableArray<CurvePointRead> points, double x, string what) => points.Count != 0
        ? LayoutFilthRules.EvaluateCurve([.. points.Select(p => new CurvePoint(p.X, p.Y))], x) : throw new RequiredFactException(what);

    private sealed record RoomRows(RoomGain[] Gains, string[] Findings);

    // Every served prison's rows of the outcome, computed once per outcome.
    private static readonly PerOutcome<Dictionary<int, RoomRows>> Rows = new(Compute);

    private static Dictionary<int, RoomRows> Compute(ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon)
    {
        var days = context.InvestmentHorizonDays;
        var served = outcome.ServedRooms
            .Where(s => context.PlannedRoomRoles.GetValueOrDefault(s.Room) == PlannedRole.Prison && RingClosure.CreditDays(days, s.ServedAt) > 0)
            .OrderBy(s => s.ServedAt).ThenBy(s => s.Room).ToArray();
        var output = new Dictionary<int, RoomRows>();
        void All(params string[] states)
        {
            foreach (var s in served) output[s.Room] = new([], [.. NotModelled, .. states]);
        }

        if (served.Length == 0) return output;
        var read = context.Needs.Prison;
        if (read.Prisoners.Count == 0)
        {
            All(NoPrisoner);
            return output;
        }

        var states = new SortedSet<string>(StringComparer.Ordinal);
        var unbedded = read.Prisoners.Where(p => !p.PrisonBed).ToArray();
        if (unbedded.Any(p => !p.Recruitable)) states.Add(Unrecruitable);
        if (unbedded.Any(p => p.Recruitable && p.WildMan)) states.Add(WildMan);
        var recruits = unbedded.Where(p => p.Recruitable && !p.WildMan).ToArray();
        var warden = read.Wardens.OrderByDescending(w => w.NegotiationAbility).ThenBy(w => w.Pawn, StringComparer.Ordinal).FirstOrDefault();
        if (recruits.Length == 0) states.Add(NoRecruit);
        else if (warden is not { NegotiationAbility: > 0 }) states.Add(NoWarden);
        else if (context.FoodHaul is null) states.Add(AwaitingPlan);
        if (recruits.Length == 0 || warden is not { NegotiationAbility: > 0 } || context.FoodHaul is null)
        {
            All([.. states]);
            return output;
        }

        var shortfall = JoinerMoodRules.ShortfallOf(outcome, commitments, context, horizon);
        var valued = new List<(PrisonerRecruitRead Prisoner, double Value)>();
        foreach (var prisoner in recruits)
        {
            var value = JoinerMoodRules.ValueOf(prisoner.Pawn, prisoner.Joiner, shortfall, context);
            states.UnionWith(value.NotModelled);
            if (prisoner.Joiner.Mood is { SituationalTraits.Count: > 0 }) states.Add(SituationalTraits);
            if (value.PerDay > 0) valued.Add((prisoner, value.PerDay));
            else states.Add(CostsMore);
        }

        var queue = new Queue<(PrisonerRecruitRead Prisoner, double Value)>(valued.OrderByDescending(v => v.Value).ThenBy(v => v.Prisoner.Pawn, StringComparer.Ordinal));
        foreach (var room in served)
        {
            var roomStates = new SortedSet<string>(states, StringComparer.Ordinal);
            var prediction = PlannedRoomCredits.Prediction(context, room.Room, room.Stuff);
            var beds = prediction is null ? 0 : (context.PlannedBeds.GetValueOrDefault(room.Room)?.Count ?? 0) + prediction.Prediction.Beds.Count;
            if (beds == 0) roomStates.Add(NoBed);
            var units = RingClosure.CreditDays(days, room.ServedAt);
            var gains = new List<RoomGain>();
            // RoomRoleWorker_PrisonCell scores one prison bed, PrisonBarracks two or more: the thought the room's beds give.
            var role = beds == 1 ? "prison_cell" : "prison_barracks";
            for (var bed = 0; bed < beds && queue.Count > 0; bed++)
            {
                var (prisoner, value) = queue.Dequeue();
                var opinion = prisoner.Opinions.FirstOrDefault(o => o.Warden == warden.Pawn)?.Opinion ?? throw new RequiredFactException("prisoner opinion:" + prisoner.Pawn);
                var perChat = PerChat(read, warden, MoodIn(prisoner, role, prediction!.Prediction.Stage), opinion);
                var recruitDays = RecruitDays(prisoner.Resistance, perChat, read.MaxInteractionsPerDay);
                gains.Add(new(prisoner.Pawn, "", value) { Once = -value * Math.Min(recruitDays, units) });
            }

            output[room.Room] = new([.. gains], [.. NotModelled, .. roomStates]);
        }

        return output;
    }

    // The prisoner's instant mood in the room (#3805): its unclamped mood now with its prison thought now replaced by the room's
    // stage of <paramref name="role"/>'s, clamped (Need_Mood.CurInstantLevel); null without a mood need. A stage row missing throws.
    private static double? MoodIn(PrisonerRecruitRead prisoner, string role, int stage)
    {
        if (prisoner.Mood is not { } mood) return null;
        var offset = mood.CellStages.FirstOrDefault(r => r.Role == role && r.Stage == stage)?.Offset ?? throw new RequiredFactException(role + " thought stage:" + prisoner.Pawn);
        return Math.Clamp(mood.UnclampedLevel + ((offset - mood.CellThought) / 100), 0, 1);
    }
}
