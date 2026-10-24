using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Contracts.Wire.Observations;

/// <summary>Bounded, read-only mechanics and target census. Null read is unknown, never no needs.</summary>
public sealed record NeedsForecastRead
{
    public long Tick { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<NeedsForecastPawn>))]
    public EquatableArray<NeedsForecastPawn> Pawns { get; init; }
    /// <summary>The map's current weather (WeatherManager.CurWeatherLerped): its weatherThought def ("" none) and that def's stage count (#3339).</summary>
    public string WeatherThought { get; init; } = "";
    public int WeatherThoughtStages { get; init; }
    /// <summary>WeatherManager.RainRate now; unknown when unread. Held over the horizon (#3339).</summary>
    public Fact<double> RainRate { get; init; }
    /// <summary>Map.Area, the cell count SteadyEnvironmentEffects cycles over (#3339).</summary>
    public Fact<int> MapArea { get; init; }
    /// <summary>The DeteriorationRate stat's StatPart_EnvironmentalEffects def data (#3339); null when the stat has no such part.</summary>
    public NeedDeteriorationPart? Deterioration { get; init; }
    /// <summary>
    /// Every spawned research bench with power (or none needed), in bench id order: the benches a researcher can work at whatever
    /// the current project is (#3567), each with its <c>StatDefOf.ResearchSpeedFactor</c> now (#3800). Empty means none observed.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<ResearchBenchRead>))]
    public EquatableArray<ResearchBenchRead> ResearchBenches { get; init; }
    /// <summary>
    /// Every unfogged spawned corpse whose sighting raises an observed-corpse event (#3806: <c>Corpse.GiveObservedHistoryEvent</c>
    /// is ObservedLayingCorpse or ObservedLayingRottingCorpse, so a humanlike out of storage), in corpse id order, with each
    /// colonist's sightings of it. Empty means none lies in sight.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<CorpseSightingRead>))]
    public EquatableArray<CorpseSightingRead> Corpses { get; init; }
    /// <summary>The deathrest defs the deathrest chamber credit reads (#3810); null without Biotech, where no pawn deathrests.</summary>
    public DeathrestDefsRead? Deathrest { get; init; }
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
/// UsesOutdoorTemperature and PsychologicallyOutdoors (no room reads as outdoors, as BeautyUtility does). Unknown when unread.
/// </summary>
public sealed record NeedCell(Fact<bool> Roofed, Fact<bool> ThickRoof, Fact<bool> NaturalRoof, Fact<bool> OutdoorTemperature, Fact<bool> PsychologicallyOutdoors);
/// <summary>A bed's room now (#3339): its RoomRoleDef ("" none), its Impressiveness score stage and ForPrisoners.</summary>
public sealed record NeedBedRoom(string Role, Fact<int> Stage, bool ForPrisoners);
/// <summary>
/// A food stack's deterioration inputs (#3339): its HitPoints, SteadyEnvironmentEffects.FinalDeteriorationRate now (every
/// DeteriorationRate part applied at its cell), whether StatPart_EnvironmentalEffects applies (deteriorateFromEnvironmentalEffects),
/// the cell terrain's extraDeteriorationFactor and SteadyEnvironmentEffects.ProtectedByEdifice.
/// </summary>
public sealed record NeedDeterioration(double HitPoints, Fact<double> RatePerDay, bool Environmental, double TerrainFactor, bool Protected);
/// <summary>
/// A thought this pawn can get (ThoughtUtility.CanGetThought) at one stage, priced by the game's own MoodOffset for this
/// pawn (#3339). Role names why it is read: slept_outside, slept_in_bedroom, slept_in_barracks, weather, need_outdoors,
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
/// of its role is unmet. A stage the game cannot price is left out. Memories carry their duration; situational thoughts none.
/// </summary>
public sealed record NeedThoughtStage(string Thought, string Role, int Stage, double Offset,
    [property: JsonConverter(typeof(ObservedNumberJsonConverter))] Fact<double> DurationTicks, bool Fades);
/// <summary>
/// The pawn facts the apparel thought workers read beside the worn apparel (#3816): pawn.gender, whether any not-missing
/// body part is in the Legs group (a legless pawn counts as wearing pants), whether GenTemperature.SafeTemperatureRange
/// includes pawn.AmbientTemperature, and the kind's apparelRequired defs (clothing the pawn kind requires does not count
/// against the covering-disapproved precepts).
/// </summary>
public sealed record NeedApparel(Defs.Gender Gender, bool Legs, bool SafeAmbient)
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> KindRequired { get; init; }
    /// <summary>
    /// The pawn's active apparel requirements whose unmet thought it can have (#3836): its royal titles' in effect while
    /// pawn.royalty.allowApparelRequirements, and its ideoligion role's while the role is active and the pawn a free non-slave
    /// colonist who is no quest lodger; each IsActive (Pawn_ApparelTracker.ActiveRequirementsForReading). Empty with no title
    /// and no role, or without Royalty and Ideology.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<NeedApparelRequirement>))]
    public EquatableArray<NeedApparelRequirement> Requirements { get; init; }
}
/// <summary>
/// One active apparel requirement (#3836): the stage-row role of the thought it raises while unmet (title_apparel_unmet or
/// role_apparel_unmet), and every apparel def whose wearing meets it (ApparelRequirement.ApparelMeetsRequirement with
/// allowUnmatched false, the test ApparelRequirement.IsMet applies to each worn garment).
/// </summary>
public sealed record NeedApparelRequirement(string Thought,
    [property: JsonConverter(typeof(EquatableArrayJsonConverter<string>))] EquatableArray<string> Meets);
public sealed record NeedRate(string Kind,
    [property: JsonConverter(typeof(ObservedNumberJsonConverter))] Fact<double> Level,
    [property: JsonConverter(typeof(ObservedNumberJsonConverter))] Fact<double> FallPerTick,
    [property: JsonConverter(typeof(ObservedNumberJsonConverter))] Fact<double> Threshold)
{
    /// <summary>
    /// The need's gain per tick with no service object: resting on the ground when the colony has no usable bed
    /// (<c>StatDefOf.BedRestEffectiveness.valueIfMissing</c>, as <c>Toils_LayDown</c> applies it). Unknown for a need that has no such recovery.
    /// </summary>
    [JsonConverter(typeof(ObservedNumberJsonConverter))]
    public Fact<double> UnservedGainPerTick { get; init; }
    /// <summary>Current-rate approximation; observed threshold crossings require fresh native rates.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Unsupported { get; init; }
}
public sealed record NeedMemory(string Id, double Offset,
    [property: JsonConverter(typeof(ObservedNumberJsonConverter))] Fact<double> RemainingTicks, bool Fades)
{
    public bool Permanent { get; init; }
    /// <summary>The def's native stackLimit (ThoughtDef.stackLimit, default 1); 0 when not read. At 1 a new memory of the def renews the
    /// existing one (Thought_Memory.TryMergeWithExistingMemory) instead of adding to it (#3598).</summary>
    public int StackLimit { get; init; }
}
/// <summary>Native mood-point contribution in a half-open local-hour window; EndHour less than StartHour wraps midnight. CurrentOffset is already present in MoodUnclampedTarget.</summary>
public sealed record NeedConditionalThought(double StartHour, double EndHour, bool AwakeOnly,
    double Offset, double CurrentOffset);
/// <summary>Exact observed continuous service. RemainingTicks is a current-mechanics projection,
/// not a release guarantee; journal ownership still requires observed continuation/release.</summary>
public sealed record NeedCurrentService(string Assignment, string Method, long Revision, string Target,
    string Kind, Cell Location,
    [property: JsonConverter(typeof(ObservedNumberJsonConverter))] Fact<double> RemainingTicks,
    [property: JsonConverter(typeof(ObservedNumberJsonConverter))] Fact<double> GainPerTick)
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Unsupported { get; init; }
}
public sealed record NeedsForecastPawn
{
    public string Pawn { get; init; } = "";
    public string State { get; init; } = "unknown";
    public Cell? Location { get; init; }
    /// <summary>Null means no supported observed continuous incumbent, not a free pawn.</summary>
    public NeedCurrentService? CurrentService { get; init; }
    /// <summary>Observed requirements outside supported voluntary control. Empty means none observed;
    /// a missing need census has an explicit blocker. Passive consequence uncertainty stays in Unsupported.</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> VoluntaryControlBlockers { get; init; }
    [JsonConverter(typeof(ObservedConditionalThoughtsJsonConverter))]
    public Fact<EquatableArray<NeedConditionalThought>> ConditionalThoughts { get; init; }
    public Fact<double> LocalHour { get; init; }
    public Fact<bool> Awake { get; init; }
    public Fact<double> SleepThreshold { get; init; }
    public Fact<double> WakeThreshold { get; init; }
    /// <summary>Earliest possible sleep relative to observation tick; unknown for blockers with no known recovery bound.</summary>
    public Fact<double> SleepBlockedUntilTicks { get; init; }
    public Fact<bool> SleepWhileStarving { get; init; }
    /// <summary>Unclamped native target in normalized mood units, including CurrentOffset contributions once.</summary>
    public Fact<double> MoodUnclampedTarget { get; init; }
    public Fact<double> MoodRisePerTick { get; init; }
    public Fact<double> MoodFallPerTick { get; init; }
    public Fact<double> MoodIntervalTicks { get; init; }
    public Fact<bool> MoodFreezesWhileAsleep { get; init; }
    public Fact<double> ComfortableMin { get; init; }
    public Fact<double> ComfortableMax { get; init; }
    [JsonConverter(typeof(ObservedNumberJsonConverter))]
    public Fact<double> Mood { get; init; }
    [JsonConverter(typeof(ObservedNumberJsonConverter))]
    public Fact<double> MoodTarget { get; init; }
    [JsonConverter(typeof(ObservedNumberJsonConverter))]
    public Fact<double> BreakThreshold { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<NeedRate>))]
    public EquatableArray<NeedRate> Needs { get; init; }
    [JsonConverter(typeof(ObservedMemoriesJsonConverter))]
    public Fact<EquatableArray<NeedMemory>> Memories { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<NeedTarget>))]
    public EquatableArray<NeedTarget> Targets { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Unsupported { get; init; }
    /// <summary>Need_Outdoors.CurLevel; unknown without the need. Disabled (PrefersIndoors) holds it at 1 (#3339).</summary>
    public Fact<double> OutdoorsLevel { get; init; }
    public bool OutdoorsDisabled { get; init; }
    /// <summary>ownership.OwnedBed's load id ("" none) and the Ascetic trait, the two pawn facts ApplyBedThoughts reads (#3339).</summary>
    public string OwnedBed { get; init; } = "";
    public bool Ascetic { get; init; }
    /// <summary>
    /// The Impressiveness of each room another pawn owns, ascending, as ThoughtWorker_BedroomJealous compares the pawn's own
    /// room against them (#3826): every spawned player-faction pawn but this one without a host faction, by its OwnedRoom,
    /// each room once. Empty when no other pawn owns a room.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<double>))]
    public EquatableArray<double> RivalRooms { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<NeedThoughtStage>))]
    public EquatableArray<NeedThoughtStage> ThoughtStages { get; init; }
    /// <summary>The apparel thought workers' pawn facts (#3816); null without an apparel tracker.</summary>
    public NeedApparel? Apparel { get; init; }
    /// <summary>pawn.health.capacities.GetLevel of the five capacities a pawn-day is counted in (#3519); unknown when the pawn has no health tracker.</summary>
    public Fact<double> Consciousness { get; init; }
    public Fact<double> Moving { get; init; }
    public Fact<double> Manipulation { get; init; }
    public Fact<double> Sight { get; init; }
    public Fact<double> Talking { get; init; }
    /// <summary>The current severity of each hediff the capacity penalty tables decode; 0 when the pawn does not have it (#3519).</summary>
    public Fact<double> MalnutritionSeverity { get; init; }
    public Fact<double> FoodPoisoningSeverity { get; init; }
    public Fact<double> HypothermiaSeverity { get; init; }
    public Fact<double> HeatstrokeSeverity { get; init; }
    /// <summary>The health reads of a raid's consequences (#3544): StatDefOf.PainShockThreshold, pawn.HealthScale, StatDefOf.IncomingDamageFactor and StatDefOf.InjuryHealingFactor; unknown without a health tracker.</summary>
    public Fact<double> PainShockThreshold { get; init; }
    public Fact<double> HealthScale { get; init; }
    public Fact<double> IncomingDamageFactor { get; init; }
    public Fact<double> InjuryHealingFactor { get; init; }
    /// <summary>
    /// The pawn's work speeds whatever it is doing (#3567): <c>StatDefOf.ResearchSpeed</c> times the difficulty's researchSpeedFactor (the
    /// points a tick at bench factor 1 per <c>ResearchManager.ResearchPerformed</c>'s 0.00825) and <c>StatDefOf.PlantWorkSpeed</c> (sow work per tick).
    /// Unknown without the stat.
    /// </summary>
    public Fact<double> ResearchSpeed { get; init; }
    public Fact<double> PlantWorkSpeed { get; init; }
    /// <summary>The pawn's illness reads (#3824); null when the pawn has no health tracker.</summary>
    public PawnDiseaseFacts? Disease { get; init; }
    /// <summary>The memories the pawn's ideo gives it on seeing a corpse lying (#3806); empty when none would (no ideo, no such precept, no mood).</summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<CorpseThoughtRead>))]
    public EquatableArray<CorpseThoughtRead> CorpseThoughts { get; init; }
    /// <summary>The ticks before the read the pawn's sighting record covers (#3806): since its first observer pass, at most two days; 0 with no record.</summary>
    public int SightingTicks { get; init; }
    /// <summary>The pawn's deathrest (#3810); null when it has no deathrest gene.</summary>
    public DeathresterRead? Deathrest { get; init; }
}
/// <summary>
/// What a disease incident's course and victim draw read of one pawn (#3824): <c>StatDefOf.ImmunityGainSpeed</c>, and per DiseaseHuman
/// incident the chance it takes hold: <c>immunity.DiseaseContractChanceFactor</c> of its hediff for a potential victim, 0 for a pawn
/// <c>IncidentWorker_Disease.PotentialVictims</c> leaves out.
/// </summary>
public sealed record PawnDiseaseFacts
{
    public double ImmunityGainSpeed { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<DiseaseContract>))]
    public EquatableArray<DiseaseContract> Contracts { get; init; }
}
public sealed record DiseaseContract(string Incident, double Chance);
/// <summary>Native legality and mechanics, not a selected need action. Travel is an observed route cost.</summary>
public sealed record NeedTarget(string Id, string Kind, string Resource, int AvailableCount,
    [property: JsonConverter(typeof(ObservedNumberJsonConverter))] Fact<double> TravelTicks,
    [property: JsonConverter(typeof(ObservedNumberJsonConverter))] Fact<double> ServiceTicks,
    [property: JsonConverter(typeof(ObservedNumberJsonConverter))] Fact<double> GainPerUnit,
    [property: JsonConverter(typeof(ObservedNumberJsonConverter))] Fact<double> GainPerTick)
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<NeedMemory>))]
    public EquatableArray<NeedMemory> MealThoughts { get; init; }
    public Cell? Location { get; init; }
    /// <summary>Stack quantity that claims sum against (vanilla ingest reserves a stack by count); unknown when unread.</summary>
    public Fact<double> StockCount { get; init; }
    /// <summary>Simultaneous claimants vanilla lets reserve this target (ReservationManager maxPawns); unknown when unreadable, never a guess.
    /// Claims on one target must share one value. The planner's default for unknown is evaluator policy, not contract.</summary>
    public Fact<int> MaxPawns { get; init; }
    public Fact<double> FreshTicks { get; init; }
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Unsupported { get; init; }
    /// <summary>The target cell's roof and room now (#3339); null when unread.</summary>
    public NeedCell? Cell { get; init; }
    /// <summary>A rest target's room role and stage (#3339).</summary>
    public NeedBedRoom? Bed { get; init; }
    /// <summary>A food target's deterioration inputs (#3339); null when it cannot deteriorate or is unread.</summary>
    public NeedDeterioration? Deterioration { get; init; }
}
