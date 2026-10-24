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
    /// <c>StatDefOf.ResearchSpeedFactor</c> of every spawned research bench with power (or none needed), in bench id order: the benches a
    /// researcher can work at whatever the current project is (#3567). Empty means none observed.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<double>))]
    public EquatableArray<double> ResearchBenchFactors { get; init; }
}
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
/// (ThoughtWorker_Ascetic's own-room thought by Impressiveness stage, #3655). A null stage of a bed or ascetic thought,
/// where the game gives none (Toils_LayDown.ApplyBedThoughts at SleptInBedroom's dull stage, #3822; the ascetic worker
/// inactive), is a row at 0. A stage the game cannot price is left out. Memories carry their duration; situational thoughts none.
/// </summary>
public sealed record NeedThoughtStage(string Thought, string Role, int Stage, double Offset,
    [property: JsonConverter(typeof(ObservedNumberJsonConverter))] Fact<double> DurationTicks, bool Fades);
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
    [JsonConverter(typeof(EquatableArrayJsonConverter<NeedThoughtStage>))]
    public EquatableArray<NeedThoughtStage> ThoughtStages { get; init; }
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
}
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
