using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts.Lifecycle;
using RimGovernor.Brain.GameFacts;

namespace RimGovernor.Brain.GameOrders.Lifecycle;

[JsonConverter(typeof(WireEnumConverter<StartingThingPlacement>))]
public enum StartingThingPlacement
{
    [WireName("STARTING_THING_PLACEMENT_START_WITH")]
    StartWith = 1,
    [WireName("STARTING_THING_PLACEMENT_START_NEAR")]
    StartNear = 2,
    /// <summary>Scattered over the map, never under a roof.</summary>
    [WireName("STARTING_THING_PLACEMENT_SCATTERED")]
    Scattered = 3,
    /// <summary>Scattered over the map, roofed ground allowed.</summary>
    [WireName("STARTING_THING_PLACEMENT_SCATTERED_ROOFED")]
    ScatteredRoofed = 4,
}

/// <summary>
/// RimWorld PawnGenerationContext: which pawns a pawn-modifying part touches.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<ScenarioPawnContext>))]
public enum ScenarioPawnContext
{
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
    [WireName("SCENARIO_PLAYER_FACTION_NEW_ARRIVALS")]
    NewArrivals = 1,
    [WireName("SCENARIO_PLAYER_FACTION_NEW_TRIBE")]
    NewTribe = 2,
    /// <summary>The base scenario's own player faction part stays.</summary>
    [WireName("SCENARIO_PLAYER_FACTION_BASE")]
    Base = 3,
}

[JsonConverter(typeof(WireEnumConverter<ScenarioArrivalMethod>))]
public enum ScenarioArrivalMethod
{
    [WireName("SCENARIO_ARRIVAL_METHOD_STANDING")]
    Standing = 1,
    [WireName("SCENARIO_ARRIVAL_METHOD_DROP_PODS")]
    DropPods = 2,
    /// <summary>The base scenario's own arrival method stays.</summary>
    [WireName("SCENARIO_ARRIVAL_METHOD_BASE")]
    Base = 3,
}

public sealed record IdentityRequest;

/// <summary>
/// ReadTick is the bare observation scope: the context and pause state
/// ReadIdentity reports, for callers that only need the current load, map,
/// tick and generation.
/// </summary>
public sealed record TickRequest;

public sealed record SaveRequest
{
    public PlayerLifecycleContext? Player { get; init; }

    public string? SaveName { get; init; }

    public required ExpectedSaveTick ExpectedTick { get; init; }
}

/// <summary>The <c>expected_tick</c> choice of <see cref="SaveRequest"/>: the save runs at any tick, or only when the game is at the tick named.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ExpectedSaveTick.AnyCase), "any")]
[JsonDerivedType(typeof(ExpectedSaveTick.AtCase), "at")]
public abstract record ExpectedSaveTick
{
    private protected ExpectedSaveTick() { }

    public sealed record AnyCase : ExpectedSaveTick;

    public sealed record AtCase(long Value) : ExpectedSaveTick;
}

public sealed record LoadRequest
{
    /// <summary>
    /// Before a map exists, instance ownership is validated by the controller gamehost adapter.
    /// </summary>
    public string? RequestId { get; init; }

    public string? SaveName { get; init; }

    public required Readiness Readiness { get; init; }

    public required uint TimeoutMs { get; init; }

    /// <summary>
    /// Required when replacing a live map. Before-map admission uses owned instance+direction.
    /// </summary>
    public PlayerLifecycleContext? ExpectedPlayer { get; init; }
}

/// <summary>
/// Return to the main menu: native ends the loaded game without saving, the way the player's quit-to-menu does, and
/// the process stays up. Like Load it starts at once and is polled by request id; it is a completed no-op when the
/// game is already at a fresh main menu.
/// </summary>
public sealed record MenuRequest
{
    public string? RequestId { get; init; }

    /// <summary>
    /// Required when a colony map is loaded: the identity and direction of the game the request ends.
    /// </summary>
    public PlayerLifecycleContext? ExpectedPlayer { get; init; }
}

/// <summary>One end of the seasonal temperature band a start tile must meet: open (no limit at this end) or at a Celsius value.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TemperatureLimit.OpenCase), "open")]
[JsonDerivedType(typeof(TemperatureLimit.AtCase), "at")]
public abstract record TemperatureLimit
{
    private protected TemperatureLimit() { }

    public sealed record OpenCase : TemperatureLimit;

    public sealed record AtCase(float Value) : TemperatureLimit;
}

/// <summary>
/// New colony: native generates a colony from a spec on a fresh main
/// menu and saves it as the last phase, so the spec carries the save name.
/// An open temperature limit leaves that end of the band unconstrained. world_temperature is the
/// RimWorld OverallTemperature name; planet_coverage is 0.05..1; map_size is
/// the square side in cells (100..400).
/// </summary>
public sealed record NewColonySpec
{
    public string? Scenario { get; init; }

    public required uint ColonistCount { get; init; }

    public string? Seed { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Biomes { get; init; }

    public required bool FlatTile { get; init; }

    public string? Difficulty { get; init; }

    public string? Storyteller { get; init; }

    public required TemperatureLimit MinTemperature { get; init; }

    public required TemperatureLimit MaxTemperature { get; init; }

    public string? WorldTemperature { get; init; }

    public required uint MapSize { get; init; }

    public required float PlanetCoverage { get; init; }

    public string? SaveName { get; init; }

    public RimGovernor.Brain.GameFacts.Common.IdeoligionDesign? Ideoligion { get; init; }

    /// <summary>
    /// The brain resolves this request through the definition catalog before dispatch.
    /// </summary>
    public required bool GovernorIdeoligion { get; init; }

    /// <summary>
    /// A theme preset id (#3511). The brain designs the starting ideoligion that holds the preset's memes, precepts and
    /// venerated animals and, once the colony exists, adopts the theme into its goals. Like
    /// <see cref="GovernorIdeoligion"/> it is resolved before dispatch; native refuses either.
    /// </summary>
    public string? Theme { get; init; }

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
    /// Mandatory-part overrides; Base keeps the base scenario's. The starting
    /// pawn count is colonist_count.
    /// </summary>
    public required ScenarioPlayerFaction PlayerFaction { get; init; }

    public required ScenarioArrivalMethod ArrivalMethod { get; init; }
}

/// <summary>
/// stuff and quality (QualityCategory name) are optional; only a scattered
/// placement says whether roofed ground is allowed.
/// </summary>
public sealed record StartingThing
{
    public required StartingThingPlacement Placement { get; init; }

    public string? ThingDef { get; init; }

    public string? Stuff { get; init; }

    public required uint Count { get; init; }

    public string? Quality { get; init; }
}

/// <summary>
/// chance is 0..1.
/// </summary>
public sealed record ForcedTrait
{
    public string? Trait { get; init; }

    public required int Degree { get; init; }

    public required float Chance { get; init; }

    public required ScenarioPawnContext Context { get; init; }
}

/// <summary>
/// severity is 0..1 (every scenario-addable hediff's cap in 1.6; native also
/// refuses above the def's lethal severity x 0.99). The game's part takes a
/// severity range; native sets both ends to severity. chance is 0..1.
/// </summary>
public sealed record ForcedHediff
{
    public string? Hediff { get; init; }

    public required float Severity { get; init; }

    public required float Chance { get; init; }

    public required ScenarioPawnContext Context { get; init; }
}

public sealed record NakedPart
{
    public required float Chance { get; init; }

    public required ScenarioPawnContext Context { get; init; }
}

/// <summary>
/// level is 0..1 (the game's part takes a range; native sets both ends to
/// level); chance is 0..1. Native refuses a need that is not major.
/// </summary>
public sealed record SetNeedLevel
{
    public string? Need { get; init; }

    public required float Level { get; init; }

    public required float Chance { get; init; }

    public required ScenarioPawnContext Context { get; init; }
}

public sealed record NewColonyRequest
{
    public string? RequestId { get; init; }

    public NewColonySpec? Spec { get; init; }

    public required uint TimeoutMs { get; init; }
}

/// <summary>
/// Governor state: opaque ASCII blobs the brain keeps in the save's
/// GovernorState game component. Native stores and returns them verbatim and
/// never interprets them; they persist once the game next saves.
/// </summary>
public sealed record GovernorStateRequest;

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
/// save. When the brain holds a WaitSaveSignal call and the save is not brain-initiated,
/// native hands that call a pre_save signal, blocks the game thread until
/// FlushDone acks its token (or ~3 s pass), then lets the save run. Signals
/// are not durable: a signal raised with no held call is dropped. Both tools
/// run on the bridge worker thread, never the game thread.
/// </summary>
public sealed record WaitSaveSignalRequest
{
    public required int TimeoutMs { get; init; }
}

public sealed record FlushDoneRequest
{
    public string? Token { get; init; }
}
