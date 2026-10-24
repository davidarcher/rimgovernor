using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Contracts.Wire.Observations;

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
}
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
/// read only for a pawn with a comfort need, #3840), the comfort a bed gives over the day. A stage the game cannot price is left out.
/// </summary>
public sealed record NeedThoughtStage(string Thought, string Role, int Stage, double Offset, ThoughtLasting Lasting);
/// <summary>
/// The pawn facts the apparel thought workers read beside the worn apparel (#3816): pawn.gender, whether any not-missing
/// body part is in the Legs group (a legless pawn counts as wearing pants), whether GenTemperature.SafeTemperatureRange
/// includes pawn.AmbientTemperature, and the kind's apparelRequired defs (clothing the pawn kind requires does not count
/// against the covering-disapproved precepts).
/// </summary>
public sealed record NeedApparel(Defs.Gender Gender, bool Legs, bool SafeAmbient)
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
    public required Defs.QualityCategory MinimumQuality { get; init; }
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
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> IncapableWork { get; init; }
    public required double FoodPerDay { get; init; }
    public required PawnMoodRead? Mood { get; init; }
}
/// <summary>A pawn's value of one work speed stat (<c>Pawn.GetStatValue</c>).</summary>
public sealed record WorkSpeedRead(string Stat, double Value);
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
/// (<see cref="OrganReplacementRead"/>).
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
/// canAffectBionicOrImplant).
/// </summary>
public sealed record DiseasePartRead(string Part, double Coverage, double Efficiency)
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> Incidents { get; init; }
}
/// <summary>
/// One install recipe that would replace a missing part of <see cref="Part"/>'s def (#3830), read as the surgery block reads an
/// operation (NativePawnDetails.Surgery): whether its part and medicine are on the map, the eligible doctors, the best doctor's
/// success chance in the colony's best medical bed and with an ideal bed (0 without a doctor or an outcome effect: the surgery block
/// leaves those unread, which the autopilot refuses), whether it
/// is a violation or lethal; and its outcome model as a queued bill's (NativeSupportActivities.SurgeryOutcomesOf: the outcomes,
/// whether the bed and room comp applies, the clamp), with the best eligible doctor's quality before the bed and the clamp.
/// </summary>
public sealed record OrganReplacementRead(string Part, string Recipe)
{
    public bool IngredientsOnMap { get; init; }
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
    /// <summary>Stack quantity that claims sum against (vanilla ingest reserves a stack by count); unknown when unread.</summary>
    public Fact<double> StockCount { get; init; }
    /// <summary>Simultaneous claimants vanilla lets reserve this target (ReservationManager maxPawns); unknown when unreadable, never a guess.
    /// Claims on one target must share one value. The planner's default for unknown is evaluator policy, not contract.</summary>
    public Fact<int> MaxPawns { get; init; }
    public Fact<double> FreshTicks { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public required EquatableArray<string> Unsupported { get; init; }
    /// <summary>The target cell's roof and room now (#3339).</summary>
    public required NeedCell Cell { get; init; }
    /// <summary>A rest target's room role and stage (#3339); null for a food or recreation target.</summary>
    public required NeedBedRoom? Bed { get; init; }
    /// <summary>A food target's deterioration inputs (#3339); null when it cannot deteriorate.</summary>
    public required NeedDeterioration? Deterioration { get; init; }
}
