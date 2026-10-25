using RimGovernor.Brain.GameFacts.Observations;
using RimGovernor.Brain.GameOrders.Observations;

namespace RimGovernor.Brain.Policy;

// The prison room credit (#3805, #3899, epic #3650 P1). Confirmed with ilspycmd on Assembly-CSharp.dll (1.7):
// - JobDriver_ChatWithPrisoner: five Toils_Interpersonal.ConvinceRecruitee toils (each a BuildRapport interaction, then a
//   350-tick delay), then TryRecruit (a RecruitAttempt, or TameAttempt for AnimalOrWildMan, then a 350-tick delay): 2,100 toil
//   ticks a job, the attempt 1,750 ticks after the first rapport, besides the walks between them.
// - InteractionWorker_RecruitAttempt.Interacted: while the recipient's resistance is above 0, an attempt lowers it by the warden's
//   NegotiationAbility × ResistanceImpactFactorCurve_Mood(CurInstantLevelPercentage, 1 without a mood need) ×
//   ResistanceImpactFactorCurve_Opinion(OpinionOf(warden)), at most to 0; at 0 the next attempt recruits (DoRecruit). An initiator
//   Inspired_Recruitment recruits at once when the recipient's mode is AttemptRecruit (PopulationUpkeep's Recruit), ending the
//   inspiration. A wild man is tamed by Rand.Chance of the tamer's chance (Inspired_Taming: 1, ending the inspiration); a failed
//   attempt on a prisoner starts no mental state (Pawn_MindState.CheckStartMentalStateBecauseRecruitAttempted).
// - Pawn_InteractionsTracker.AddInteractionThought gives the recipient BuildRapport's RapportBuilt at opinionOffset × the
//   initiator's SocialImpact; MemoryThoughtHandler.TryGainMemory adds it (Thought_MemorySocial never merges), then drops the
//   oldest of its group past stackLimitForSameOtherPawn and the oldest of the def past stackLimit.
// - Pawn_RelationsTracker.OpinionOf: the relations' opinionOffset plus ThoughtHandler.TotalOpinionOffset (per group, the nonzero
//   members' stacked sum rounded away from 0), times the hediffs' opinionOfOthersFactor and rounded (Mathf.RoundToInt, to even)
//   when not 0, 0 when positive toward a hostile pawn, within ±100. Thought_MemorySocial.OpinionOffset fades by
//   InverseLerp(1, lerpOpinionToZeroAfterDurationPct, age / duration) and is discarded past its duration;
//   Thought_MemorySocialCumulative moves 1 toward 0 a day of age.
// - ThoughtHandler.MoodOffsetOfGroup: the mean of the group's nonzero memories times Σ stackedEffectMultiplier^i over their count;
//   Thought_Memory.MoodOffset fades by 1 − age / duration under lerpMoodToZero; a memory past its duration is discarded.
// - InspirationHandler: every 100 ticks a pawn not inspired and not blocked by a hediff starts one by
//   Rand.MTBEventOccurs(LerpDouble(0.5, 1, 210, 10, mood), 60000, 100) at mood 0.5 and above, picking among the defs whose
//   InspirationCanOccur holds by CommonalityFor; one lasts its def's baseDurationDays.
// - Pawn_GuestTracker.Recruitable is false only for the unwaveringly loyal; ScheduledForInteraction lets a prisoner be chatted
//   with at most MaxInteractionsPerDay (2) times a day.
// - RoomRoleWorker_PrisonCell scores a room with exactly one non-medical prison bed, PrisonBarracks one with two or more; the
//   PrisonCell and PrisonBarracks thoughts (ThoughtWorker_RoomImpressiveness) stage by the room's Impressiveness score stage.
// - RecruitUtility.Recruit and Pawn.SetFaction clear no memories: the recruit keeps them as a colonist.
// - A recruitable prisoner of another ideoligion is converted first (#3915; PopulationRules.RecruitIntent sets Convert, switching to
//   Recruit once the prisoner shares the colony's): WorkGiver_Warden_Convert runs JobDriver_ConvertPrisoner in the same
//   ScheduledForInteraction slots, NumTalks (3) Chitchat toils then TryConvert (a ConvertIdeoAttempt), 350 ticks each, the attempt
//   1,050 ticks after the first chitchat. Pawn_IdeoTracker.IdeoConversionAttempt takes certainty down by CertaintyReduction x the
//   tracker's CertaintyChangeFactor (clamped to 0..1) and at 0 sets the warden's ideoligion (certainty 0.5, no roll). Certainty
//   also drifts up each tick by ConversionTuning.CertaintyPerDayByMoodCurve at the mood need's CurLevelPercentage (0 without a mood
//   need) over the factor; CurLevel follows the instant level at the need's seeker rates, so the instant level stands for it. A
//   failed attempt on a prisoner starts no resentment or fight (InteractionWorker_ConvertIdeoAttempt.Interacted).
// - Each Chitchat toil gives the recipient InteractionDefOf.Chitchat.recipientThought: AddInteractionThought scales its stage-0
//   opinionOffset by the converter's SocialImpact. Thought_MemorySocialCumulative.TryMergeWithExistingMemory adds the raw offset
//   to the first same-def, same-other-pawn memory without resetting age; OpinionOffset caps only the displayed value. The
//   recruitment opinion includes these memories only when that converter is its recruiting warden (#3985).

/// <summary>
/// The Prison room credit (#3805, #3899): the recruitment a planned prison enables. A held prisoner without a prison bed is never
/// recruited (a warden chats with a prisoner in its bed); the room gives a bed to each, up to its beds, the most valuable first,
/// over the outcome's served prisons in service order. From the room's service the colony's best warden chats with each bedded
/// prisoner <see cref="PrisonRecruitRead.MaxInteractionsPerDay"/> times a day, chat by chat (<see cref="Recruitment"/>): the
/// prisoner's mood memories expire and fade, each chat adds rapport, its opinion of the warden follows, and an inspiration the
/// warden may have or start recruits it at once. From the expected recruitment day the prisoner adds its value as a colonist a
/// day (<see cref="JoinerMoodRules.ValueOf"/>, #3825, against the work the outcome leaves short): the credit is the value over the
/// served days less the expected days to recruitment, less the warden's work the chats take. A prisoner the autopilot converts first
/// (<see cref="PopulationRules.RecruitIntent"/>, #3915) is chatted to a conversion before its recruitment: those days, and the converter's chat jobs, come
/// first.
/// </summary>
public static class ForecastPrison
{
    /// <summary>The named state of a colony holding no prisoner: the room earns nothing.</summary>
    public const string NoPrisoner = "prison_no_prisoner";

    /// <summary>The named state of a prison no held prisoner without a bed can be recruited from: the room earns nothing.</summary>
    public const string NoRecruit = "prison_no_recruit";

    /// <summary>The named state of an unwaveringly loyal prisoner (Recruitable false): never recruited, it earns nothing.</summary>
    public const string Unrecruitable = "prison_unrecruitable";

    /// <summary>The colony's shared population policy does not recruit this prisoner, so a prison enables no recruitment.</summary>
    public const string NotRecruited = "prison_not_recruited";

    /// <summary>The named state of a recruit whose value as a colonist is not positive: recruiting it earns nothing.</summary>
    public const string CostsMore = "prison_joiner_costs_more";

    /// <summary>The named state of a colony no colonist of which can do Warden work: no prisoner is recruited.</summary>
    public const string NoWarden = "prison_no_warden";

    /// <summary>The named state of a prisoner to convert that no warden can convert (none holds the colony's ideoligion and can talk): it is never recruited, so it earns nothing.</summary>
    public const string NoConverter = "prison_no_converter";

    /// <summary>The named state of a served prison that closes no room with a bed: it beds nobody.</summary>
    public const string NoBed = "prison_no_bed";

    /// <summary>The named state before the colony's first layout plan (#3857): a joiner's food walks are the plan's, so no recruit is valued.</summary>
    public const string AwaitingPlan = "prison_awaiting:" + PawnValueRules.AwaitingLayoutPlan;

    /// <summary>The not-modelled finding of a recruit's trait thought whose worker reads its situation (JoinerMoodRead.SituationalTraits, #3965).</summary>
    public const string SituationalTraits = "joiner_situational_trait_thoughts";

    /// <summary>The mechanics the credit leaves out, named with each served prison.</summary>
    public static readonly EquatableArray<string> NotModelled =
    [
        "prison_warden_walk", // the warden's walks to the prisoner and between the chat toils are not charged (#3964)
        "prison_bedless_memory_renewal", // memories expire from now, though a prisoner renews some until the room beds it (#3966)
        "prison_cell_memories", // the memories the prisoner gains in the cell over the recruitment are not added (#3967)
        "prison_inspiration_shared", // a warden inspiration is counted for every prisoner it could recruit (#3968)
    ];

    /// <summary>The credit (<see cref="RoomCredit"/>): one row per prisoner the room beds, under the prisoner.</summary>
    public static IReadOnlyList<RoomGain> PrisonCredit(ForecastRoomService room, ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon) =>
        Rows.Of(outcome, commitments, context, horizon).GetValueOrDefault(room.Room)?.Gains ?? [];

    /// <summary>The findings (<see cref="RoomFindings"/>): the room's named states and what the credit leaves out.</summary>
    public static IEnumerable<string> Findings(ForecastRoomService room, ForecastCoupledOutcome outcome, IReadOnlyList<ForecastCommitment> commitments, ForecastCoupledContext context, double horizon) =>
        Rows.Of(outcome, commitments, context, horizon).GetValueOrDefault(room.Room)?.Findings ?? [.. NotModelled];

    /// <summary>
    /// The ticks between the first rapport of a chat job and its recruit attempt (#3899): JobDriver_ChatWithPrisoner's five
    /// ConvinceRecruitee toils at their 350-tick defaultDuration, method-body literals mirrored (kept-constants.md).
    /// </summary>
    public const double RapportToils = 5, ToilTicks = 350;

    /// <summary>The toil ticks of one chat job: the five rapport toils and the TryRecruit toil, 350 each (kept-constants.md).</summary>
    public const double ChatJobTicks = (RapportToils + 1) * ToilTicks;

    /// <summary>
    /// The resistance one attempt of <paramref name="warden"/> lowers (#3805): its NegotiationAbility × the mood curve at the
    /// prisoner's instant <paramref name="mood"/> (null: 1, no mood need) × the opinion curve at its <paramref name="opinion"/>.
    /// </summary>
    public static double PerChat(PrisonRecruitRead read, WardenRead warden, double? mood, int opinion) =>
        warden.NegotiationAbility * Curve(read.MoodCurve, mood ?? 1, "recruit mood curve") * Curve(read.OpinionCurve, opinion, "recruit opinion curve");

    private static double Curve(EquatableArray<CurvePointRead> points, double x, string what) => points.Count != 0
        ? LayoutFilthRules.EvaluateCurve([.. points.Select(p => new CurvePoint(p.X, p.Y))], x) : throw new RequiredFactException(what);

    /// <summary>
    /// A prisoner's expected recruitment from its room's service (#3899): the days to it within the served days (after its conversion,
    /// #3915), the recruit chat jobs it takes, and the conversion jobs before them.
    /// </summary>
    public sealed record Expected(double Days, double Jobs, double ConvertJobs);

    /// <summary>The toil ticks of one conversion job: its chitchat toils and the TryConvert toil, <see cref="ToilTicks"/> each (JobDriver_ConvertPrisoner).</summary>
    public static double ConvertJobTicks(PrisonRecruitRead read) => (read.ConvertTalks + 1) * ToilTicks;

    /// <summary>
    /// The chat-by-chat recruitment of <paramref name="prisoner"/> by <paramref name="warden"/> (#3899) in a room serving from
    /// <paramref name="servedAt"/> ticks for <paramref name="units"/> days, at the room's prison thought <paramref name="cell"/>
    /// (null without a mood need). Job k starts at <c>servedAt + k × TicksPerDay / MaxInteractionsPerDay</c>; its five rapport
    /// chats come <see cref="ToilTicks"/> apart and its attempt <see cref="RapportToils"/> toils after the first. Each attempt reads
    /// the prisoner's instant mood (<see cref="Mood"/>) and its opinion of the warden (<see cref="Opinion"/>) at its tick: with
    /// resistance above 0 it lowers it by <see cref="PerChat"/>, at 0 it recruits; a wild man is tamed at the warden's
    /// TameChance instead. The warden's inspiration (<see cref="Inspirations"/>, from its <paramref name="wardenMood"/>) recruits
    /// or tames at once while it is the recruiting def. Expiry and age count from now (Q4 of #3899). A prisoner to convert
    /// (<paramref name="converter"/>, the warden whose attempts convert it) is first chatted to a conversion
    /// (<see cref="Converted"/>); its recruit jobs start in the slots after it, and a conversion the served days do not reach leaves
    /// it unrecruited.
    /// </summary>
    public static Expected Recruitment(PrisonRecruitRead read, WardenRead warden, double? wardenMood, PrisonerRecruitRead prisoner, double? cell, double servedAt, double units,
        ConversionReductionRead? converter = null, double? certainty = null)
    {
        DomainException.Require(read.MaxInteractionsPerDay > 0, "recruit chats a day");
        var opinion = prisoner.Opinions.FirstOrDefault(o => o.Warden == warden.Pawn) ?? throw new RequiredFactException("prisoner opinion:" + prisoner.Pawn);
        var interval = (double)GameTime.TicksPerDay / read.MaxInteractionsPerDay;
        var end = servedAt + (units * GameTime.TicksPerDay);
        var inspirations = new Inspirations(read, warden, wardenMood, prisoner.WildMan ? read.TamingInspiration : read.RecruitInspiration, end);
        var opinionOf = new Opinion(read, opinion);
        var resistance = prisoner.Resistance;
        double alive = 1, days = 0, jobs = 0, converting = 0;
        var first = 0;
        if (converter is not null)
        {
            var (next, attempts) = Converted(read, prisoner, converter, cell, servedAt, end,
                certainty ?? throw new RequiredFactException("prisoner certainty:" + prisoner.Pawn), converter.Warden == warden.Pawn ? opinionOf : null);
            converting = attempts;
            if (next is not { } slot) return new(units, 0, converting);
            first = slot;
        }

        for (var k = first; alive > 0; k++)
        {
            var start = servedAt + (k * interval);
            var attempt = start + (RapportToils * ToilTicks);
            if (attempt > end) break;
            jobs += alive;
            for (var i = 0; i < RapportToils; i++) opinionOf.Chat(start + (i * ToilTicks));
            inspirations.AdvanceTo(attempt);
            double recruited;
            if (prisoner.WildMan) recruited = inspirations.TakeInspired() + inspirations.Scale(1 - opinion.TameChance);
            else if (resistance <= 0) recruited = alive;
            else
            {
                recruited = inspirations.TakeInspired();
                resistance = Math.Max(0, resistance - PerChat(read, warden, Mood(prisoner.Mood, cell, attempt), opinionOf.At(attempt)));
            }

            recruited = Math.Min(recruited, alive);
            days += recruited * (attempt - servedAt) / GameTime.TicksPerDay;
            alive = recruited >= alive ? 0 : alive - recruited;
        }

        return new(days + (alive * units), jobs, converting);
    }

    /// <summary>
    /// A prisoner's conversion (#3915) by <paramref name="converter"/> in a room serving from <paramref name="servedAt"/> to
    /// <paramref name="end"/>: job k starts <c>servedAt + k x TicksPerDay / MaxInteractionsPerDay</c> and its attempt comes
    /// <c>ConvertTalks x ToilTicks</c> later. Certainty, from its value now, drifts up hourly by the curve at the prisoner's
    /// instant mood (<see cref="Mood"/>; its prison thought now before the room serves) over the change factor, and an attempt takes
    /// <c>Reduction x ChangeFactor</c> from it; at 0 it is converted. Returns the slot of the first recruit job (null when the served
    /// days end first) and the attempts made. When the converter is the recruiting warden, <paramref name="opinion"/> receives
    /// each conversion chitchat at its toil tick before the recruitment starts; a different converter changes no opinion of that warden.
    /// </summary>
    public static (int? NextSlot, double Attempts) Converted(PrisonRecruitRead read, PrisonerRecruitRead prisoner, ConversionReductionRead converter, double? cell, double servedAt, double end, double certainty, Opinion? opinion = null)
    {
        var conversion = prisoner.Conversion ?? throw new RequiredFactException("prisoner conversion:" + prisoner.Pawn);
        DomainException.Require(conversion.ChangeFactor > 0, "certainty change factor");
        var interval = (double)GameTime.TicksPerDay / read.MaxInteractionsPerDay;
        double clock = 0;
        for (var k = 0; ; k++)
        {
            var attempt = servedAt + (k * interval) + (read.ConvertTalks * ToilTicks);
            if (attempt > end) return (null, k);
            for (var i = 0; i < read.ConvertTalks; i++) opinion?.ConversionChat(servedAt + (k * interval) + (i * ToilTicks));
            while (clock < attempt)
            {
                var step = Math.Min(GameTime.TicksPerHour, attempt - clock);
                var level = Mood(prisoner.Mood, clock < servedAt ? prisoner.Mood?.CellThought : cell, clock);
                var perDay = level is { } l ? Curve(read.CertaintyDrift, l, "certainty drift curve") : 0;
                certainty = Math.Min(1, certainty + (perDay / conversion.ChangeFactor * step / GameTime.TicksPerDay));
                clock += step;
            }

            certainty = Math.Max(0, certainty - (converter.Reduction * conversion.ChangeFactor));
            if (certainty <= 0) return (k + 1, k + 1);
        }
    }

    /// <summary>
    /// The prisoner's instant mood at <paramref name="tick"/> in the room (#3805, #3899): its unclamped mood now with its prison
    /// thought now replaced by the room's <paramref name="cell"/> offset, and each memory group's offset at the tick in place of
    /// now's (<see cref="MoodGroupAt"/>), clamped (Need_Mood.CurInstantLevel); null without a mood need.
    /// </summary>
    public static double? Mood(PrisonerMoodRead? mood, double? cell, double tick)
    {
        if (mood is null) return null;
        var memories = mood.Memories.Sum(g => MoodGroupAt(g, tick) - MoodGroupAt(g, 0));
        return Math.Clamp(mood.UnclampedLevel + (((cell ?? throw new RequiredFactException("prison thought stage")) - mood.CellThought + memories) / 100), 0, 1);
    }

    /// <summary>
    /// A mood memory group's offset at <paramref name="tick"/> (ThoughtHandler.MoodOffsetOfGroup): its members still held (a
    /// permanent one, or one whose remaining ticks are not past), each at its offset now faded by the share of its remaining
    /// ticks left when it fades; the mean of the nonzero ones times Σ multiplier^i over their count.
    /// </summary>
    public static double MoodGroupAt(MemoryGroupRead group, double tick)
    {
        var offsets = new List<double>();
        foreach (var member in group.Members)
        {
            var offset = member.Life is not MemoryLife.Expires { RemainingTicks: var r } ? member.Offset
                : tick > r ? 0 : member.Fades && r > 0 ? member.Offset * (r - tick) / r : member.Offset;
            if (offset != 0) offsets.Add(offset);
        }

        if (offsets.Count == 0) return 0;
        double stacked = 0, multiplier = 1;
        foreach (var _ in offsets)
        {
            stacked += multiplier;
            multiplier *= group.StackedMultiplier;
        }

        return offsets.Average() * stacked;
    }

    /// <summary>
    /// The prisoner's opinion of one warden over a recruitment (#3899): <see cref="WardenOpinionRead"/>'s relations and situational
    /// parts held at now's, its social memory groups at each tick's age, and the rapport group the warden's chats add to, stacked
    /// as TryGainMemory stacks it against the def's limits (the prisoner's rapport memories of other pawns sharing the def's).
    /// Conversion chitchats merge into its existing cumulative group without resetting age, adding raw offsets before the
    /// displayed cap; cumulative memories of other pawns share the def's limit too.
    /// </summary>
    public sealed class Opinion
    {
        private readonly RapportRead rapport;
        private readonly ChitchatRead chitchat;
        private readonly WardenOpinionRead read;
        private readonly List<(double Born, double Offset, double Duration)> mine = [];
        private readonly List<double> others;
        private readonly SocialMemoryGroupRead[] groups;
        private readonly List<SocialMemoryRead.Cumulative> cumulative;
        private readonly List<SocialMemoryRead.Cumulative> otherCumulative;
        private double cumulativeClock;

        public Opinion(PrisonRecruitRead mechanics, WardenOpinionRead read)
        {
            rapport = mechanics.Rapport;
            chitchat = mechanics.Chitchat;
            this.read = read;
            groups = [.. read.Memories.Where(g => g.Thought != rapport.Thought && g.Thought != chitchat.Thought)];
            cumulative = [.. read.Memories.Where(g => g.Thought == chitchat.Thought).SelectMany(g => g.Members).Select(m =>
                m as SocialMemoryRead.Cumulative ?? throw new RequiredFactException("chitchat memory class"))];
            otherCumulative = [.. read.OtherChitchat];
            foreach (var group in read.Memories.Where(g => g.Thought == rapport.Thought))
                foreach (var member in group.Members)
                    mine.Add(member is SocialMemoryRead.Fading f ? (-f.Age, f.Offset, f.DurationTicks) : throw new RequiredFactException("rapport memory class"));
            others = [.. read.OtherRapport.Select(a => -a)];
        }

        /// <summary>A conversion chitchat: age and discard cumulative memories, merge raw offset into the first held one without resetting its age, then apply the def's limits.</summary>
        public void ConversionChat(double tick)
        {
            if (read.Chitchat == 0) return;
            Age(cumulative, tick - cumulativeClock);
            Age(otherCumulative, tick - cumulativeClock);
            cumulativeClock = tick;
            if (cumulative.Count == 0) cumulative.Add(new(read.Chitchat, 0, chitchat.Cap));
            else cumulative[0] = cumulative[0] with { Offset = cumulative[0].Offset + read.Chitchat };
            if (chitchat.StackLimitPerPawn >= 0)
                while (cumulative.Count > chitchat.StackLimitPerPawn) cumulative.Remove(cumulative.MaxBy(m => m.Age)!);
            if (chitchat.StackLimit < 0) return;
            while (cumulative.Count + otherCumulative.Count > chitchat.StackLimit)
            {
                var oldest = cumulative.Count == 0 ? double.NegativeInfinity : cumulative.Max(m => m.Age);
                if (otherCumulative.Count != 0 && otherCumulative.Max(m => m.Age) > oldest) otherCumulative.Remove(otherCumulative.MaxBy(m => m.Age)!);
                else cumulative.Remove(cumulative.MaxBy(m => m.Age)!);
            }
        }

        private static void Age(List<SocialMemoryRead.Cumulative> memories, double elapsed)
        {
            for (var i = 0; i < memories.Count; i++)
            {
                var memory = memories[i];
                memories[i] = memory with { Offset = CumulativeRaw(memory, elapsed), Age = (memory.Age + elapsed) % GameTime.TicksPerDay };
            }
            memories.RemoveAll(m => m.Offset == 0);
        }

        /// <summary>A rapport chat at <paramref name="tick"/>: its memory added (none when the prisoner cannot get it), then the def's limits kept, the oldest dropped.</summary>
        public void Chat(double tick)
        {
            if (read.Rapport == 0) return;
            mine.RemoveAll(m => tick - m.Born > m.Duration);
            others.RemoveAll(born => tick - born > rapport.DurationTicks);
            mine.Add((tick, read.Rapport, rapport.DurationTicks));
            if (rapport.StackLimitPerPawn >= 0)
                while (mine.Count > rapport.StackLimitPerPawn) mine.Remove(mine.MinBy(m => m.Born));
            if (rapport.StackLimit < 0) return;
            while (mine.Count + others.Count > rapport.StackLimit)
            {
                var oldest = mine.Count == 0 ? double.PositiveInfinity : mine.Min(m => m.Born);
                if (others.Count != 0 && others.Min() < oldest) others.Remove(others.Min());
                else mine.Remove(mine.MinBy(m => m.Born));
            }
        }

        /// <summary>The opinion at <paramref name="tick"/>, as Pawn_RelationsTracker.OpinionOf sums and rounds it.</summary>
        public int At(double tick)
        {
            var total = read.Relations + read.Situational + groups.Sum(g => SocialGroupAt(g.StackedMultiplier, g.LerpAfter, g.Members.Select(m => Member(m, tick, g.LerpAfter))))
                + SocialGroupAt(chitchat.StackedMultiplier, 0, cumulative.Select(m => Member(m, tick - cumulativeClock, 0)))
                + SocialGroupAt(rapport.StackedMultiplier, rapport.LerpAfter, mine.Select(m => (tick - m.Born, Fading(m.Offset, tick - m.Born, m.Duration, rapport.LerpAfter))));
            if (total != 0) total = (int)Math.Round(total * read.Factor, MidpointRounding.ToEven);
            if (total > 0 && read.Hostile) total = 0;
            return Math.Clamp(total, -100, 100);
        }

        // The wire carries only the two classes (#3899): a cumulative memory, else a fading one.
        private static (double Age, double Offset) Member(SocialMemoryRead member, double tick, double lerp) =>
            member is SocialMemoryRead.Cumulative c ? ((c.Age + tick) % GameTime.TicksPerDay, Cumulative(c, tick))
            : member is SocialMemoryRead.Fading f ? (f.Age + tick, Fading(f.Offset, f.Age + tick, f.DurationTicks, lerp))
            : throw new RequiredFactException("social memory class");

        // Thought_MemorySocial.OpinionOffset at an age: 0 past the duration, else the offset × InverseLerp(1, lerp, age / duration).
        private static double Fading(double offset, double age, double duration, double lerp)
        {
            if (age > duration || duration <= 0) return 0;
            var pct = age / duration;
            var factor = lerp == 1 ? 0 : Math.Clamp((pct - 1) / (lerp - 1), 0, 1);
            return offset * factor;
        }

        // Thought_MemorySocialCumulative: 1 toward 0 for each day of age reached (ThoughtInterval at age 60000, then reset), at most its cap.
        private static double Cumulative(SocialMemoryRead.Cumulative memory, double tick) => Math.Min(CumulativeRaw(memory, tick), memory.Cap);

        private static double CumulativeRaw(SocialMemoryRead.Cumulative memory, double tick)
        {
            var steps = Math.Floor((memory.Age + tick) / GameTime.TicksPerDay);
            var offset = Math.Sign(memory.Offset) * Math.Max(0, Math.Abs(memory.Offset) - steps);
            return offset;
        }

        // ThoughtHandler.OpinionOffsetOfGroup: the nonzero members, by age when the multiplier is not 1, their stacked sum rounded away from 0.
        private static int SocialGroupAt(double multiplier, double lerp, IEnumerable<(double Age, double Offset)> members)
        {
            var held = members.Where(m => m.Offset != 0).ToList();
            if (held.Count == 0) return 0;
            if (multiplier != 1) held.Sort((a, b) => a.Age.CompareTo(b.Age));
            double sum = 0, factor = 1;
            foreach (var (_, offset) in held)
            {
                sum += offset * factor;
                factor *= multiplier;
            }

            var rounded = (int)Math.Round(sum, MidpointRounding.ToEven);
            return sum == 0 ? 0 : sum > 0 ? Math.Max(rounded, 1) : Math.Min(rounded, -1);
        }
    }

    /// <summary>
    /// The warden's inspirations as a renewal process (#3899, Q2 of #3899), over one-hour steps from now: the one it has now
    /// runs out its duration; while it has none and is not blocked, each step starts one with the chance of the step's
    /// InspirationHandler checks (one every <see cref="CheckTicks"/> at the MTB its mood gives, <see cref="StartChance"/>), the
    /// def picked by its commonality share; each lasts its def's duration. The mass inspired with <c>recruiting</c> (the def
    /// whose attempt recruits or tames at once) is taken at an attempt (<see cref="TakeInspired"/>), ending it.
    /// </summary>
    public sealed class Inspirations
    {
        /// <summary>InspirationHandler's CheckStartInspirationIntervalTicks, a private const mirrored (kept-constants.md).</summary>
        public const double CheckTicks = 100;

        private const double Step = GameTime.TicksPerHour;
        private readonly double[] recruiting;
        private readonly double[] other;
        private readonly double start;
        private readonly (double Share, int Steps)[] starts;
        private readonly int recruitSteps;
        private readonly double recruitShare;
        private double free;
        private int step;

        public Inspirations(PrisonRecruitRead read, WardenRead warden, double? mood, string def, double end)
        {
            var length = (int)Math.Ceiling(end / Step) + 2;
            var longest = warden.Inspirations.Select(i => Steps(i.DurationDays)).Append(warden.Inspired is { } n ? Steps(n.DurationDays - n.AgeDays) : 0).DefaultIfEmpty(0).Max();
            recruiting = new double[length + longest + 1];
            other = new double[length + longest + 1];
            if (warden.Inspired is { } now)
            {
                var left = Steps(now.DurationDays - now.AgeDays);
                (now.Def == def ? recruiting : other)[left] = 1;
            }
            else free = 1;

            // No def that can occur (InspirationHandler.GetRandomAvailableInspitationDef finds none) starts nothing.
            var total = warden.Inspirations.Sum(i => i.Commonality);
            start = warden.InspirationBlocked || total <= 0 ? 0 : StartChance(mood, Step);
            var mine = warden.Inspirations.FirstOrDefault(i => i.Def == def);
            recruitShare = total > 0 && mine is not null ? mine.Commonality / total : 0;
            recruitSteps = mine is null ? 1 : Steps(mine.DurationDays);
            starts = total > 0 ? [.. warden.Inspirations.Where(i => i.Def != def).Select(i => (i.Commonality / total, Steps(i.DurationDays)))] : [];
        }

        private static int Steps(double days) => Math.Max(1, (int)Math.Ceiling(days * GameTime.TicksPerDay / Step));

        /// <summary>
        /// The chance a pawn not inspired starts one over <paramref name="ticks"/> (InspirationHandler.CheckStartRandomInspiration):
        /// 0 without a mood or below 0.5, else 1 − (1 − p)^(ticks / <see cref="CheckTicks"/>) for p = Rand.MTBEventOccurs' chance a
        /// check at the MTB days LerpDouble(0.5, 1, 210, 10, mood) gives (StartInspirationMTBDays; its 210 a literal, kept-constants.md).
        /// </summary>
        public static double StartChance(double? mood, double ticks)
        {
            if (mood is not { } level || level < 0.5) return 0;
            var mtb = 210 + ((Math.Min(level, 1) - 0.5) / 0.5 * (10 - 210));
            var p = CheckTicks / (mtb * GameTime.TicksPerDay);
            return 1 - Math.Pow(1 - p, ticks / CheckTicks);
        }

        /// <summary>Runs the steps up to <paramref name="tick"/>.</summary>
        public void AdvanceTo(double tick)
        {
            for (; step < recruiting.Length - 1 && (step + 1) * Step <= tick; step++)
            {
                free += recruiting[step] + other[step];
                recruiting[step] = other[step] = 0;
                if (start <= 0 || free <= 0) continue;
                var started = free * start;
                free -= started;
                if (recruitShare > 0) recruiting[Math.Min(step + recruitSteps, recruiting.Length - 1)] += started * recruitShare;
                foreach (var (share, steps) in starts) other[Math.Min(step + steps, other.Length - 1)] += started * share;
            }
        }

        /// <summary>The mass inspired with the recruiting def now, taken (the attempt ends the inspiration with the recruit).</summary>
        public double TakeInspired()
        {
            double taken = 0;
            for (var i = step; i < recruiting.Length; i++)
            {
                taken += recruiting[i];
                recruiting[i] = 0;
            }

            return taken;
        }

        /// <summary>Keeps <paramref name="kept"/> of the mass not inspired with the recruiting def; returns what it drops.</summary>
        public double Scale(double kept)
        {
            var dropped = free * (1 - kept);
            free *= kept;
            for (var i = step; i < other.Length; i++)
            {
                dropped += other[i] * (1 - kept);
                other[i] *= kept;
            }

            return dropped;
        }
    }

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
        var recruits = unbedded.Where(p => p.Recruitable).Select(prisoner =>
        {
            var population = context.PrisonerPopulation ?? throw new RequiredFactException("prisoner population");
            var person = population.Prisoners.FirstOrDefault(p => p.Pawn.Value == prisoner.Pawn)
                ?? throw new RequiredFactException("population prisoner:" + prisoner.Pawn);
            var intent = PopulationRules.RecruitIntent(person, population.Colony);
            if (intent is null) states.Add(NotRecruited);
            return (Prisoner: prisoner, Person: person, Intent: intent);
        }).Where(p => p.Intent is not null).ToArray();
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
        var certaintyByPawn = new Dictionary<string, double>(StringComparer.Ordinal);
        var converters = new Dictionary<string, ConversionReductionRead>(StringComparer.Ordinal);
        foreach (var (prisoner, person, intent) in recruits)
        {
            var value = JoinerMoodRules.ValueOf(prisoner.Pawn, prisoner.Joiner, shortfall, context);
            states.UnionWith(value.NotModelled);
            if (prisoner.Joiner.Mood is { SituationalTraits.Count: > 0 }) states.Add(SituationalTraits);
            if (value.PerDay <= 0)
            {
                states.Add(CostsMore);
                continue;
            }

            // A prisoner the autopilot converts first (#3915) is converted by the warden whose attempts take the most certainty.
            if (intent == PrisonerInteractionMode.Convert)
            {
                var conversion = prisoner.Conversion ?? throw new RequiredFactException("prisoner conversion:" + prisoner.Pawn);
                if (conversion.Converters.MaxBy(c => c.Reduction) is not { } converter)
                {
                    states.Add(NoConverter);
                    continue;
                }

                certaintyByPawn[prisoner.Pawn] = person.Observed.Settings.PolicyInputs.Ideo is PawnIdeo.Member member ? member.Certainty
                    : throw new RequiredFactException("prisoner certainty:" + prisoner.Pawn);
                converters[prisoner.Pawn] = converter;
            }

            valued.Add((prisoner, value.PerDay));
        }

        // A warden's chat jobs charged as the work they take from it (#3899, as the classroom charges its teacher, #3809): the
        // shortfall rows its chat toil ticks a day would fill, a job's share of them.
        var pawn = context.Needs.Pawns.FirstOrDefault(p => p.Pawn == warden.Pawn) ?? throw new RequiredFactException("warden upkeep:" + warden.Pawn);
        double LostPerJob(string wardenPawn, double jobTicks)
        {
            var upkeep = context.Needs.Pawns.FirstOrDefault(p => p.Pawn == wardenPawn) ?? throw new RequiredFactException("warden upkeep:" + wardenPawn);
            return PawnValueRules.WorkAdded(new PawnValueFacts(wardenPawn)
            {
                Speeds = upkeep.Upkeep.Speeds.ToDictionary(s => s.Stat, s => s.Value, StringComparer.Ordinal), Incapable = upkeep.Upkeep.IncapableWork,
                FreeTicksPerDay = jobTicks * read.MaxInteractionsPerDay,
            }, shortfall).Sum(s => s.TicksPerDay) / GameTime.TicksPerDay / read.MaxInteractionsPerDay;
        }

        var lostPerJob = LostPerJob(warden.Pawn, ChatJobTicks);
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
                var cell = CellOffset(prisoner, role, prediction!.Prediction.Stage);
                converters.TryGetValue(prisoner.Pawn, out var converter);
                var expected = Recruitment(read, warden, pawn.Mood?.Target, prisoner, cell, room.ServedAt, units, converter,
                    converter is null ? null : certaintyByPawn[prisoner.Pawn]);
                var converting = converter is null ? 0 : LostPerJob(converter.Warden, ConvertJobTicks(read)) * expected.ConvertJobs;
                gains.Add(new(prisoner.Pawn, "", value) { Once = -(value * expected.Days) - (lostPerJob * expected.Jobs) - converting });
            }

            output[room.Room] = new([.. gains], [.. NotModelled, .. roomStates]);
        }

        return output;
    }

    // The room's prison thought offset for the prisoner (#3805): its stage row of role's thought; null without a mood need. A stage row missing throws.
    private static double? CellOffset(PrisonerRecruitRead prisoner, string role, int stage)
    {
        if (prisoner.Mood is not { } mood) return null;
        return mood.CellStages.FirstOrDefault(r => r.Role == role && r.Stage == stage)?.Offset ?? throw new RequiredFactException(role + " thought stage:" + prisoner.Pawn);
    }
}
