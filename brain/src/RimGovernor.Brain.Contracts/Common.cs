using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Contracts.Wire.Common;

[JsonConverter(typeof(WireEnumConverter<TradeRequestKind>))]
public enum TradeRequestKind
{
    [WireName("TRADE_REQUEST_KIND_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("TRADE_REQUEST_KIND_CARAVAN")]
    Caravan = 1,
    [WireName("TRADE_REQUEST_KIND_ORBITAL")]
    Orbital = 2,
}

/// <summary>
/// Whose corpse a thing is: the player faction's humanlike
/// (colonists and slaves), any other humanlike, or an animal.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<CorpseClass>))]
public enum CorpseClass
{
    [WireName("CORPSE_CLASS_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("CORPSE_CLASS_COLONIST")]
    Colonist = 1,
    [WireName("CORPSE_CLASS_STRANGER")]
    Stranger = 2,
    [WireName("CORPSE_CLASS_ANIMAL")]
    Animal = 3,
}

/// <summary>
/// A rotting thing's stage (Verse RotStage).
/// </summary>
[JsonConverter(typeof(WireEnumConverter<RotStage>))]
public enum RotStage
{
    [WireName("ROT_STAGE_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("ROT_STAGE_FRESH")]
    Fresh = 1,
    [WireName("ROT_STAGE_ROTTING")]
    Rotting = 2,
    [WireName("ROT_STAGE_DESSICATED")]
    Dessicated = 3,
}

/// <summary>
/// How RimWorld answers the same request again. Native sets it on a refusal;
/// a refusal without one is unknown. PERMANENT: the game refuses it until the
/// world changes materially (or never). TRANSIENT: it may succeed once the
/// world changes. UNKNOWN: native cannot say; never silently banned.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<RefusalClass>))]
public enum RefusalClass
{
    [WireName("REFUSAL_CLASS_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("REFUSAL_CLASS_PERMANENT")]
    Permanent = 1,
    [WireName("REFUSAL_CLASS_TRANSIENT")]
    Transient = 2,
    [WireName("REFUSAL_CLASS_UNKNOWN")]
    Unknown = 3,
}

[JsonConverter(typeof(WireEnumConverter<FailureCode>))]
public enum FailureCode
{
    [WireName("FAILURE_CODE_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("FAILURE_CODE_INVALID_REQUEST")]
    InvalidRequest = 1,
    [WireName("FAILURE_CODE_UNAVAILABLE")]
    Unavailable = 2,
    [WireName("FAILURE_CODE_STALE_IDENTITY")]
    StaleIdentity = 3,
    [WireName("FAILURE_CODE_STALE_GENERATION")]
    StaleGeneration = 4,
    [WireName("FAILURE_CODE_AUTHORITY_REQUIRED")]
    AuthorityRequired = 5,
    [WireName("FAILURE_CODE_LEASE_EXPIRED")]
    LeaseExpired = 6,
    [WireName("FAILURE_CODE_OWNER_CONFLICT")]
    OwnerConflict = 7,
    [WireName("FAILURE_CODE_ATTEMPT_CONFLICT")]
    AttemptConflict = 8,
    [WireName("FAILURE_CODE_CAPACITY_EXHAUSTED")]
    CapacityExhausted = 9,
    [WireName("FAILURE_CODE_NOT_FOUND")]
    NotFound = 10,
    [WireName("FAILURE_CODE_UNSUPPORTED")]
    Unsupported = 11,
    [WireName("FAILURE_CODE_NATIVE_FAILURE")]
    NativeFailure = 12,
    [WireName("FAILURE_CODE_CANCELLED")]
    Cancelled = 13,
    [WireName("FAILURE_CODE_DEADLINE_EXCEEDED")]
    DeadlineExceeded = 14,
    /// <summary>
    /// In-place wall replacement (#2529): no built wall at the cell, vanilla refuses
    /// the replacement blueprint, or the swap would drop a roof's last holder.
    /// </summary>
    [WireName("FAILURE_CODE_NO_WALL_TO_REPLACE")]
    NoWallToReplace = 15,
    [WireName("FAILURE_CODE_WALL_REPLACEMENT_REFUSED")]
    WallReplacementRefused = 16,
    [WireName("FAILURE_CODE_WALL_REPLACEMENT_STRANDS_ROOF")]
    WallReplacementStrandsRoof = 17,
}

[JsonConverter(typeof(WireEnumConverter<UnavailableReason>))]
public enum UnavailableReason
{
    [WireName("UNAVAILABLE_REASON_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("UNAVAILABLE_REASON_NOT_LOADED")]
    NotLoaded = 1,
    [WireName("UNAVAILABLE_REASON_NOT_OBSERVED")]
    NotObserved = 2,
    [WireName("UNAVAILABLE_REASON_UNSUPPORTED")]
    Unsupported = 3,
    [WireName("UNAVAILABLE_REASON_READ_FAILED")]
    ReadFailed = 4,
    [WireName("UNAVAILABLE_REASON_STALE")]
    Stale = 5,
    [WireName("UNAVAILABLE_REASON_LIMIT_EXCEEDED")]
    LimitExceeded = 6,
    [WireName("UNAVAILABLE_REASON_NOT_REQUESTED")]
    NotRequested = 7,
    [WireName("UNAVAILABLE_REASON_NOT_APPLICABLE")]
    NotApplicable = 8,
    [WireName("UNAVAILABLE_REASON_HIDDEN")]
    Hidden = 9,
    [WireName("UNAVAILABLE_REASON_NATIVE_COMPONENT_MISSING")]
    NativeComponentMissing = 10,
}

/// <summary>
/// Plain precepts are the exact issue selections; role, ritual and building
/// instances are initialized/preserved by the normal game creation/reform path.
/// </summary>
public sealed record IdeoligionDesign
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Memes { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<string>))]
    public EquatableArray<string> Precepts { get; init; }

    public bool? Fluid { get; init; }
}

/// <summary>
/// Presence is significant: map zero and tick zero are valid observations.
/// </summary>
public sealed record Identity
{
    public string? ColonyId { get; init; }

    public string? LoadToken { get; init; }

    public int? MapId { get; init; }
}

/// <summary>
/// A session participant is scoped to the same world as the observation/action.
/// Map and ship targets resolve on that map; settlement trades name our caravan.
/// </summary>
public sealed record TradeTarget
{
    public TradeTargetKind? Kind { get; init; }
}

/// <summary>The <c>kind</c> choice of <see cref="TradeTarget"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TradeTargetKind.MapTraderCase), "mapTrader")]
[JsonDerivedType(typeof(TradeTargetKind.SettlementCase), "settlement")]
[JsonDerivedType(typeof(TradeTargetKind.OrbitalShipCase), "orbitalShip")]
public abstract record TradeTargetKind
{
    private protected TradeTargetKind() { }

    /// <summary>The <c>MapTrader</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public MapTradeTarget? MapTrader => (this as MapTraderCase)?.Value;

    public sealed record MapTraderCase(MapTradeTarget Value) : TradeTargetKind;

    public static implicit operator TradeTargetKind(MapTradeTarget value) => new MapTraderCase(value);

    /// <summary>The <c>Settlement</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public SettlementTradeTarget? Settlement => (this as SettlementCase)?.Value;

    public sealed record SettlementCase(SettlementTradeTarget Value) : TradeTargetKind;

    public static implicit operator TradeTargetKind(SettlementTradeTarget value) => new SettlementCase(value);

    /// <summary>The <c>OrbitalShip</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public OrbitalTradeTarget? OrbitalShip => (this as OrbitalShipCase)?.Value;

    public sealed record OrbitalShipCase(OrbitalTradeTarget Value) : TradeTargetKind;

    public static implicit operator TradeTargetKind(OrbitalTradeTarget value) => new OrbitalShipCase(value);
}

public sealed record MapTradeTarget
{
    public string? TraderId { get; init; }
}

public sealed record SettlementTradeTarget
{
    public string? SettlementId { get; init; }

    public string? CaravanId { get; init; }
}

public sealed record OrbitalTradeTarget
{
    public string? ShipId { get; init; }
}

public sealed record ObservationContext
{
    public Identity? Identity { get; init; }

    public long? Tick { get; init; }

    public ulong? NativeGeneration { get; init; }
}

/// <summary>
/// Attempts are allocated per action, within a durable controller namespace.
/// </summary>
public sealed record AttemptKey
{
    public string? ControllerSessionId { get; init; }

    public string? ActionId { get; init; }

    public ulong? AttemptId { get; init; }
}

/// <summary>
/// A pointer at a thing, pawn, building, zone, bill, room or faction by its
/// load id. Definition, label and position come from the row it
/// resolves to (the frame's tables) and the definition catalog, never from
/// the reference.
/// </summary>
public sealed record Ref
{
    public string? Id { get; init; }
}

public sealed record Cell
{
    public int? X { get; init; }

    public int? Z { get; init; }
}

/// <summary>
/// Used before command admission. An uncertain admitted write requires a receipt.
/// </summary>
public sealed record Failure
{
    public FailureCode? Code { get; init; }

    public string? Detail { get; init; }

    public ObservationContext? ObservedContext { get; init; }

    public RefusalClass? RefusalClass { get; init; }
}

public sealed record Unavailable
{
    public UnavailableReason? Reason { get; init; }

    public string? Detail { get; init; }
}

