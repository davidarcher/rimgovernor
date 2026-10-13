using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Contracts.Wire.Lifecycle;

[JsonConverter(typeof(WireEnumConverter<CapabilitySupport>))]
public enum CapabilitySupport
{
    [WireName("CAPABILITY_SUPPORT_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("CAPABILITY_SUPPORT_SUPPORTED")]
    Supported = 1,
    [WireName("CAPABILITY_SUPPORT_UNAVAILABLE")]
    Unavailable = 2,
    [WireName("CAPABILITY_SUPPORT_UNSUPPORTED")]
    Unsupported = 3,
}

[JsonConverter(typeof(WireEnumConverter<Readiness>))]
public enum Readiness
{
    [WireName("READINESS_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("READINESS_MAP")]
    Map = 1,
    [WireName("READINESS_VISUAL")]
    Visual = 2,
}

[JsonConverter(typeof(WireEnumConverter<StartingThingPlacement>))]
public enum StartingThingPlacement
{
    [WireName("STARTING_THING_PLACEMENT_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("STARTING_THING_PLACEMENT_START_WITH")]
    StartWith = 1,
    [WireName("STARTING_THING_PLACEMENT_START_NEAR")]
    StartNear = 2,
    [WireName("STARTING_THING_PLACEMENT_SCATTERED")]
    Scattered = 3,
}

/// <summary>
/// RimWorld PawnGenerationContext: which pawns a pawn-modifying part touches.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<ScenarioPawnContext>))]
public enum ScenarioPawnContext
{
    [WireName("SCENARIO_PAWN_CONTEXT_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("SCENARIO_PAWN_CONTEXT_ALL")]
    All = 1,
    [WireName("SCENARIO_PAWN_CONTEXT_PLAYER_STARTER")]
    PlayerStarter = 2,
    [WireName("SCENARIO_PAWN_CONTEXT_NON_PLAYER")]
    NonPlayer = 3,
}

[JsonConverter(typeof(WireEnumConverter<ScenarioPlayerFaction>))]
public enum ScenarioPlayerFaction
{
    [WireName("SCENARIO_PLAYER_FACTION_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("SCENARIO_PLAYER_FACTION_NEW_ARRIVALS")]
    NewArrivals = 1,
    [WireName("SCENARIO_PLAYER_FACTION_NEW_TRIBE")]
    NewTribe = 2,
}

[JsonConverter(typeof(WireEnumConverter<ScenarioArrivalMethod>))]
public enum ScenarioArrivalMethod
{
    [WireName("SCENARIO_ARRIVAL_METHOD_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("SCENARIO_ARRIVAL_METHOD_STANDING")]
    Standing = 1,
    [WireName("SCENARIO_ARRIVAL_METHOD_DROP_PODS")]
    DropPods = 2,
}

/// <summary>
/// Native phases only; restarting the game is launcher-side.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<NewColonyPhase>))]
public enum NewColonyPhase
{
    [WireName("NEW_COLONY_PHASE_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("NEW_COLONY_PHASE_GENERATING_WORLD")]
    GeneratingWorld = 1,
    [WireName("NEW_COLONY_PHASE_CHOOSING_TILE")]
    ChoosingTile = 2,
    [WireName("NEW_COLONY_PHASE_ROLLING_COLONISTS")]
    RollingColonists = 3,
    [WireName("NEW_COLONY_PHASE_GENERATING_MAP")]
    GeneratingMap = 4,
    [WireName("NEW_COLONY_PHASE_FINISHING")]
    Finishing = 5,
    [WireName("NEW_COLONY_PHASE_SAVING")]
    Saving = 6,
}

public sealed record IdentityRequest;

/// <summary>
/// Descriptor names identify a fixed generated method; they are never dispatch arguments.
/// </summary>
public sealed record Capability
{
    public string? FullMethodName { get; init; }

    public CapabilitySupport? Support { get; init; }

    public string? Detail { get; init; }
}

public sealed record LoadedIdentity
{
    public Common.ObservationContext? Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<Capability>))]
    public EquatableArray<Capability> Capabilities { get; init; }

    public bool? Paused { get; init; }
}

public sealed record IdentityReply
{
    public IdentityReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="IdentityReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(IdentityReplyOutcome.LoadedCase), "loaded")]
[JsonDerivedType(typeof(IdentityReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(IdentityReplyOutcome.FailureCase), "failure")]
public abstract record IdentityReplyOutcome
{
    private protected IdentityReplyOutcome() { }

    public sealed record LoadedCase(LoadedIdentity Value) : IdentityReplyOutcome;

    public static implicit operator IdentityReplyOutcome(LoadedIdentity value) => new LoadedCase(value);

    public sealed record UnavailableCase(Common.Unavailable Value) : IdentityReplyOutcome;

    public static implicit operator IdentityReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    public sealed record FailureCase(Common.Failure Value) : IdentityReplyOutcome;

    public static implicit operator IdentityReplyOutcome(Common.Failure value) => new FailureCase(value);
}

/// <summary>
/// ReadTick is the bare observation scope: the context and pause state
/// ReadIdentity reports, without its capability list, for callers that only
/// need the current load, map, tick and generation.
/// </summary>
public sealed record TickRequest;

/// <summary>
/// An open window that force-pauses the game (Window.forcePause).
/// </summary>
public sealed record PausingWindow
{
    public string? NativeType { get; init; }

    public string? Title { get; init; }
}

/// <summary>
/// paused is TickManager.Paused: the speed control at Paused, or
/// force_paused (a window or other native force-pause request). time_speed
/// is TickManager.CurTimeSpeed's name.
/// </summary>
public sealed record LoadedTick
{
    public Common.ObservationContext? Context { get; init; }

    public bool? Paused { get; init; }

    public bool? ForcePaused { get; init; }

    public string? TimeSpeed { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PausingWindow>))]
    public EquatableArray<PausingWindow> PausingWindows { get; init; }
}

public sealed record TickReply
{
    public TickReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="TickReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TickReplyOutcome.LoadedCase), "loaded")]
[JsonDerivedType(typeof(TickReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(TickReplyOutcome.FailureCase), "failure")]
public abstract record TickReplyOutcome
{
    private protected TickReplyOutcome() { }

    public sealed record LoadedCase(LoadedTick Value) : TickReplyOutcome;

    public static implicit operator TickReplyOutcome(LoadedTick value) => new LoadedCase(value);

    public sealed record UnavailableCase(Common.Unavailable Value) : TickReplyOutcome;

    public static implicit operator TickReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    public sealed record FailureCase(Common.Failure Value) : TickReplyOutcome;

    public static implicit operator TickReplyOutcome(Common.Failure value) => new FailureCase(value);
}

/// <summary>
/// These requests belong to explicit session lifecycle, outside gameplay/model dispatch.
/// </summary>
public sealed record PlayerLifecycleContext
{
    public Common.Identity? Identity { get; init; }

    public ulong? PlayerDirection { get; init; }

    public string? RequestId { get; init; }
}

public sealed record SaveRequest
{
    public PlayerLifecycleContext? Player { get; init; }

    public string? SaveName { get; init; }

    public long? ExpectedTick { get; init; }
}

public sealed record SaveCompleted
{
    public string? RequestId { get; init; }

    public string? SaveName { get; init; }

    public Common.ObservationContext? Context { get; init; }

    public bool? Paused { get; init; }

    public ulong? PlayerDirection { get; init; }

    public ulong? ByteLength { get; init; }
}

public sealed record SaveUncertain
{
    public string? RequestId { get; init; }

    public string? SaveName { get; init; }

    public Common.ObservationContext? ObservedContext { get; init; }

    public string? Detail { get; init; }
}

public sealed record SaveReply
{
    public SaveReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="SaveReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SaveReplyOutcome.CompletedCase), "completed")]
[JsonDerivedType(typeof(SaveReplyOutcome.UncertainCase), "uncertain")]
[JsonDerivedType(typeof(SaveReplyOutcome.FailureCase), "failure")]
public abstract record SaveReplyOutcome
{
    private protected SaveReplyOutcome() { }

    public sealed record CompletedCase(SaveCompleted Value) : SaveReplyOutcome;

    public static implicit operator SaveReplyOutcome(SaveCompleted value) => new CompletedCase(value);

    public sealed record UncertainCase(SaveUncertain Value) : SaveReplyOutcome;

    public static implicit operator SaveReplyOutcome(SaveUncertain value) => new UncertainCase(value);

    public sealed record FailureCase(Common.Failure Value) : SaveReplyOutcome;

    public static implicit operator SaveReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record LoadRequest
{
    /// <summary>
    /// Before a map exists, instance ownership is validated by the Go controller gamehost adapter.
    /// </summary>
    public string? RequestId { get; init; }

    public string? SaveName { get; init; }

    public Readiness? Readiness { get; init; }

    public uint? TimeoutMs { get; init; }

    /// <summary>
    /// Required when replacing a live map. Before-map admission uses owned instance+direction.
    /// </summary>
    public PlayerLifecycleContext? ExpectedPlayer { get; init; }

    public string? ExpectedInstanceId { get; init; }

    public ulong? PlayerDirection { get; init; }
}

public sealed record LoadCompleted
{
    public string? RequestId { get; init; }

    public string? SaveName { get; init; }

    public LoadedIdentity? Loaded { get; init; }

    public Readiness? Readiness { get; init; }
}

public sealed record LoadPending
{
    public string? RequestId { get; init; }

    public string? SaveName { get; init; }

    public bool? ProcessConnected { get; init; }

    public bool? MapReady { get; init; }

    public bool? VisualReady { get; init; }

    public string? Detail { get; init; }
}

public sealed record LoadSuperseded
{
    public string? RequestId { get; init; }

    public Common.ObservationContext? ObservedContext { get; init; }

    public string? Detail { get; init; }
}

public sealed record LoadReply
{
    public LoadReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="LoadReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LoadReplyOutcome.CompletedCase), "completed")]
[JsonDerivedType(typeof(LoadReplyOutcome.PendingCase), "pending")]
[JsonDerivedType(typeof(LoadReplyOutcome.FailureCase), "failure")]
[JsonDerivedType(typeof(LoadReplyOutcome.SupersededCase), "superseded")]
public abstract record LoadReplyOutcome
{
    private protected LoadReplyOutcome() { }

    public sealed record CompletedCase(LoadCompleted Value) : LoadReplyOutcome;

    public static implicit operator LoadReplyOutcome(LoadCompleted value) => new CompletedCase(value);

    public sealed record PendingCase(LoadPending Value) : LoadReplyOutcome;

    public static implicit operator LoadReplyOutcome(LoadPending value) => new PendingCase(value);

    public sealed record FailureCase(Common.Failure Value) : LoadReplyOutcome;

    public static implicit operator LoadReplyOutcome(Common.Failure value) => new FailureCase(value);

    public sealed record SupersededCase(LoadSuperseded Value) : LoadReplyOutcome;

    public static implicit operator LoadReplyOutcome(LoadSuperseded value) => new SupersededCase(value);
}

public sealed record RequestStatus
{
    public string? RequestId { get; init; }

    public string? ExpectedInstanceId { get; init; }
}

/// <summary>
/// New colony: native generates a colony from a spec on a fresh main
/// menu and saves it as the last phase, so the spec carries the save name.
/// Unset temperature fields leave the game default. world_temperature is the
/// RimWorld OverallTemperature name; planet_coverage is 0.05..1; map_size is
/// the square side in cells (100..400).
/// </summary>
public sealed record NewColonySpec
{
    public string? Scenario { get; init; }

    public uint? ColonistCount { get; init; }

    public string? Seed { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Biomes { get; init; }

    public bool? FlatTile { get; init; }

    public string? Difficulty { get; init; }

    public string? Storyteller { get; init; }

    public float? MinTemperature { get; init; }

    public float? MaxTemperature { get; init; }

    public string? WorldTemperature { get; init; }

    public uint? MapSize { get; init; }

    public float? PlanetCoverage { get; init; }

    public string? SaveName { get; init; }

    public Common.IdeoligionDesign? Ideoligion { get; init; }

    /// <summary>
    /// Go resolves this request through the definition catalog before dispatch.
    /// </summary>
    public bool? GovernorIdeoligion { get; init; }

    /// <summary>
    /// Scenario parts (the game editor's part set). Native applies them to a copy
    /// of `scenario`; defName existence is native's call. Replace by kind: a
    /// non-empty list replaces every base-scenario part of its kind (the three
    /// starting_things placements are one kind), an empty list keeps the base's.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<StartingThing>))]
    public EquatableArray<StartingThing> StartingThings { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ForcedTrait>))]
    public EquatableArray<ForcedTrait> ForcedTraits { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<ForcedHediff>))]
    public EquatableArray<ForcedHediff> ForcedHediffs { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<NakedPart>))]
    public EquatableArray<NakedPart> Naked { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<SetNeedLevel>))]
    public EquatableArray<SetNeedLevel> SetNeedLevels { get; init; }

    /// <summary>
    /// GameConditionDef names registered permanently on the start map (the
    /// game's PermanentGameCondition part). The editor lists only defs with
    /// canBePermanent, native accepts any def (ColdSnap works). There is no
    /// separate map-scoped part.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> PermaGameConditions { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> DisallowedBuildings { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> DisabledIncidents { get; init; }

    /// <summary>
    /// Mandatory-part overrides; unset keeps the base scenario's. The starting
    /// pawn count is colonist_count.
    /// </summary>
    public ScenarioPlayerFaction? PlayerFaction { get; init; }

    public ScenarioArrivalMethod? ArrivalMethod { get; init; }
}

/// <summary>
/// stuff and quality (QualityCategory name) are optional; allow_roofed applies
/// to scattered things only.
/// </summary>
public sealed record StartingThing
{
    public StartingThingPlacement? Placement { get; init; }

    public string? ThingDef { get; init; }

    public string? Stuff { get; init; }

    public uint? Count { get; init; }

    public string? Quality { get; init; }

    public bool? AllowRoofed { get; init; }
}

/// <summary>
/// chance is 0..1.
/// </summary>
public sealed record ForcedTrait
{
    public string? Trait { get; init; }

    public int? Degree { get; init; }

    public float? Chance { get; init; }

    public ScenarioPawnContext? Context { get; init; }
}

/// <summary>
/// severity is 0..1 (every scenario-addable hediff's cap in 1.6; native also
/// refuses above the def's lethal severity x 0.99). The game's part takes a
/// severity range; native sets both ends to severity. chance is 0..1.
/// </summary>
public sealed record ForcedHediff
{
    public string? Hediff { get; init; }

    public float? Severity { get; init; }

    public float? Chance { get; init; }

    public ScenarioPawnContext? Context { get; init; }
}

public sealed record NakedPart
{
    public float? Chance { get; init; }

    public ScenarioPawnContext? Context { get; init; }
}

/// <summary>
/// level is 0..1 (the game's part takes a range; native sets both ends to
/// level); chance is 0..1. Native refuses a need that is not major.
/// </summary>
public sealed record SetNeedLevel
{
    public string? Need { get; init; }

    public float? Level { get; init; }

    public float? Chance { get; init; }

    public ScenarioPawnContext? Context { get; init; }
}

public sealed record NewColonyRequest
{
    public string? RequestId { get; init; }

    public NewColonySpec? Spec { get; init; }

    public uint? TimeoutMs { get; init; }
}

public sealed record NewColonyCompleted
{
    public string? RequestId { get; init; }

    public string? SaveName { get; init; }

    public Common.ObservationContext? Context { get; init; }

    public bool? Paused { get; init; }

    public ulong? ByteLength { get; init; }

    /// <summary>
    /// The seed the colony was generated from, after any reroll native made.
    /// </summary>
    public string? Seed { get; init; }
}

public sealed record NewColonyPending
{
    public string? RequestId { get; init; }

    public NewColonyPhase? Phase { get; init; }

    public string? Detail { get; init; }

    public ulong? ElapsedMs { get; init; }

    public uint? RerollCount { get; init; }
}

public sealed record NewColonySuperseded
{
    public string? RequestId { get; init; }

    public string? Detail { get; init; }
}

public sealed record NewColonyReply
{
    public NewColonyReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="NewColonyReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NewColonyReplyOutcome.CompletedCase), "completed")]
[JsonDerivedType(typeof(NewColonyReplyOutcome.PendingCase), "pending")]
[JsonDerivedType(typeof(NewColonyReplyOutcome.FailureCase), "failure")]
[JsonDerivedType(typeof(NewColonyReplyOutcome.SupersededCase), "superseded")]
public abstract record NewColonyReplyOutcome
{
    private protected NewColonyReplyOutcome() { }

    public sealed record CompletedCase(NewColonyCompleted Value) : NewColonyReplyOutcome;

    public static implicit operator NewColonyReplyOutcome(NewColonyCompleted value) => new CompletedCase(value);

    public sealed record PendingCase(NewColonyPending Value) : NewColonyReplyOutcome;

    public static implicit operator NewColonyReplyOutcome(NewColonyPending value) => new PendingCase(value);

    public sealed record FailureCase(Common.Failure Value) : NewColonyReplyOutcome;

    public static implicit operator NewColonyReplyOutcome(Common.Failure value) => new FailureCase(value);

    public sealed record SupersededCase(NewColonySuperseded Value) : NewColonyReplyOutcome;

    public static implicit operator NewColonyReplyOutcome(NewColonySuperseded value) => new SupersededCase(value);
}

/// <summary>
/// Governor state: opaque ASCII blobs Go keeps in the save's
/// GovernorState game component. Native stores and returns them verbatim and
/// never interprets them; they persist once the game next saves.
/// </summary>
public sealed record GovernorStateRequest;

public sealed record GovernorStateBlobs
{
    public IReadOnlyDictionary<string, string> Blobs { get; init; } = new Dictionary<string, string>();
}

public sealed record GovernorStateReply
{
    public GovernorStateReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="GovernorStateReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(GovernorStateReplyOutcome.LoadedCase), "loaded")]
[JsonDerivedType(typeof(GovernorStateReplyOutcome.UnavailableCase), "unavailable")]
[JsonDerivedType(typeof(GovernorStateReplyOutcome.FailureCase), "failure")]
public abstract record GovernorStateReplyOutcome
{
    private protected GovernorStateReplyOutcome() { }

    public sealed record LoadedCase(GovernorStateBlobs Value) : GovernorStateReplyOutcome;

    public static implicit operator GovernorStateReplyOutcome(GovernorStateBlobs value) => new LoadedCase(value);

    public sealed record UnavailableCase(Common.Unavailable Value) : GovernorStateReplyOutcome;

    public static implicit operator GovernorStateReplyOutcome(Common.Unavailable value) => new UnavailableCase(value);

    public sealed record FailureCase(Common.Failure Value) : GovernorStateReplyOutcome;

    public static implicit operator GovernorStateReplyOutcome(Common.Failure value) => new FailureCase(value);
}

/// <summary>
/// PutBatch replaces the whole blob set: keys absent from blobs are
/// removed. It runs off the game thread; the reply's loaded outcome is empty.
/// </summary>
public sealed record PutGovernorStateBatchRequest
{
    public IReadOnlyDictionary<string, string> Blobs { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Save-time handshake. Native's SaveGame prefix never cancels a
/// save. When Go holds a WaitSaveSignal call and the save is not Go-initiated,
/// native hands that call a pre_save signal, blocks the game thread until
/// FlushDone acks its token (or ~3 s pass), then lets the save run. Signals
/// are not durable: a signal raised with no held call is dropped. Both tools
/// run on the bridge worker thread, never the game thread.
/// </summary>
public sealed record WaitSaveSignalRequest
{
    public int? TimeoutMs { get; init; }
}

public sealed record SaveSignal
{
    public string? Kind { get; init; }

    public string? Token { get; init; }
}

public sealed record SaveSignalTimeout;

public sealed record WaitSaveSignalReply
{
    public WaitSaveSignalReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="WaitSaveSignalReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(WaitSaveSignalReplyOutcome.SignalCase), "signal")]
[JsonDerivedType(typeof(WaitSaveSignalReplyOutcome.TimeoutCase), "timeout")]
[JsonDerivedType(typeof(WaitSaveSignalReplyOutcome.FailureCase), "failure")]
public abstract record WaitSaveSignalReplyOutcome
{
    private protected WaitSaveSignalReplyOutcome() { }

    public sealed record SignalCase(SaveSignal Value) : WaitSaveSignalReplyOutcome;

    public static implicit operator WaitSaveSignalReplyOutcome(SaveSignal value) => new SignalCase(value);

    public sealed record TimeoutCase(SaveSignalTimeout Value) : WaitSaveSignalReplyOutcome;

    public static implicit operator WaitSaveSignalReplyOutcome(SaveSignalTimeout value) => new TimeoutCase(value);

    public sealed record FailureCase(Common.Failure Value) : WaitSaveSignalReplyOutcome;

    public static implicit operator WaitSaveSignalReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record FlushDoneRequest
{
    public string? Token { get; init; }
}

public sealed record FlushDoneAcked;

public sealed record FlushDoneReply
{
    public FlushDoneReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="FlushDoneReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(FlushDoneReplyOutcome.AckedCase), "acked")]
[JsonDerivedType(typeof(FlushDoneReplyOutcome.FailureCase), "failure")]
public abstract record FlushDoneReplyOutcome
{
    private protected FlushDoneReplyOutcome() { }

    public sealed record AckedCase(FlushDoneAcked Value) : FlushDoneReplyOutcome;

    public static implicit operator FlushDoneReplyOutcome(FlushDoneAcked value) => new AckedCase(value);

    public sealed record FailureCase(Common.Failure Value) : FlushDoneReplyOutcome;

    public static implicit operator FlushDoneReplyOutcome(Common.Failure value) => new FailureCase(value);
}

