using System.Text.Json.Serialization;
using RimGovernor.Brain.Domain;
using RimGovernor.Brain.GameFacts;

namespace RimGovernor.Brain.GameFacts.Placement;

/// <summary>
/// Presence and domain limits are checked by ordinary boundary validators.
/// ProtoJSON parsing/formatting is provided by the official Protobuf runtimes.
/// </summary>
[JsonConverter(typeof(WireEnumConverter<Rotation>))]
public enum Rotation
{
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

public sealed record PlacementReply
{
    public PlacementReplyOutcome Outcome { get; init; } = null!;
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
    public RimGovernor.Brain.GameFacts.Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(RimGovernor.Brain.GameFacts.Common.Failure Value) : PlacementReplyOutcome;

    public static implicit operator PlacementReplyOutcome(RimGovernor.Brain.GameFacts.Common.Failure value) => new FailureCase(value);
}

public sealed record PlacementBatch
{
    public required RimGovernor.Brain.GameFacts.Common.ObservationContext Context { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<CandidateReply>))]
    public required EquatableArray<CandidateReply> Results { get; init; }
}

public sealed record CandidateReply
{
    public CandidateReplyOutcome Outcome { get; init; } = null!;
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
    public RimGovernor.Brain.GameFacts.Common.Failure? Failure => (this as FailureCase)?.Value;

    public sealed record FailureCase(RimGovernor.Brain.GameFacts.Common.Failure Value) : CandidateReplyOutcome;

    public static implicit operator CandidateReplyOutcome(RimGovernor.Brain.GameFacts.Common.Failure value) => new FailureCase(value);
}

public sealed record PlacementEvaluated
{
    public required bool CanPlace { get; init; }

    public required bool MadeFromStuff { get; init; }

    public required bool ResearchFinished { get; init; }

    public required bool BuildableByPlayer { get; init; }

    /// <summary>
    /// Evaluated guarantees a complete cost scan; unreadable/oversized scans fail the candidate.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<PlacementCost>))]
    public required EquatableArray<PlacementCost> CostList { get; init; }

    /// <summary>Evaluated guarantees a complete material scan; an unreadable one fails the candidate.</summary>
    public required PlacementMaterials Materials { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PlacementRotation>))]
    public required EquatableArray<PlacementRotation> Rotations { get; init; }
}

public sealed record PlacementCost
{
    public required string DefName { get; init; }

    public required int Count { get; init; }
}

/// <summary>
/// The complete material scan of an evaluated candidate.
/// </summary>
public sealed record PlacementMaterials
{
    /// <summary>
    /// Complete rows for all material definitions relevant to the evaluated candidate.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<PlacementMaterialStock>))]
    public required EquatableArray<PlacementMaterialStock> Rows { get; init; }
}

public sealed record PlacementMaterialStock
{
    public required string DefName { get; init; }

    /// <summary>
    /// The unforbidden, unclaimed count; zero is known empty stock. An incomplete scan fails the candidate.
    /// </summary>
    public required int Available { get; init; }
}

public sealed record PlacementRotation
{
    public required bool Accepted { get; init; }

    /// <summary>Why native refused the rotation; empty when it accepted.</summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Evaluated rotation guarantees complete footprint and blocker scans.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<RimGovernor.Brain.Domain.Cell>))]
    public required EquatableArray<RimGovernor.Brain.Domain.Cell> OccupiedCells { get; init; }

    [JsonConverter(typeof(EquatableArrayJsonConverter<PlacementBlocker>))]
    public required EquatableArray<PlacementBlocker> BlockingThings { get; init; }

    /// <summary>
    /// Native watch geometry and allowed-area/reachability checks for every eligible
    /// colonist. Does not change normal-game placement legality or certify future pawn use.
    /// </summary>
    public required WatchAccess WatchCells { get; init; }

    /// <summary>
    /// The definition's interaction cell at this rotation (empty when it has
    /// none): the cell a worker must stand on, outside occupied_cells, which a
    /// later placement there would block.
    /// </summary>
    [JsonConverter(typeof(EquatableArrayJsonConverter<RimGovernor.Brain.Domain.Cell>))]
    public required EquatableArray<RimGovernor.Brain.Domain.Cell> InteractionCells { get; init; }

    /// <summary>
    /// Wind turbines only: the cells of the native wind catch zone
    /// (WindTurbineUtility.CalculateWindCells, 7x16 around the footprint) that
    /// are roofed, off the map or hold a wind-blocking thing.
    /// <see cref="RimGovernor.Brain.GameFacts.Observations.BuildingWind.NotWindTurbine"/> for every other definition.
    /// </summary>
    public required RimGovernor.Brain.GameFacts.Observations.BuildingWind Wind { get; init; }
}

/// <summary>Whether every eligible colonist can reach a watch cell of a placement.</summary>
[JsonConverter(typeof(WireEnumConverter<WatchAccess>))]
public enum WatchAccess
{
    /// <summary>No recreation watches this definition.</summary>
    NotWatched,
    Reachable,
    Unreachable,
}

public sealed record PlacementBlocker
{
    public required string Category { get; init; }

    public required bool IsBlueprint { get; init; }

    public required bool IsFrame { get; init; }

    public required bool WouldBeWiped { get; init; }

    public required bool FrameWouldBeCancelled { get; init; }

    public required string DefName { get; init; }
}
