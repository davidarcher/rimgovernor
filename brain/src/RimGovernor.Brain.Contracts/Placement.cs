using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;

namespace RimGovernor.Contracts.Wire.Placement;

/// <summary>
/// Presence and domain limits are checked by ordinary boundary validators.
/// ProtoJSON parsing/formatting is provided by the official Protobuf runtimes.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<Rotation>))]
public enum Rotation
{
    [WireName("ROTATION_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("ROTATION_NORTH")]
    North = 1,
    [WireName("ROTATION_EAST")]
    East = 2,
    [WireName("ROTATION_SOUTH")]
    South = 3,
    [WireName("ROTATION_WEST")]
    West = 4,
    [WireName("ROTATION_ALL")]
    All = 5,
}

[JsonConverter(typeof(WireEnumConverter<Passability>))]
public enum Passability
{
    [WireName("PASSABILITY_UNSPECIFIED")]
    Unspecified = 0,
    [WireName("PASSABILITY_STANDABLE")]
    Standable = 1,
    [WireName("PASSABILITY_PASS_THROUGH_ONLY")]
    PassThroughOnly = 2,
    [WireName("PASSABILITY_IMPASSABLE")]
    Impassable = 3,
}

public sealed record PlacementRequest
{
    [JsonConverter(typeof(EquatableArrayJsonConverter<PlacementCandidate>))]
    public EquatableArray<PlacementCandidate> Placements { get; init; }

    public Common.Identity? Identity { get; init; }
}

public sealed record PlacementCandidate
{
    public string? DefName { get; init; }

    public int? X { get; init; }

    public int? Z { get; init; }

    public Rotation? Rotation { get; init; }

    /// <summary>
    /// Absent or empty requests the game's default material.
    /// </summary>
    public string? Stuff { get; init; }

    /// <summary>
    /// Previews the placement as though natural rock at the cell were already
    /// mined, for a planned building whose excavation the same plan orders
    /// first. Dispatch never sets it.
    /// </summary>
    public bool? IgnoreNaturalRock { get; init; }
}

public sealed record PlacementReply
{
    public PlacementReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="PlacementReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PlacementReplyOutcome.BatchCase), "batch")]
[JsonDerivedType(typeof(PlacementReplyOutcome.FailureCase), "failure")]
public abstract record PlacementReplyOutcome
{
    private protected PlacementReplyOutcome() { }

    /// <summary>The <c>Batch</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public PlacementBatch? Batch => (this as BatchCase)?.Value;

    public sealed record BatchCase(PlacementBatch Value) : PlacementReplyOutcome;

    public static implicit operator PlacementReplyOutcome(PlacementBatch value) => new BatchCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : PlacementReplyOutcome;

    public static implicit operator PlacementReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record PlacementBatch
{
    public Common.ObservationContext? Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<CandidateReply>))]
    public EquatableArray<CandidateReply> Results { get; init; }
}

public sealed record CandidateReply
{
    public CandidateReplyOutcome? Outcome { get; init; }
}

/// <summary>The <c>outcome</c> choice of <see cref="CandidateReply"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CandidateReplyOutcome.EvaluatedCase), "evaluated")]
[JsonDerivedType(typeof(CandidateReplyOutcome.FailureCase), "failure")]
public abstract record CandidateReplyOutcome
{
    private protected CandidateReplyOutcome() { }

    /// <summary>The <c>Evaluated</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public PlacementEvaluated? Evaluated => (this as EvaluatedCase)?.Value;

    public sealed record EvaluatedCase(PlacementEvaluated Value) : CandidateReplyOutcome;

    public static implicit operator CandidateReplyOutcome(PlacementEvaluated value) => new EvaluatedCase(value);

    /// <summary>The <c>Failure</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(Common.Failure Value) : CandidateReplyOutcome;

    public static implicit operator CandidateReplyOutcome(Common.Failure value) => new FailureCase(value);
}

public sealed record PlacementEvaluated
{
    public bool? CanPlace { get; init; }

    public bool? MadeFromStuff { get; init; }

    public Passability? Passability { get; init; }

    public bool? IsDoor { get; init; }

    public bool? ResearchFinished { get; init; }

    public bool? BuildableByPlayer { get; init; }

    /// <summary>
    /// Evaluated guarantees a complete cost scan; unreadable/oversized scans fail the candidate.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<PlacementCost>))]
    public EquatableArray<PlacementCost> CostList { get; init; }

    public PlacementMaterials? Materials { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PlacementRotation>))]
    public EquatableArray<PlacementRotation> Rotations { get; init; }
}

public sealed record PlacementCost
{
    public string? DefName { get; init; }

    public int? Count { get; init; }
}

public sealed record PlacementMaterials
{
    public PlacementMaterialsAvailability? Availability { get; init; }
}

/// <summary>The <c>availability</c> choice of <see cref="PlacementMaterials"/>: one derived record per case, discriminated by <c>type</c> in JSON.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PlacementMaterialsAvailability.KnownCase), "known")]
[JsonDerivedType(typeof(PlacementMaterialsAvailability.UnavailableCase), "unavailable")]
public abstract record PlacementMaterialsAvailability
{
    private protected PlacementMaterialsAvailability() { }

    /// <summary>The <c>Known</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public MaterialRows? Known => (this as KnownCase)?.Value;

    public sealed record KnownCase(MaterialRows Value) : PlacementMaterialsAvailability;

    public static implicit operator PlacementMaterialsAvailability(MaterialRows value) => new KnownCase(value);

    /// <summary>The <c>Unavailable</c> case's payload, null when another case is set.</summary>
    [JsonIgnore]
    public Common.Unavailable? Unavailable => (this as UnavailableCase)?.Value;

    public sealed record UnavailableCase(Common.Unavailable Value) : PlacementMaterialsAvailability;

    public static implicit operator PlacementMaterialsAvailability(Common.Unavailable value) => new UnavailableCase(value);
}

public sealed record MaterialRows
{
    /// <summary>
    /// Complete rows for all material definitions relevant to the evaluated candidate.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<PlacementMaterialStock>))]
    public EquatableArray<PlacementMaterialStock> Rows { get; init; }
}

public sealed record PlacementMaterialStock
{
    public string? DefName { get; init; }

    /// <summary>
    /// Absent means unknown; a present zero is known empty stock.
    /// </summary>
    public int? Available { get; init; }
}

public sealed record PlacementRotation
{
    public Rotation? Rotation { get; init; }

    public bool? Accepted { get; init; }

    public string? Reason { get; init; }

    /// <summary>
    /// Evaluated rotation guarantees complete footprint and blocker scans.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public EquatableArray<Common.Cell> OccupiedCells { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PlacementBlocker>))]
    public EquatableArray<PlacementBlocker> BlockingThings { get; init; }

    /// <summary>
    /// Native watch geometry and allowed-area/reachability checks for every eligible
    /// colonist. Absent is unavailable/not applicable; false is known unusable.
    /// Does not change normal-game placement legality or certify future pawn use.
    /// </summary>
    public bool? WatchCellsAccessible { get; init; }

    /// <summary>
    /// The definition's interaction cell at this rotation (empty when it has
    /// none): the cell a worker must stand on, outside occupied_cells, which a
    /// later placement there would block.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<Common.Cell>))]
    public EquatableArray<Common.Cell> InteractionCells { get; init; }

    /// <summary>
    /// Wind turbines only: the cells of the native wind catch zone
    /// (WindTurbineUtility.CalculateWindCells, 7x16 around the footprint) that
    /// are roofed, off the map or hold a wind-blocking thing. Absent for every
    /// other definition.
    /// </summary>
    public uint? WindBlockedCells { get; init; }
}

public sealed record PlacementBlocker
{
    public string? Category { get; init; }

    public bool? IsBlueprint { get; init; }

    public bool? IsFrame { get; init; }

    public bool? WouldBeWiped { get; init; }

    public bool? FrameWouldBeCancelled { get; init; }

    public string? DefName { get; init; }
}

