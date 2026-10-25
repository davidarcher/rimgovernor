using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts;

namespace RimGovernor.Brain.GameFacts.Observations;

/// <summary>
/// The needs forecast read (#3781), read whole: every colonist given with its needs mechanics and the targets that serve
/// them, the map's weather and deterioration inputs, its powered research benches and the corpses in sight. The native
/// read throws when it cannot read a member; it never returns a partial record.
/// </summary>
public sealed record NeedsForecastRead
{
    public required Tick Tick { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<NeedsForecastPawn>))]
    public required EquatableArray<NeedsForecastPawn> Pawns { get; init; }
    /// <summary>The map's current weather (WeatherManager.CurWeatherLerped): its weatherThought def ("" none) and that def's stage count (#3339).</summary>
    public required string WeatherThought { get; init; }
    public required int WeatherThoughtStages { get; init; }
    /// <summary>WeatherManager.RainRate now, held over the horizon (#3339).</summary>
    public required double RainRate { get; init; }
    /// <summary>Map.Area, the cell count SteadyEnvironmentEffects cycles over (#3339).</summary>
    public required int MapArea { get; init; }
    /// <summary>The DeteriorationRate stat's StatPart_EnvironmentalEffects def data (#3339); null when the stat has no such part.</summary>
    public required NeedDeteriorationPart? Deterioration { get; init; }
    /// <summary>
    /// Every spawned research bench with power (or none needed), in bench id order: the benches a researcher can work at whatever
    /// the current project is (#3567), each with its <c>StatDefOf.ResearchSpeedFactor</c> now (#3800). Empty means none observed.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<ResearchBenchRead>))]
    public required EquatableArray<ResearchBenchRead> ResearchBenches { get; init; }
    /// <summary>
    /// Every unfogged spawned corpse whose sighting raises an observed-corpse event (#3806: <c>Corpse.GiveObservedHistoryEvent</c>
    /// is ObservedLayingCorpse or ObservedLayingRottingCorpse, so a humanlike out of storage), in corpse id order, with each
    /// colonist's sightings of it. Empty means none lies in sight.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<CorpseSightingRead>))]
    public required EquatableArray<CorpseSightingRead> Corpses { get; init; }
    /// <summary>The deathrest defs the deathrest chamber credit reads (#3810); null without Biotech, where no pawn deathrests.</summary>
    public required DeathrestDefsRead? Deathrest { get; init; }
    /// <summary>The recruitment mechanics the prison credit reads (#3805): the wardens among the colonists given and every prisoner of the colony on the map.</summary>
    public required PrisonRecruitRead Prison { get; init; }
    /// <summary>The lesson mechanics the classroom credit reads (#3809): the learning need's rates, the learning desires, the
    /// colony's school desks, its teacher and every child with a learning need.</summary>
    public required SchoolRead School { get; init; }
    /// <summary>The baby fit mechanics the nursery credit reads (#3854): the fit defs and every baby with who hears it.</summary>
    public required BabyFitsRead Babies { get; init; }
}
/// <summary>
/// The lesson mechanics (#3809). A child's learning need (<c>Need_Learning</c>) falls <see cref="FallPerTick"/>
/// (<c>Need_Learning.BaseFallPerTick</c>, asleep or awake) and a learning job refills it <see cref="RefillPerTick"/>
/// (<c>LearningUtility.NeedSatisfiedPerTick</c>) times its <c>LearningUtility.LearningRateFactor</c> until it is satisfied. A
/// lesson (<see cref="Lessontaking"/>, <c>LearningDesireDefOf.Lessontaking</c>) teaches its <see cref="LessonXpPerTick"/>
/// (<c>xpPerTick</c>) times that factor a tick (<c>JobDriver_Lessongiving</c>). <see cref="Desires"/> are every
/// <c>LearningDesireDef</c> in def order. <see cref="StandingDesks"/> counts the colony's spawned <c>ThingDefOf.SchoolDesk</c>s.
/// <see cref="Teacher"/> is the pawn <c>SchoolUtility.FindTeacher</c> takes first while it can teach: the first of
/// <c>ChildcareUtility.SpawnedColonistChildcarers</c> (FreeColonistsSpawned order) that is no juvenile, has Childcare active and
/// can talk; "" when none is.
/// </summary>
public sealed record SchoolRead
{
    public required double FallPerTick { get; init; }
    public required double RefillPerTick { get; init; }
    public required string Lessontaking { get; init; }
    public required double LessonXpPerTick { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<LearningDesireRead>))]
    public required EquatableArray<LearningDesireRead> Desires { get; init; }
    public required int StandingDesks { get; init; }
    public required string Teacher { get; init; }
    /// <summary>Every colonist given with a learning need (<c>pawn.needs.learning</c>), in pawn id order. Empty means no child.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<SchoolChildRead>))]
    public required EquatableArray<SchoolChildRead> Children { get; init; }
}
/// <summary>A <c>LearningDesireDef</c>: its <c>selectionWeight</c> and whether its worker's <c>CanGiveDesire</c> holds now.</summary>
public sealed record LearningDesireRead(string Def, double SelectionWeight, bool CanGive);
/// <summary>
/// A child with a learning need (#3809): its <c>StatDefOf.LearningRateFactor</c> and the skills <see cref="SchoolRead.Teacher"/>
/// would teach it (<c>JobDriver_Lessongiving.ChooseSkill</c>: the teacher's skills neither it nor the child is
/// <c>TotallyDisabled</c> in that have a <c>lessonInteraction</c>, best first, its <c>TopTeachableSkillCount</c>, one drawn at
/// random a lesson), each with the child's <see cref="SkillLearning"/> of it. Empty without a teacher.
/// </summary>
public sealed record SchoolChildRead
{
    public required string Pawn { get; init; }
    public required double LearningRateFactor { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<TaughtSkillRead>))]
    public required EquatableArray<TaughtSkillRead> Taught { get; init; }
}
/// <summary>One skill a lesson may teach a child, by SkillDef name, with the child's learning mechanics of it.</summary>
public sealed record TaughtSkillRead(string Skill, SkillLearning Learning);
/// <summary>
/// The baby fit mechanics (#3854). <c>MentalFitGenerator.TickInterval</c> checks an awake pawn out of any mental state every
/// <see cref="CheckIntervalTicks"/> (its private <c>CheckInterval</c>), trying each <see cref="Fits"/> def in def order and starting
/// the first whose <c>Rand.MTBEventOccurs</c> fires; after a mental state ends it waits <see cref="RecoveryAwakeTicks"/> awake ticks
/// (its private <c>MinTicksSinceRecoveryToHaveFit</c>). A fit (<c>MentalState_BabyFit</c>) gives each hearer within reach one memory.
/// </summary>
public sealed record BabyFitsRead
{
    /// <summary>Pawn.CheckForDisturbedSleep's minimum interval between gains, in ticks.</summary>
    public required double SleepDisturbanceIntervalTicks { get; init; }
    /// <summary>Loaded play activities, refill/fall mechanics and normal-quality building stats; null without Biotech.</summary>
    public required BabyPlayMechanicsRead? Play { get; init; }
    public required double CheckIntervalTicks { get; init; }
    public required double RecoveryAwakeTicks { get; init; }
    /// <summary>Every <c>MentalFitDef</c> whose <c>developmentalStageFilter</c> holds Baby, in def order. Empty without Biotech.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<BabyFitRead>))]
    public required EquatableArray<BabyFitRead> Fits { get; init; }
    /// <summary>Every colonist given whose developmental stage a <see cref="Fits"/> def admits, in pawn id order. Empty means no baby.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<BabyRead>))]
    public required EquatableArray<BabyRead> Babies { get; init; }
}
/// <summary>
/// A baby fit def: its <c>mtbDaysMoodCurve</c> over <c>Need_Mood.CurLevelPercentage</c>, its mental state's
/// <c>maxTicksBeforeRecovery</c> (min and max are equal and recovery never comes sooner) and whether that state's hearers gain a
/// memory (a <c>MentalState_BabyCry</c> or <c>MentalState_BabyGiggle</c>; false for a state class the read does not know).
/// </summary>
public sealed record BabyFitRead
{
    public required string Def { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<CurvePointRead>))]
    public required EquatableArray<CurvePointRead> MtbDaysByMood { get; init; }
    public required double DurationTicks { get; init; }
    public required bool Heard { get; init; }
}
/// <summary>
/// One baby (#3854): the fits that can start for it now (<see cref="BabyFitsRead.Fits"/> defs whose state worker's
/// <c>StateCanOccur</c> holds), whether it lies in a room the game scores a Nursery (<c>RoomRoleWorker_Nursery</c>), and every
/// colonist given who hears it (<c>GenClamor.DoClamor</c> at <c>MentalState_BabyFit.BabyScreamRadius</c>), with the doors as they are
/// and with its nursery's doors open.
/// </summary>
public sealed record BabyRead
{
    /// <summary>Eligible SleepDisturbed recipients at each canonical rest target and their native default sleeping surface.
    /// Empty means none can gain the thought. A target with both reach flags false is explicitly out of sound reach.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<BabySleepHearerRead>))]
    public required EquatableArray<BabySleepHearerRead> SleepHearers { get; init; }
    /// <summary>CribQuality offsets for the current crib, the alternate surface and a new normal-quality crib; null without mood.</summary>
    public required BabyCribMoodRead? CribMood { get; init; }
    /// <summary>Null when the baby has no play need; otherwise its native play opportunities.</summary>
    public required BabyPlayRead? Play { get; init; }
    public required string Pawn { get; init; }
    /// <summary>The native sleeping surface without the nursery's beds; ground is named by an empty Bed, as NeedSleep.</summary>
    public required NeedSleep SleepWithoutNursery { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> Fits { get; init; }
    public required bool InNursery { get; init; }
    /// <summary>In pawn id order; empty means nobody hears it either way.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<BabyHearerRead>))]
    public required EquatableArray<BabyHearerRead> Hearers { get; init; }
}
public sealed record BabyCribMoodRead(double Current, double WithoutNursery, double Normal);
/// <summary>A native baby-play giver, classified by its actual worker implementation.</summary>
public enum BabyPlayKind { Static, Walking, Toys }
public sealed record BabyPlayActivityRead(string Def, BabyPlayKind Kind);
/// <summary>Normal-quality BabyPlayGainFactor for a building/stuff pair; Decoration distinguishes room multipliers from toy sources.</summary>
public sealed record BabyPlayBuildingRead(string Def, string Stuff, bool Decoration, double GainFactor);
public sealed record BabyPlayMechanicsRead
{
    public required double FallPerTick { get; init; }
    public required double GainPerTick { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<BabyPlayActivityRead>))]
    public required EquatableArray<BabyPlayActivityRead> Activities { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<BabyPlayBuildingRead>))]
    public required EquatableArray<BabyPlayBuildingRead> Buildings { get; init; }
}
/// <summary>A baby with a play need. Empty Carers means none can reach it and perform static play now.</summary>
public sealed record BabyPlayRead
{
    /// <summary>The observed active play carer; empty when no play job currently targets this baby.</summary>
    public required string OngoingCarer { get; init; }
    public required double RoomFactor { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<BabyPlayCarerRead>))]
    public required EquatableArray<BabyPlayCarerRead> Carers { get; init; }
}
/// <summary>Per carer, usable toy source and its room decoration factor, both now and excluding the nursery's furniture.
/// Walking decoration factors enclose every reachable room and retain 1 as a deliberately loose absence bound;
/// endpoints need not be attainable. They do not assert a stationary walk.</summary>
public sealed record BabyPlayCarerRead(string Pawn, BabyPlaySourceRead? Toy, BabyPlaySourceRead? ToyWithoutNursery,
    double WalkingMinFactor, double WalkingMaxFactor, double WalkingWithoutNurseryMinFactor, double WalkingWithoutNurseryMaxFactor);
public sealed record BabyPlaySourceRead(double GainFactor, double MinRoomFactor, double MaxRoomFactor);
/// <summary>
/// A colonist who hears a baby: with the doors as they are now (<see cref="Now"/>), with the doors of the baby's nursery open
/// (<see cref="NurseryOpen"/>; as <see cref="Now"/> for a baby in no nursery), and the memory each heard fit gives it.
/// </summary>
public sealed record BabyHearerRead
{
    public required string Pawn { get; init; }
    public required bool Now { get; init; }
    public required bool NurseryOpen { get; init; }
    /// <summary>Per heard fit def, the memory it gives this hearer; a fit whose memory it cannot get (ThoughtUtility.CanGetThought) has none.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<HeardMemoryRead>))]
    public required EquatableArray<HeardMemoryRead> Memories { get; init; }
}
/// <summary>
/// The memory a fit gives its hearer (#3854): the parent's (<c>MyCryingBaby</c>, <c>MyGigglingBaby</c>) or anyone else's
/// (<c>CryingBaby</c>, <c>GigglingBaby</c>) ThoughtDef, its <c>MoodOffset</c> on the hearer as an unattached memory, its
/// <c>DurationTicks</c>, <c>stackLimit</c> and <c>stackedEffectMultiplier</c>.
/// </summary>
public sealed record HeardMemoryRead(string Fit, string Thought, double Offset, double DurationTicks, int StackLimit, double StackedMultiplier);
/// <summary>A cry's shared SleepDisturbed memory at one hearer's sleeping target (empty Target names ground).
/// Reach uses that target's cell, independently of the pawn's current standing position.</summary>
public sealed record BabySleepHearerRead(string Pawn, string Target, bool Now, bool NurseryOpen, HeardMemoryRead Memory);
/// <summary>
/// The recruitment mechanics (#3805, #3899), from <c>InteractionWorker_RecruitAttempt.Interacted</c>: while a prisoner's resistance
/// is above 0, each warden recruit attempt lowers it by the warden's <c>StatDefOf.NegotiationAbility</c> times <see cref="MoodCurve"/>
/// at the prisoner's instant mood (1 without a mood need) times <see cref="OpinionCurve"/> at its opinion of the warden; at 0 the
/// next attempt recruits. A prisoner is chatted with at most <see cref="MaxInteractionsPerDay"/> times a day
/// (<c>Pawn_GuestTracker.MaxInteractionsPerDay</c>, as <c>ScheduledForInteraction</c> gates it). The two curves are the worker's
/// private <c>ResistanceImpactFactorCurve_Mood</c> and <c>ResistanceImpactFactorCurve_Opinion</c>, point by point. Each chat job
/// (<c>JobDriver_ChatWithPrisoner</c>) first builds rapport (<see cref="Rapport"/>, <c>InteractionDefOf.BuildRapport</c>'s
/// recipient thought); an initiator inspired by <see cref="RecruitInspiration"/> (<c>InspirationDefOf.Inspired_Recruitment</c>)
/// recruits a prisoner at once, one inspired by <see cref="TamingInspiration"/> (<c>InspirationDefOf.Inspired_Taming</c>) tames a
/// wild man at once, either ending the inspiration.
/// <para>
/// A prisoner of another ideoligion is converted before it is recruited (#3915): <c>WorkGiver_Warden_Convert</c> runs a
/// <c>JobDriver_ConvertPrisoner</c> job per chat slot, <see cref="ConvertTalks"/> chitchat toils (its private <c>NumTalks</c>) and
/// then a <c>ConvertIdeoAttempt</c>, which lowers the prisoner's certainty (<see cref="PrisonerConversionRead"/>); the certainty
/// also drifts up each day by <see cref="CertaintyDrift"/> (<c>ConversionTuning.CertaintyPerDayByMoodCurve</c>, over the
/// prisoner's mood level).
/// </para>
/// </summary>
public sealed record PrisonRecruitRead
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<CurvePointRead>))]
    public required EquatableArray<CurvePointRead> MoodCurve { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<CurvePointRead>))]
    public required EquatableArray<CurvePointRead> OpinionCurve { get; init; }
    public required int MaxInteractionsPerDay { get; init; }
    public required int ConvertTalks { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<CurvePointRead>))]
    public required EquatableArray<CurvePointRead> CertaintyDrift { get; init; }
    public required RapportRead Rapport { get; init; }
    public required string RecruitInspiration { get; init; }
    public required string TamingInspiration { get; init; }
    /// <summary>Every colonist given that can do Warden work (not <c>Pawn.WorkTypeIsDisabled</c>), in pawn id order. Empty means none can.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<WardenRead>))]
    public required EquatableArray<WardenRead> Wardens { get; init; }
    /// <summary>Every spawned prisoner of the colony on the map (<c>MapPawns.PrisonersOfColonySpawned</c>), in pawn id order. Empty means none is held.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<PrisonerRecruitRead>))]
    public required EquatableArray<PrisonerRecruitRead> Prisoners { get; init; }
}
/// <summary>One point of a game <c>SimpleCurve</c>, in its order.</summary>
public sealed record CurvePointRead(double X, double Y);
/// <summary>
/// The rapport memory a chat gives the prisoner (#3899): <c>InteractionDefOf.BuildRapport.recipientThought</c>'s defName, its
/// <c>DurationTicks</c>, <c>stackLimit</c> (over every other pawn), <c>stackLimitForSameOtherPawn</c>, <c>stackedEffectMultiplier</c>
/// and <c>lerpOpinionToZeroAfterDurationPct</c>, as <c>MemoryThoughtHandler.TryGainMemory</c> stacks it and
/// <c>Thought_MemorySocial.OpinionOffset</c> fades it.
/// </summary>
public sealed record RapportRead(string Thought, double DurationTicks, int StackLimit, int StackLimitPerPawn, double StackedMultiplier, double LerpAfter);
/// <summary>
/// A colonist who can do Warden work (#3805, #3899): its <c>StatDefOf.NegotiationAbility</c>, its inspiration now (null when it
/// has none), whether a hediff blocks a new one (<c>InspirationHandler.BlockedByHediff</c>), and each inspiration def that can
/// start for it now (<c>InspirationWorker.InspirationCanOccur</c>), in def order.
/// </summary>
public sealed record WardenRead(string Pawn, double NegotiationAbility)
{
    public required InspirationNowRead? Inspired { get; init; }
    public required bool InspirationBlocked { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<InspirationChanceRead>))]
    public required EquatableArray<InspirationChanceRead> Inspirations { get; init; }
}
/// <summary>The inspiration a pawn has now: its def and <c>Inspiration.AgeDays</c>, and the def's <c>baseDurationDays</c>.</summary>
public sealed record InspirationNowRead(string Def, double AgeDays, double DurationDays);
/// <summary>An inspiration def that can start for a pawn: <c>InspirationWorker.CommonalityFor</c> (its weight in the random pick) and <c>baseDurationDays</c>.</summary>
public sealed record InspirationChanceRead(string Def, double Commonality, double DurationDays);
/// <summary>
/// A prisoner's opinion of one warden (#3805, #3899), in the parts <c>Pawn_RelationsTracker.OpinionOf</c> sums, read without its
/// thought cache: the relations' opinionOffset, the situational social thoughts' <c>OpinionOffsetOfGroup</c>s now (each rounded),
/// its social memory groups of the warden (<see cref="SocialMemoryGroupRead"/>), the hediffs' <c>opinionOfOthersFactor</c> product
/// and whether the prisoner is hostile to the warden (a positive opinion of a hostile pawn is 0). Rapport is the opinionOffset
/// one chat's rapport memory starts at (<see cref="RapportRead"/>'s stage offset times the warden's <c>StatDefOf.SocialImpact</c>,
/// as <c>Pawn_InteractionsTracker.AddInteractionThought</c> scales it; 0 when the prisoner cannot get it: no mood need,
/// <c>ThoughtUtility.CanGetThought</c>, the developmental filter or nullification), OtherRapport the ages of the prisoner's
/// rapport memories of other pawns (they share the def's stackLimit), and TameChance the chance a tame attempt of the warden
/// succeeds on the prisoner held in its cell (the worker's TameAnimalChance × wildness curve × prison, bond and venerated
/// factors), read for a wild man and 0 for a pawn recruited by resistance, which is never rolled.
/// </summary>
public sealed record WardenOpinionRead(string Warden, int Relations, int Situational, double Factor, bool Hostile)
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<SocialMemoryGroupRead>))]
    public required EquatableArray<SocialMemoryGroupRead> Memories { get; init; }
    public required double Rapport { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<double>))]
    public required EquatableArray<double> OtherRapport { get; init; }
    public required double TameChance { get; init; }
}
/// <summary>
/// One group of a pawn's social memories of another (#3899, <c>Thought_MemorySocial.GroupsWith</c>): its def, the def's
/// <c>stackedEffectMultiplier</c> and <c>lerpOpinionToZeroAfterDurationPct</c>, and its memories.
/// </summary>
public sealed record SocialMemoryGroupRead(string Thought, double StackedMultiplier, double LerpAfter)
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<SocialMemoryRead>))]
    public required EquatableArray<SocialMemoryRead> Members { get; init; }
}
/// <summary>
/// A social memory (#3899), by its class: a <c>Thought_MemorySocial</c> fades its opinionOffset by its age
/// (<c>OpinionOffset</c>'s InverseLerp from 1 to the group's LerpAfter of age over <c>DurationTicks</c>) and is discarded past
/// its duration; a <c>Thought_MemorySocialCumulative</c> moves its opinionOffset 1 toward 0 each day of age (<c>ThoughtInterval</c>
/// at age 60000, the age then reset), counts at most its def's <c>maxCumulatedOpinionOffset</c> and is discarded at 0. Offset is
/// the opinionOffset, 0 while the thought is nullified.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SocialMemoryRead.Fading), "fading")]
[JsonDerivedType(typeof(SocialMemoryRead.Cumulative), "cumulative")]
public abstract record SocialMemoryRead
{
    private protected SocialMemoryRead() { }

    public sealed record Fading(double Offset, double Age, double DurationTicks) : SocialMemoryRead;

    public sealed record Cumulative(double Offset, double Age, double Cap) : SocialMemoryRead;
}
/// <summary>
/// One prisoner as the prison credit reads it (#3805): <c>Pawn_GuestTracker.Recruitable</c> (false for the unwaveringly loyal),
/// its <c>Resistance</c>, whether it owns a prison bed (<c>ownership.OwnedBed.ForPrisoners</c>), whether it is a wild man (recruited
/// by a taming chance instead, <c>AnimalOrWildMan</c>), its mood now (null without a mood need), its opinion of each warden
/// (<see cref="PrisonRecruitRead.Wardens"/> order) and what it would be as a colonist (<see cref="JoinerRead"/>).
/// <para>
/// The duplicated Recruitable and Resistance of the population census are read here again on purpose: this read is one capture
/// with the mood and opinions the rate is priced from.
/// </para>
/// </summary>
public sealed record PrisonerRecruitRead
{
    public required string Pawn { get; init; }
    public required bool Recruitable { get; init; }
    public required double Resistance { get; init; }
    public required bool PrisonBed { get; init; }
    public required bool WildMan { get; init; }
    public required PrisonerMoodRead? Mood { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<WardenOpinionRead>))]
    public required EquatableArray<WardenOpinionRead> Opinions { get; init; }
    /// <summary>The conversion it needs before it can be recruited; null when it needs none: Ideology is off or in classic mode, the colony has no primary ideoligion, or the prisoner holds none or the colony's.</summary>
    public required PrisonerConversionRead? Conversion { get; init; }
    public required JoinerRead Joiner { get; init; }
}
/// <summary>
/// A prisoner's conversion to the colony's primary ideoligion (#3915): the
/// <c>CertaintyChangeFactor</c> that scales every change to it (the native age curve under Biotech, else 1; a reduction multiplies by it, a
/// drift divides by it), and each warden who can convert it (<c>WorkGiver_Warden_Convert.JobOnThing</c>: a warden who holds the
/// primary ideoligion, <c>guest.ideoForConversion</c>, and can talk) with the certainty one of its attempts removes before the
/// factor (<c>InteractionWorker_ConvertIdeoAttempt.CertaintyReduction</c>: the warden's ConversionPower, the prisoner's
/// CertaintyLossFactor, the memes against the traits, relics and the difficulty). At 0 certainty it takes the warden's ideoligion.
/// </summary>
public sealed record PrisonerConversionRead(double ChangeFactor)
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<ConversionReductionRead>))]
    public required EquatableArray<ConversionReductionRead> Converters { get; init; }
}
/// <summary>One warden's <c>CertaintyReduction</c> on a prisoner (<see cref="PrisonerConversionRead"/>).</summary>
public sealed record ConversionReductionRead(string Warden, double Reduction);
/// <summary>
/// A prisoner's mood now (#3805, #3899): its unclamped instant mood (as <see cref="NeedMood.UnclampedTarget"/>: the base level
/// plus the thoughts' total and the difficulty's colonistMoodOffset, over 100; <c>Need_Mood.CurInstantLevel</c> clamps it), the
/// mood offset of its PrisonCell and PrisonBarracks thought groups now, those thoughts' offset at every stage
/// (<see cref="NeedThoughtStage"/>, roles <c>prison_cell</c> and <c>prison_barracks</c>; the stage is the room's Impressiveness
/// score stage, a null stage or an Ascetic prisoner a 0 row, as <c>ThoughtWorker_RoomImpressiveness</c> leaves the thought
/// inactive), and its memory groups (<see cref="MemoryGroupRead"/>), whose expiry and fading the recruitment's chats see.
/// </summary>
public sealed record PrisonerMoodRead(double UnclampedLevel, double CellThought)
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<NeedThoughtStage>))]
    public required EquatableArray<NeedThoughtStage> CellStages { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<MemoryGroupRead>))]
    public required EquatableArray<MemoryGroupRead> Memories { get; init; }
}
/// <summary>
/// One mood memory group (#3899, <c>Thought.GroupsWith</c>): its def's <c>stackedEffectMultiplier</c> and its memories
/// (<see cref="NeedMemory"/>: MoodOffset now, life and fading). <c>ThoughtHandler.MoodOffsetOfGroup</c> is the members' mean
/// offset times the stacked sum of the multiplier's powers over their count.
/// </summary>
public sealed record MemoryGroupRead(double StackedMultiplier)
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<NeedMemory>))]
    public required EquatableArray<NeedMemory> Members { get; init; }
}
/// <summary>
/// A pawn as it would be once it joins the colony (#3805; the joiner's mood model, shared by any pawn not yet a colonist): its
/// <see cref="PawnUpkeepRead"/> (its <see cref="PawnUpkeepRead.Mood"/> null: <see cref="Mood"/> holds what the joiner's mood is
/// built from), its mood parts (null without a mood need) and its food, rest and joy needs (<see cref="NeedRate"/>; a need it lacks
/// now that a colonist has, such as a prisoner's joy, read from a detached need of the pawn at the need's second threshold).
/// </summary>
public sealed record JoinerRead
{
    public required PawnUpkeepRead Upkeep { get; init; }
    public required JoinerMoodRead? Mood { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<NeedRate>))]
    public required EquatableArray<NeedRate> Needs { get; init; }
}
/// <summary>
/// What a joiner's mood is built from (#3805, #3899), its own part: the mood base level (<c>OverrideMoodBase</c> or the need's
/// baseLevel), the difficulty's colonistMoodOffset a colonist gets, its minor break threshold, its trait thoughts and its live
/// memories, which joining keeps (<c>RecruitUtility.Recruit</c> clears none), one row per memory group (<c>MoodOffsetOfGroup</c>'s
/// stacking over the memories alone). A trait thought is a situational non-social def whose requiredTraits the pawn has
/// (<c>ThoughtUtility.CanGetThought</c>'s trait clause) and its traits do not disallow. In <see cref="Traits"/>: a
/// <c>ThoughtWorker_AlwaysActive</c> one at its unattached thought's MoodOffset (stage 0, nullification included), and one whose
/// worker reads the pawn's own body or the map's head count, which joining leaves as they are (<c>ThoughtWorker_Pain</c>,
/// <c>ThoughtWorker_HasAddedBodyPart</c>, <c>ThoughtWorker_Recluse</c>), at the MoodOffset of its worker's state now (0 inactive).
/// In <see cref="RoomTraits"/>: the own-bedroom ones (<c>ThoughtWorker_Greedy</c>, <c>ThoughtWorker_BedroomJealous</c>,
/// <c>ThoughtWorker_Ascetic</c>, which give a non-colonist nothing), every stage priced as <see cref="NeedsForecastPawn.ThoughtStages"/>'
/// greedy, jealous and ascetic rows. Any other is named in <see cref="SituationalTraits"/>. The colony's part is the colonists'
/// (JoinerMoodRules).
/// </summary>
public sealed record JoinerMoodRead(double BaseLevel, double ColonistOffset, double Threshold)
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<ThoughtOffsetRead>))]
    public required EquatableArray<ThoughtOffsetRead> Traits { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<NeedThoughtStage>))]
    public required EquatableArray<NeedThoughtStage> RoomTraits { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> SituationalTraits { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<ThoughtOffsetRead>))]
    public required EquatableArray<ThoughtOffsetRead> Memories { get; init; }
}
/// <summary>A thought def's mood offset for one pawn.</summary>
public sealed record ThoughtOffsetRead(string Thought, double Offset);
/// <summary>
/// The deathrest defs (#3810): <c>Need_Deathrest.FallPerDay</c> and <c>GainPerDayDeathresting</c> (the need a day awake loses, and a
/// day of deathrest gains at efficiency 1), every building def with a <c>CompProperties_DeathrestBindable</c>, and the minScore of each
/// <c>RoomStatDefOf.Impressiveness</c> score stage: the DeathrestChamber thought's stage is that score stage + 1 for an indoor room, 0
/// outdoors (Toils_LayDown.UpdateDeathrestThoughtIndex).
/// </summary>
public sealed record DeathrestDefsRead(double FallPerDay, double GainPerDay,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<DeathrestBindableRead>))] EquatableArray<DeathrestBindableRead> Bindables,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<double>))] EquatableArray<double> ImpressivenessStages);
/// <summary>
/// One deathrest-bindable building def (#3810): its deathrestEffectivenessFactor, stackLimit (0 is none), countsTowardsBuildingLimit
/// (the deathrester's DeathrestCapacity bounds those) and mustBeLayingInToBind (the casket the deathrester lies in).
/// </summary>
public sealed record DeathrestBindableRead(string Def, double Factor, int StackLimit, bool CountsTowardsLimit, bool Casket);
/// <summary>
/// A deathrester (#3810): its Gene_Deathrest.DeathrestCapacity; the deathrest efficiency its bound buildings give
/// (Gene_Deathrest.DeathrestEfficiency's product of each spawned bound building's deathrestEffectivenessFactor above 0, whatever bed
/// it lies in now; 1 with none); and its chamberThoughtIndex, the DeathrestChamber stage its last deathrest left (-1 none).
/// </summary>
public sealed record DeathresterRead(int Capacity, double Efficiency, int ChamberStage);
/// <summary>
/// One corpse lying in sight (#3806): whether its event is ObservedLayingRottingCorpse (not fresh), whether its inner pawn was
/// the player's (a colonist corpse, which a tomb takes), and per colonist with a sighting the ticks before the read of each of
/// that colonist's observer passes that saw it (the native side replays PawnObserver.PossibleToObserve, within 5 cells in line of
/// sight, from the corpse's cell to each logged pass cell).
/// </summary>
public sealed record CorpseSightingRead(bool Rotting, bool Colonist,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<CorpseSightings>))] EquatableArray<CorpseSightings> Sightings);
/// <summary>One colonist's sightings of a corpse (#3806): the ticks before the read of each observer pass that saw it, oldest first.</summary>
public sealed record CorpseSightings(string Pawn, [property: JsonConverter(typeof(EquatableArrayJsonConverter<int>))] EquatableArray<int> TicksAgo);
/// <summary>
/// A memory the pawn's ideo gives it on an observed-corpse event (#3806): each precept comp
/// <c>PreceptComp_SelfTookMemoryThought</c> keyed by ObservedLayingCorpse (fresh) or ObservedLayingRottingCorpse (<paramref name="Rotting"/>),
/// its thought, the game's MoodOffset for this pawn (0 when ThoughtUtility.CanGetThought refuses it), its DurationTicks, stackLimit
/// (negative is none) and stackedEffectMultiplier.
/// </summary>
public sealed record CorpseThoughtRead(bool Rotting, string Thought, double Offset, double DurationTicks, int StackLimit, double StackedMultiplier);
/// <summary>
/// One research bench now (#3567, #3800): its thing id (the research activities' bench), its def, its Position cell (the
/// cell whose room its stat parts read) and its <c>StatDefOf.ResearchSpeedFactor</c> with every part applied.
/// </summary>
public sealed record ResearchBenchRead(string Bench, string Def, Cell Position, double Factor);
/// <summary>StatPart_EnvironmentalEffects fields (factorOffsetUnroofed, factorOffsetOutdoors, protectedByEdificeFactor), def data.</summary>
public sealed record NeedDeteriorationPart(double UnroofedOffset, double OutdoorsOffset, double ProtectedFactor);
/// <summary>
/// One cell's roof and room facts now (#3339): Roofed and the roof def's isThickRoof/isNatural, the cell room's
/// UsesOutdoorTemperature and PsychologicallyOutdoors (no room reads as outdoors, as BeautyUtility does).
/// </summary>
public sealed record NeedCell(bool Roofed, bool ThickRoof, bool NaturalRoof, bool OutdoorTemperature, bool PsychologicallyOutdoors);
/// <summary>
/// Where JobGiver_GetRest lays the pawn now and what Toils_LayDown gives it there (#3840): the bed RestUtility.FindBedFor
/// picks (its rest target id, the owned bed first), else the ground at the pawn's cell (TryFindGroundSleepSpotFor's first
/// choice). <see cref="Outside"/> is the sleeping cell's room PsychologicallyOutdoors (no room reads as outdoors) and
/// <see cref="Ground"/> ApplyBedThoughts' SleptOnGround test (no bed, or one whose CostListAdjusted is empty: a sleeping
/// spot). <see cref="GainPerTick"/> is the rest gained a tick lying there (Need_Rest.BaseRestGainPerTick times the rest
/// effectiveness LayDown's tick applies, the bed's BedRestEffectiveness or the stat's valueIfMissing for a spot or the
/// ground, times RestRateMultiplier), <see cref="Comfort"/> the comfort need's instant level lying there (the bed's Comfort
/// stat, 0 on the ground) and <see cref="Temperature"/> the ambient temperature ApplyBedThoughts compares.
/// </summary>
public sealed record NeedSleep(string Bed, bool Outside, bool Ground, double GainPerTick, double Comfort, double Temperature);
/// <summary>
/// The lie-down a pawn is in now (#3882). <see cref="Place"/> is read as <see cref="NeedsForecastPawn.Sleep"/> is, but at the
/// pawn's own cell and its CurrentBed (<see cref="NeedSleep.Bed"/> the bed's load id, "" on the ground): what Toils_LayDown's
/// tick gains there and what ApplyBedThoughts gives when the lie-down ends. RestUtility.ShouldWakeUp, which LayDown checks,
/// wakes the pawn once its rest reaches the wake threshold or at once while <see cref="HediffBlocksSleeping"/>
/// (HasHediffBlocksSleeping), and never while it deathrests (<see cref="Deathrest"/>, #3883).
/// </summary>
public sealed record NeedLyingIn(NeedSleep Place, bool HediffBlocksSleeping, LyingInDeathrest Deathrest)
{
    /// <summary>Toils_LayDown's chamber stage if the observed deathrest ended in its current room; -1 means no thought.
    /// Also -1 for a lie-down that is not a deathrest.</summary>
    public required int DeathrestWakeStage { get; init; }
}
/// <summary>
/// The deathrest the pawn lies in now (#3883), as Hediff_Deathrest ticks Gene_Deathrest.TickDeathresting. <see cref="None"/>: not
/// deathresting (Pawn.Deathresting, the Deathrest hediff). <see cref="Paused"/>: Hediff_Deathrest.Paused
/// (SanguophageUtility.ShouldBeDeathrestingOrInComaInsteadOfDead), so the deathrest neither advances nor checks sunlight and
/// lasts while the pawn's injuries do. <see cref="Wakes"/>: autoWake is set and the wake not yet notified, so the deathrest ends
/// (Gene_Deathrest.Wake) once deathrestTicks reaches MinDeathrestTicks, <c>InTicks</c> from now. <see cref="Held"/>: no
/// auto-wake, so it lasts until a player wakes the pawn. A deathrest that advances also ends when the bed touches sunlight
/// (<see cref="Sunlight"/>).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LyingInDeathrest.None), "none")]
[JsonDerivedType(typeof(LyingInDeathrest.Paused), "paused")]
[JsonDerivedType(typeof(LyingInDeathrest.Wakes), "wakes")]
[JsonDerivedType(typeof(LyingInDeathrest.Held), "held")]
public abstract record LyingInDeathrest
{
    private protected LyingInDeathrest() { }

    public sealed record None : LyingInDeathrest;

    public sealed record Paused : LyingInDeathrest;

    public sealed record Wakes(double InTicks, DeathrestSunlight Sunlight) : LyingInDeathrest;

    public sealed record Held(DeathrestSunlight Sunlight) : LyingInDeathrest;
}
/// <summary>
/// When an advancing deathrest's bed first touches sunlight (#3883). Gene_Deathrest.TickDeathresting wakes the pawn on its
/// 150-tick hash interval while a cell of its CurrentBed is in sunlight (IntVec3.InSunlight: unroofed and SkyManager.CurSkyGlow
/// above 0.1); a pawn on the ground never wakes so. <see cref="Shaded"/>: no bed, or every bed cell roofed now.
/// <see cref="Sunlit"/>: the bed's <c>Unroofed</c> cells now and the ticks to the first check whose sky glow is above 0.1: the
/// celestial glow capped by the current weather's maxGlow, interpolated from the previous weather through its transition
/// while the current weather lasts, then darkened by each game condition affecting the map while it lasts
/// (SkyManager's SkyTarget.LerpDarken at the condition's lerp factor). Unprojected sky effects are named in NotModelled. <see cref="Unlit"/>: no such check
/// within <c>ThroughTicks</c>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(DeathrestSunlight.Shaded), "shaded")]
[JsonDerivedType(typeof(DeathrestSunlight.Sunlit), "sunlit")]
[JsonDerivedType(typeof(DeathrestSunlight.Unlit), "unlit")]
public abstract record DeathrestSunlight
{
    /// <summary>The specific sky effects the native scan cannot project.</summary>
    public EquatableArray<string> NotModelled { get; init; }
    private protected DeathrestSunlight() { }

    public sealed record Shaded : DeathrestSunlight;

    public sealed record Sunlit([property: JsonConverter(typeof(EquatableArrayJsonConverter<Cell>))] EquatableArray<Cell> Unroofed, double InTicks) : DeathrestSunlight;

    public sealed record Unlit(double ThroughTicks) : DeathrestSunlight;
}
/// <summary>
/// When the pawn's current lie-down gives its bed thoughts (#3871). Toils_LayDown's init draws mindState.applyBedThoughtsTick =
/// now + Rand.Range(2500, 10000) when it is 0; ApplyBedRelatedEffects gives the thoughts once that tick passes and sets
/// applyBedThoughtsOnLeave, so FinalizeLayingJob gives them again when the pawn gets up; Pawn_MindState resets the tick to 0
/// whenever the pawn is not laying. <see cref="Given"/>: given in this lie-down, so given again when it ends; <see cref="Due"/>:
/// given once the pawn has lain <c>InTicks</c> more; <see cref="None"/>: no onset drawn (the tick is 0), so none before the
/// pawn lies down again.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(BedThoughtOnset.Given), "given")]
[JsonDerivedType(typeof(BedThoughtOnset.Due), "due")]
[JsonDerivedType(typeof(BedThoughtOnset.None), "none")]
public abstract record BedThoughtOnset
{
    private protected BedThoughtOnset() { }

    public sealed record Given : BedThoughtOnset;

    public sealed record Due(double InTicks) : BedThoughtOnset;

    public sealed record None : BedThoughtOnset;
}
/// <summary>A bed's room now (#3339): its RoomRoleDef ("" none), its Impressiveness score stage and ForPrisoners.</summary>
public sealed record NeedBedRoom(string Role, int Stage, bool ForPrisoners)
{
    /// <summary>
    /// The care a patient lying in the bed gets from it now (#3804): whether the bed is costless (CostListAdjusted empty, a sleeping
    /// spot: ApplyBedThoughts gives SleptOnGround), its room's PsychologicallyOutdoors, RoomStatDefOf.InfectionChanceFactor
    /// (HediffComp_Infecter.CompTended stores it), and the bed's own StatDefOf.SurgerySuccessChanceFactor and
    /// StatDefOf.ImmunityGainSpeedFactor (the SurgeryOutcomeComp_BedAndRoomQuality and StatPart_BedStat reads: the bed's def, its
    /// facilities and its room's cleanliness factor).
    /// </summary>
    public required bool Costless { get; init; }
    public required bool PsychologicallyOutdoors { get; init; }
    public required double InfectionChanceFactor { get; init; }
    public required double SurgerySuccessFactor { get; init; }
    public required double ImmunityGainSpeedFactor { get; init; }
}
/// <summary>A pawn's own room (#3899): its <c>RoomStatDefOf.Impressiveness</c> and that stat's score stage.</summary>
public sealed record NeedOwnedRoom(double Impressiveness, int Stage);
/// <summary>
/// A food stack's deterioration inputs (#3339): its HitPoints, SteadyEnvironmentEffects.FinalDeteriorationRate now (every
/// DeteriorationRate part applied at its cell), whether StatPart_EnvironmentalEffects applies (deteriorateFromEnvironmentalEffects),
/// the cell terrain's extraDeteriorationFactor and SteadyEnvironmentEffects.ProtectedByEdifice.
/// </summary>
public sealed record NeedDeterioration(double HitPoints, double RatePerDay, bool Environmental, double TerrainFactor, bool Protected);
/// <summary>How long a thought lasts once the pawn has it (#3781): a memory's duration and whether it fades, or a situational thought, which lasts while its condition holds.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ThoughtLasting.Memory), "memory")]
[JsonDerivedType(typeof(ThoughtLasting.Situational), "situational")]
public abstract record ThoughtLasting
{
    private protected ThoughtLasting() { }

    /// <summary>A memory thought: its def's DurationTicks, whether its offset fades to zero over them (lerpMoodToZero), and its stackLimit.</summary>
    public sealed record Memory(double DurationTicks, bool Fades, int StackLimit) : ThoughtLasting;

    public sealed record Situational : ThoughtLasting;
}
/// <summary>
/// A thought this pawn can get (ThoughtUtility.CanGetThought) at one stage, priced by the game's own MoodOffset for this
/// pawn (#3339). Role names why it is read: slept_outside, slept_in_bedroom, slept_in_barracks, slept_on_ground,
/// slept_in_cold and slept_in_heat (the other bed memories ApplyBedThoughts gives, #3840), weather, need_outdoors,
/// undergrounder_indoors (constructed roof), undergrounder_underground (natural roof), undergrounder_outdoors, ascetic
/// (ThoughtWorker_Ascetic's own-room thought by Impressiveness stage, #3655), and the title room thoughts a throne room or
/// suite relieves (#3807): title_bedroom_unmet, title_no_bedroom, title_throne_unmet, title_no_throne; and the trait bedroom
/// thoughts a suite relieves (#3826): greedy (ThoughtWorker_Greedy, staged by the owned room's Impressiveness stage + 1, 0 with
/// none) and jealous (ThoughtWorker_BedroomJealous). A null stage of a bed, ascetic or greedy thought,
/// where the game gives none (Toils_LayDown.ApplyBedThoughts at SleptInBedroom's dull stage, #3822; the ascetic or greedy
/// worker inactive), is a row at 0. Then apparel_damaged, naked, dead_mans_apparel, and the ideoligion's apparel precept thoughts by
/// worker (#3816): groin_uncovered, groin_or_chest_uncovered, groin_chest_or_hair_uncovered, groin_chest_hair_or_face_uncovered,
/// any_body_part_covered, any_body_part_but_groin_covered, any_body_part_but_hair_or_face_covered, each read only when
/// ThoughtWorker_Precept's ideoligion gate lets the pawn have it. Then the required-apparel thoughts (#3836):
/// title_apparel_unmet (ThoughtWorker_RoyalTitleApparelRequirementNotMet) and role_apparel_unmet
/// (ThoughtDefOf.IdeoRoleApparelRequirementNotMet), each active while one of the pawn's <see cref="NeedApparel.Requirements"/>
/// of its role is unmet; and title_apparel_quality_unmet (ThoughtWorker_RoyalTitleApparelMinQualityNotMet, #3841), active while
/// a worn garment that has a quality is below <see cref="NeedApparel.MinimumQuality"/>. Then need_comfort (ThoughtWorker_NeedComfort,
/// read only for a pawn with a comfort need, #3840), the comfort a bed gives over the day. Then the room memories a planned
/// dining or rec room gives (#3803): ate_in_dining_room (AteInImpressiveDiningRoom) and joy_in_rec_room
/// (JoyActivityInImpressiveRecRoom) by the Impressiveness stage of the room a meal or joy activity ends in, each null stage a
/// row at 0, and ate_without_table (AteWithoutTable). A stage the game cannot price is left out.
/// </summary>
public sealed record NeedThoughtStage(string Thought, string Role, int Stage, double Offset, ThoughtLasting Lasting);
/// <summary>
/// The pawn facts the apparel thought workers read beside the worn apparel (#3816): pawn.gender, whether any not-missing
/// body part is in the Legs group (a legless pawn counts as wearing pants), whether GenTemperature.SafeTemperatureRange
/// includes pawn.AmbientTemperature, and the kind's apparelRequired defs (clothing the pawn kind requires does not count
/// against the covering-disapproved precepts).
/// </summary>
public sealed record NeedApparel(RimGovernor.Brain.GameFacts.Defs.Gender Gender, bool Legs, bool SafeAmbient)
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> KindRequired { get; init; }
    /// <summary>
    /// The pawn's active apparel requirements whose unmet thought it can have (#3836): its royal titles' in effect while
    /// pawn.royalty.allowApparelRequirements, and its ideoligion role's while the role is active and the pawn a free non-slave
    /// colonist who is no quest lodger; each IsActive (Pawn_ApparelTracker.ActiveRequirementsForReading). Empty with no title
    /// and no role, or without Royalty and Ideology.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<NeedApparelRequirement>))]
    public required EquatableArray<NeedApparelRequirement> Requirements { get; init; }
    /// <summary>
    /// The highest RoyalTitleDef.requiredMinimumApparelQuality of the pawn's titles in effect (#3841), the quality
    /// ThoughtWorker_RoyalTitleApparelMinQualityNotMet holds every worn garment that has one to; Awful, which no garment is
    /// below, with no title in effect or without Royalty.
    /// </summary>
    public required RimGovernor.Brain.GameFacts.Defs.QualityCategory MinimumQuality { get; init; }
}
/// <summary>
/// One active apparel requirement (#3836): the stage-row role of the thought it raises while unmet (title_apparel_unmet or
/// role_apparel_unmet), and every apparel def whose wearing meets it (ApparelRequirement.ApparelMeetsRequirement with
/// allowUnmatched false, the test ApparelRequirement.IsMet applies to each worn garment).
/// </summary>
public sealed record NeedApparelRequirement(string Thought,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<string>))] EquatableArray<string> Meets);
/// <summary>One need's level, its fall a tick and its threshold (food: hungry, rest: tired, joy: the second threshold), at current conditions.</summary>
public sealed record NeedRate(string Kind, double Level, double FallPerTick, double Threshold)
{
    /// <summary>
    /// The need's gain per tick with no service object: resting on the ground when the colony has no usable bed
    /// (<c>StatDefOf.BedRestEffectiveness.valueIfMissing</c>, as <c>Toils_LayDown</c> applies it), and for joy the best of going for a walk
    /// and skygazing (#3825), taken only while <c>JoyUtility.EnjoyableOutsideNow</c> holds (<see cref="OutdoorShareByTwelfth"/>).
    /// 0 for food: hunger never recovers without eating.
    /// </summary>
    public required double UnservedGainPerTick { get; init; }
    /// <summary>
    /// For joy, the expected share of each twelfth of the coming year, indexed by twelfth (the current one from now to its end), in
    /// which <c>JoyUtility.EnjoyableOutsideNow</c> holds for the pawn and so a walk or skygazing can serve it (#3825 D7): the hours whose
    /// outdoor temperature lies in the pawn's comfortable range, each at the chance that no rain of 0.25 or more (the biome's weather
    /// commonalities at that temperature, weighted by duration) and no active game condition rules it out; all 0 on a vacuum
    /// biome for a pawn concerned by vacuum. Twelve entries for joy, empty for any other need.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<double>))]
    public EquatableArray<double> OutdoorShareByTwelfth { get; init; }
    /// <summary>
    /// The rest need's gain per tick per unit of rest effectiveness (#3840): <c>Need_Rest.BaseRestGainPerTick</c> times the pawn's
    /// <c>StatDefOf.RestRateMultiplier</c>, which <c>Need_Rest.TickResting</c> multiplies by the bed's effectiveness. 0 for any other need.
    /// </summary>
    public required double GainPerTickPerEffectiveness { get; init; }
    /// <summary>Current-rate approximation; observed threshold crossings require fresh native rates.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> Unsupported { get; init; }
}
/// <summary>How long a memory has left (#3781): a permanent memory never expires; any other expires after its remaining ticks.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(MemoryLife.Permanent), "permanent")]
[JsonDerivedType(typeof(MemoryLife.Expires), "expires")]
public abstract record MemoryLife
{
    private protected MemoryLife() { }

    public sealed record Permanent : MemoryLife;

    /// <summary>Thought_Memory.DurationTicks less its age, at least 0.</summary>
    public sealed record Expires(double RemainingTicks) : MemoryLife;
}
/// <summary>
/// A memory: its def, the game's MoodOffset for the pawn, how long it has left and whether it fades over that time
/// (lerpMoodToZero). StackLimit is the def's stackLimit (ThoughtDef.stackLimit): at 1 a new memory of the def renews the existing
/// one (Thought_Memory.TryMergeWithExistingMemory) instead of adding to it (#3598).
/// </summary>
public sealed record NeedMemory(string Id, double Offset, MemoryLife Life, bool Fades)
{
    public required int StackLimit { get; init; }
}
/// <summary>Native mood-point contribution in a half-open local-hour window; EndHour less than StartHour wraps midnight. CurrentOffset is already present in UnclampedTarget.</summary>
public sealed record NeedConditionalThought(double StartHour, double EndHour, bool AwakeOnly,
    double Offset, double CurrentOffset);
/// <summary>
/// The hour-conditional thoughts the pawn can get (#3781): every one modeled (the verified night owl workers), or the defs of
/// those whose worker or definition the forecast does not model, when it does not forecast the pawn's mood.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ConditionalMood.Modeled), "modeled")]
[JsonDerivedType(typeof(ConditionalMood.Unmodeled), "unmodeled")]
public abstract record ConditionalMood
{
    private protected ConditionalMood() { }

    public sealed record Modeled(
        [property: JsonConverter(typeof(EquatableArrayJsonConverter<NeedConditionalThought>))] EquatableArray<NeedConditionalThought> Thoughts) : ConditionalMood;

    public sealed record Unmodeled(
        [property: JsonConverter(typeof(EquatableArrayJsonConverter<string>))] EquatableArray<string> Thoughts) : ConditionalMood;
}
/// <summary>
/// A pawn's mood need now (#3781): Need_Mood.CurLevelPercentage, its CurInstantLevel target, the unclamped target in normalized
/// mood units (including each conditional thought's CurrentOffset once), Need_Seeker's rise and fall a tick (def.seeker{Rise,Fall}PerHour),
/// whether it freezes while asleep, the mental breaker's minor threshold, its hour-conditional thoughts and its live memories.
/// </summary>
public sealed record NeedMood
{
    public required double Level { get; init; }
    public required double Target { get; init; }
    public required double UnclampedTarget { get; init; }
    public required double RisePerTick { get; init; }
    public required double FallPerTick { get; init; }
    public required bool FreezesWhileAsleep { get; init; }
    public required double BreakThreshold { get; init; }
    public required ConditionalMood Conditional { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<NeedMemory>))]
    public required EquatableArray<NeedMemory> Memories { get; init; }
}
/// <summary>Exact observed continuous service. RemainingTicks is a current-mechanics projection,
/// not a release guarantee; journal ownership still requires observed continuation/release.</summary>
public sealed record NeedCurrentService(string Assignment, string Method, long Revision, string Target,
    string Kind, Cell Location, double RemainingTicks, double GainPerTick)
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> Unsupported { get; init; }
}
public sealed record NeedsForecastPawn
{
    public required string Pawn { get; init; }
    /// <summary>eligible, unsupported_voluntary_state, dead, downed, mental_state, drafted, player_authority or player_work.</summary>
    public required string State { get; init; }
    public required Cell Location { get; init; }
    /// <summary>Null means no supported observed continuous incumbent, not a free pawn.</summary>
    public required NeedCurrentService? CurrentService { get; init; }
    /// <summary>Observed requirements outside supported voluntary control. Empty means none observed;
    /// a missing need census has an explicit blocker. Passive consequence uncertainty stays in Unsupported.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> VoluntaryControlBlockers { get; init; }
    /// <summary>GenLocalDate.HourFloat at the pawn.</summary>
    public required double LocalHour { get; init; }
    public required bool Awake { get; init; }
    /// <summary>When the current lie-down gives the bed thoughts (<see cref="BedThoughtOnset"/>, #3871); the forecast reads it for a
    /// pawn asleep now, whose observed sleep continues.</summary>
    public required BedThoughtOnset BedThoughts { get; init; }
    /// <summary>RestUtility's FallAsleepMaxLevel and WakeThreshold for the pawn.</summary>
    public required double SleepThreshold { get; init; }
    public required double WakeThreshold { get; init; }
    /// <summary>A hediff blocks sleeping or the life stage cannot sleep voluntarily: no recovery bound is read, so the forecast plans no sleep.</summary>
    public required bool SleepBlocked { get; init; }
    /// <summary>The ticks before the pawn may fall asleep after its last disturbance (400 after mindState.lastDisturbanceTick), at least 0.</summary>
    public required double SleepBlockedUntilTicks { get; init; }
    public required bool SleepWhileStarving { get; init; }
    /// <summary>The pawn's mood need; null for a pawn without one.</summary>
    public required NeedMood? Mood { get; init; }
    public required double ComfortableMin { get; init; }
    public required double ComfortableMax { get; init; }
    /// <summary>
    /// The comfortable range ApplyBedThoughts compares the sleeping ambient temperature with (#3840): the race's
    /// <c>ComfyTemperatureMin</c>/<c>Max</c> (<c>def.GetStatValueAbstract</c>, no apparel) plus each active gene's offsets under Biotech;
    /// below it the pawn gains SleptInCold, above it SleptInHeat.
    /// </summary>
    public required double SleepComfortableMin { get; init; }
    public required double SleepComfortableMax { get; init; }
    /// <summary>Where the pawn sleeps now (#3840); null for a pawn without a rest need, a wild man or one not spawned.</summary>
    public required NeedSleep? Sleep { get; init; }
    /// <summary>The lie-down the pawn is in now (#3882, <see cref="NeedLyingIn"/>); null when the pawn is not laying.</summary>
    public required NeedLyingIn? LyingIn { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<NeedRate>))]
    public required EquatableArray<NeedRate> Needs { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<NeedTarget>))]
    public required EquatableArray<NeedTarget> Targets { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> Unsupported { get; init; }
    /// <summary>Need_Outdoors.CurLevel, or Lacks without the need. Disabled (PrefersIndoors) holds it at 1 (#3339).</summary>
    public required NeedLevel Outdoors { get; init; }
    public required bool OutdoorsDisabled { get; init; }
    /// <summary>ownership.OwnedBed's load id ("" none) and the Ascetic trait, the two pawn facts ApplyBedThoughts reads (#3339).</summary>
    public required string OwnedBed { get; init; }
    /// <summary>The owned bed's room and care (#3804, <see cref="NeedBedRoom"/>); null without an owned bed.</summary>
    public required NeedBedRoom? OwnedBedRoom { get; init; }
    /// <summary>
    /// The pawn's own room (#3899, <c>Pawn_Ownership.OwnedRoom</c>: its owned bed's room when the room counts it among its owners),
    /// as the own-bedroom trait thoughts read it (ThoughtWorker_Greedy, ThoughtWorker_BedroomJealous, ThoughtWorker_Ascetic); null
    /// when it owns none.
    /// </summary>
    public required NeedOwnedRoom? OwnedRoom { get; init; }
    public required bool Ascetic { get; init; }
    /// <summary>
    /// The Impressiveness of each room another pawn owns, ascending, as ThoughtWorker_BedroomJealous compares the pawn's own
    /// room against them (#3826): every spawned player-faction pawn but this one without a host faction, by its OwnedRoom,
    /// each room once. Empty when no other pawn owns a room.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<double>))]
    public required EquatableArray<double> RivalRooms { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<NeedThoughtStage>))]
    public required EquatableArray<NeedThoughtStage> ThoughtStages { get; init; }
    /// <summary>The apparel thought workers' pawn facts (#3816); null without an apparel tracker.</summary>
    public required NeedApparel? Apparel { get; init; }
    /// <summary>pawn.health.capacities.GetLevel of the five capacities a pawn-day is counted in (#3519).</summary>
    public required double Consciousness { get; init; }
    public required double Moving { get; init; }
    public required double Manipulation { get; init; }
    public required double Sight { get; init; }
    public required double Talking { get; init; }
    /// <summary>The current severity of each hediff the capacity penalty tables decode; 0 when the pawn does not have it (#3519).</summary>
    public required double MalnutritionSeverity { get; init; }
    public required double FoodPoisoningSeverity { get; init; }
    public required double HypothermiaSeverity { get; init; }
    public required double HeatstrokeSeverity { get; init; }
    /// <summary>The health reads of a raid's consequences (#3544): StatDefOf.PainShockThreshold, pawn.HealthScale, StatDefOf.IncomingDamageFactor and StatDefOf.InjuryHealingFactor.</summary>
    public required double PainShockThreshold { get; init; }
    public required double HealthScale { get; init; }
    public required double IncomingDamageFactor { get; init; }
    public required double InjuryHealingFactor { get; init; }
    /// <summary>
    /// The pawn's work speeds whatever it is doing (#3567): <c>StatDefOf.ResearchSpeed</c> times the difficulty's researchSpeedFactor (the
    /// points a tick at bench factor 1 per <c>ResearchManager.ResearchPerformed</c>'s 0.00825) and <c>StatDefOf.PlantWorkSpeed</c> (sow work per tick).
    /// </summary>
    public required double ResearchSpeed { get; init; }
    public required double PlantWorkSpeed { get; init; }
    /// <summary>The pawn's illness reads (#3824).</summary>
    public required PawnDiseaseFacts Disease { get; init; }
    /// <summary>The pawn's raid part-loss reads (#3847).</summary>
    public required PawnPartLossFacts PartLoss { get; init; }
    /// <summary>The memories the pawn's ideo gives it on seeing a corpse lying (#3806); empty when none would (no ideo, no such precept, no mood).</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<CorpseThoughtRead>))]
    public required EquatableArray<CorpseThoughtRead> CorpseThoughts { get; init; }
    /// <summary>The ticks before the read the pawn's sighting record covers (#3806): since its first observer pass, at most two days; 0 with no record.</summary>
    public required int SightingTicks { get; init; }
    /// <summary>What the pawn's value reads of it (#3825), every number plain.</summary>
    public required PawnUpkeepRead Upkeep { get; init; }
    /// <summary>The pawn's deathrest (#3810); null when it has no deathrest gene.</summary>
    public required DeathresterRead? Deathrest { get; init; }
}
/// <summary>
/// What a pawn's value (<c>PawnValueRules</c>, #3825) reads of one pawn: its work speed at each stat a job driver or recipe the
/// catalog maps applies and its MoveSpeed and CarryingCapacity, which its food's hauling walks and carries at (#3849;
/// <c>DefinitionCatalog.UpkeepStatNames</c>; 0 where <c>StatWorker.IsDisabledFor</c> the pawn), the work types it
/// is incapable of (<c>Pawn.WorkTypeIsDisabled</c>), the nutrition it eats a day (<c>Need_Food.FoodFallPerTickAssumingCategory</c> at
/// Fed, a day of ticks; 0 without a food need) and its mood (null without a mood need: it never breaks).
/// </summary>
public sealed record PawnUpkeepRead
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<WorkSpeedRead>))]
    public required EquatableArray<WorkSpeedRead> Speeds { get; init; }
    /// <summary>The speed at each stat that reads one of the pawn's skills with that skill one stored level higher (#3912; NativeSkillStat.OneLevelUp): what one more level adds to the pawn's work. A stat no skill of the pawn reads has no row.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<SkillSpeedRead>))]
    public required EquatableArray<SkillSpeedRead> SkillSpeeds { get; init; }
    /// <summary>Future integer-age work states for a child, with the observed native aging rates. Null for a pawn outside childhood.</summary>
    public required ChildWorkRead? ChildWork { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> IncapableWork { get; init; }
    public required double FoodPerDay { get; init; }
    public required PawnMoodRead? Mood { get; init; }
}
/// <summary>A pawn's value of one work speed stat (<c>Pawn.GetStatValue</c>).</summary>
public sealed record WorkSpeedRead(string Stat, double Value);
/// <summary>A pawn's value of a work speed stat with one skill one stored level higher (#3912); 0 where the stat is disabled for it.</summary>
public sealed record SkillSpeedRead(string Skill, string Stat, double OneLevelUp);
/// <summary>Read-only age counterfactuals; health, genes, equipment and non-age work restrictions are held observed.</summary>
public sealed record ChildWorkRead(double BiologicalYears, double ChildAgingRate, double AdultAgingRate, double GeneAgingFactor,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<AgeWorkRead>))] EquatableArray<AgeWorkRead> Ages);
/// <summary>Work speeds and restrictions from this integer biological age until the next row. The last row is age-invariant.</summary>
public sealed record AgeWorkRead(int BiologicalYears,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<WorkSpeedRead>))] EquatableArray<WorkSpeedRead> Speeds,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<SkillSpeedRead>))] EquatableArray<SkillSpeedRead> SkillSpeeds,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<string>))] EquatableArray<string> IncapableWork);
/// <summary>The pawn's unclamped mood target (as <see cref="NeedMood.UnclampedTarget"/>) and its minor break threshold (<c>MentalBreaker.BreakThresholdMinor</c>).</summary>
public sealed record PawnMoodRead(double Target, double Threshold);
/// <summary>
/// What a disease incident's course and victim draw read of one pawn (#3824): <c>StatDefOf.ImmunityGainSpeed</c> without the bed it lies
/// in (StatPart_BedStat's CurrentBed ImmunityGainSpeedFactor divided out, #3804: the forecast multiplies the bed it is cared in), and per DiseaseHuman
/// incident the chance it takes hold: <c>immunity.DiseaseContractChanceFactor</c> of its hediff for a potential victim, 0 for a pawn
/// <c>IncidentWorker_Disease.PotentialVictims</c> leaves out.
/// <para>
/// What its organ loss reads (#3830): the BloodPumping, Breathing and BloodFiltration levels the consciousness and moving workers
/// lerp in, every body part of a def some DiseaseHuman incident affects (<see cref="DiseasePartRead"/>), and per part def a
/// HediffComp_DestroyOrgan disease affects, each install recipe that would replace that part once missing
/// (<see cref="OrganReplacementRead"/>). A creepjoiner's read adds the parts and replacements of its downsides' triggered
/// hediffs (#3844, the organ decay downside).
/// </para>
/// </summary>
public sealed record PawnDiseaseFacts
{
    public required double ImmunityGainSpeed { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<DiseaseContract>))]
    public required EquatableArray<DiseaseContract> Contracts { get; init; }
    public required double BloodPumping { get; init; }
    public required double Breathing { get; init; }
    public required double BloodFiltration { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<DiseasePartRead>))]
    public required EquatableArray<DiseasePartRead> Parts { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<OrganReplacementRead>))]
    public required EquatableArray<OrganReplacementRead> Replacements { get; init; }
}
public sealed record DiseaseContract(string Incident, double Chance);
/// <summary>How a need target restores its need (#3781): eating a stack (the ingest duration and the need a unit restores) or a service a tick (resting or recreation).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NeedRecovery.Ingest), "ingest")]
[JsonDerivedType(typeof(NeedRecovery.Rate), "rate")]
public abstract record NeedRecovery
{
    private protected NeedRecovery() { }

    /// <summary>baseIngestTicks over the eating speed, and NutritionForEater a unit over the food need's MaxLevel.</summary>
    public sealed record Ingest(double ServiceTicks, double GainPerUnit) : NeedRecovery;

    /// <summary>The need gained a tick of service at current tolerance.</summary>
    public sealed record Rate(double GainPerTick) : NeedRecovery;
}
/// <summary>
/// What a raid's part loss reads of one pawn (#3847): the chance a hit whose part loss would kill is let through
/// (DamageWorker_AddInjury.FinalizeAndAddInjury's instant-kill protection: <c>Difficulty.allowInstantKillChance</c> for a colonist, 1
/// for any other pawn), every body part a hit can land on (<see cref="BodyPartLossRead"/>), and per def of a destroyable part whose loss
/// does not kill, each install recipe that would replace it once missing (<see cref="OrganReplacementRead"/>, read as the illness's are).
/// </summary>
public sealed record PawnPartLossFacts
{
    public double InstantKillChance { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<BodyPartLossRead>))]
    public EquatableArray<BodyPartLossRead> Parts { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<OrganReplacementRead>))]
    public EquatableArray<OrganReplacementRead> Replacements { get; init; }
}
/// <summary>
/// One not-missing body part with a coverageAbs above 0 (#3847), in <c>BodyDef.AllParts</c> order: its def, coverageAbs, the apparel
/// body-part groups it sits in, whether its depth is Outside, whether it is the body's core part, the row of its parent (-1 for none or
/// one not read), <c>HediffSet.GetPartHealth</c> and <c>BodyPartDef.GetMaxHealth</c>, <c>BodyPartDef.destroyableByDamage</c>, whether
/// its loss kills (<c>Pawn_HealthTracker.WouldDieAfterAddingHediff</c> of MissingBodyPart), and the Consciousness, Moving, Manipulation,
/// Sight and Talking levels with it and every part under it missing (<c>PawnCapacityUtility.CalculateCapacityLevel</c> over a copy of the
/// hediff set, the missing parts no longer fresh).
/// </summary>
public sealed record BodyPartLossRead(string Part, double Coverage)
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Groups { get; init; }
    public bool Outside { get; init; }
    public bool Core { get; init; }
    public int Parent { get; init; }
    public double Health { get; init; }
    public double MaxHealth { get; init; }
    public bool Destroyable { get; init; }
    public bool Kills { get; init; }
    public double Consciousness { get; init; }
    public double Moving { get; init; }
    public double Manipulation { get; init; }
    public double Sight { get; init; }
    public double Talking { get; init; }
}
/// <summary>
/// One body part of a def a DiseaseHuman incident affects (#3830): its def, coverageAbs,
/// <c>PawnCapacityUtility.CalculatePartEfficiency</c> (0 when missing), and the incidents whose hediff HediffGiverUtility.TryApply
/// could give it (not missing, not already carrying the hediff, no directly added part on it or an ancestor, whatever the hediff's
/// canAffectBionicOrImplant). On a creepjoiner it also lists every part of a def a downside's triggered hediff affects
/// (HediffCompProperties_ReplaceHediff.TriggeredHediff.partsToAffect, #3844), with the triggered hediffs TryApply could give it
/// by the same test.
/// </summary>
public sealed record DiseasePartRead(string Part, double Coverage, double Efficiency)
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> Incidents { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> Hediffs { get; init; }
}
/// <summary>
/// One install recipe that would replace a missing part of <see cref="Part"/>'s def (#3830), read as the surgery block reads an
/// operation (NativePawnDetails.Surgery): the part it installs (<see cref="PartThing"/>, its one fixed ingredient other than
/// medicine; null for a recipe that takes none) and whether that part is on the map (<c>RecipeDef.PotentiallyMissingIngredients</c>),
/// whether medicine the patient's care allows is on the map and how many units the recipe takes (#3868: the stock-arrival model
/// waits on what is missing), the eligible doctors, the best doctor's
/// success chance in the colony's best medical bed and with an ideal bed (0 without a doctor or an outcome effect: the surgery block
/// leaves those unread, which the autopilot refuses), whether it
/// is a violation or lethal; and its outcome model as a queued bill's (NativeSupportActivities.SurgeryOutcomesOf: the outcomes,
/// whether the bed and room comp applies, the clamp), with the best eligible doctor's quality before the bed and the clamp.
/// </summary>
public sealed record OrganReplacementRead(string Part, string Recipe)
{
    public string? PartThing { get; init; }
    public bool PartOnMap { get; init; }
    public bool MedicineOnMap { get; init; }
    public int MedicineCount { get; init; }
    public int EligibleDoctors { get; init; }
    public double SuccessChance { get; init; }
    public double DoctorSuccessChance { get; init; }
    public bool Violation { get; init; }
    public bool Lethal { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<SurgeryOutcomeRead>))]
    public EquatableArray<SurgeryOutcomeRead> Outcomes { get; init; }
    public bool Environment { get; init; }
    public double QualityMin { get; init; }
    public double QualityMax { get; init; }
    public double Quality { get; init; }
}
/// <summary>Native legality and mechanics, not a selected need action. Travel is an observed route cost.</summary>
public sealed record NeedTarget(string Id, string Kind, string Resource, int AvailableCount, double TravelTicks, NeedRecovery Recovery)
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<NeedMemory>))]
    public required EquatableArray<NeedMemory> MealThoughts { get; init; }
    public required Cell Location { get; init; }
    /// <summary>The stack claims sum against (vanilla ingest reserves a stack by count) and how long it keeps; a rest or recreation target is not a stack.</summary>
    public required NeedStock Stock { get; init; }
    /// <summary>Simultaneous claimants vanilla lets reserve this target (ReservationManager maxPawns), as the service job reserves it
    /// (NativeNeedsForecast.ServiceReservation); at least 1. Claims on one target must share one value.</summary>
    public required int MaxPawns { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> Unsupported { get; init; }
    /// <summary>The target cell's roof and room now (#3339).</summary>
    public required NeedCell Cell { get; init; }
    /// <summary>A rest target's room role and stage (#3339); null for a food or recreation target.</summary>
    public required NeedBedRoom? Bed { get; init; }
    /// <summary>A food target's deterioration inputs (#3339); null when it cannot deteriorate.</summary>
    public required NeedDeterioration? Deterioration { get; init; }
}
/// <summary>What a need target is as a stock (#3895): a food stack with the count claims sum against and how long it keeps, or a bed or
/// recreation building, which no claim takes units from.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NeedStock.Stacked), "stacked")]
[JsonDerivedType(typeof(NeedStock.NotStacked), "not_stacked")]
public abstract record NeedStock
{
    private protected NeedStock() { }

    /// <summary>The stack's count now, and its freshness at the current temperature.</summary>
    public sealed record Stacked(int Count, FoodFreshness Freshness) : NeedStock;

    /// <summary>A bed or a recreation building.</summary>
    public sealed record NotStacked : NeedStock;
}
