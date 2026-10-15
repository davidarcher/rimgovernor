namespace RimGovernor.Brain.Domain;

/// <summary>Prediction-only boundary. Implementations have no colony order capability.</summary>
public interface IEncounterPredictor
{
    Task<EncounterPrediction> PredictAsync(EncounterRequest request, CancellationToken cancellationToken);
}

public enum EncounterStatus { Evidence, Unsupported, Unavailable, Malformed, Cancelled, TimedOut, Busy, Stale }
public enum EncounterDepartureKind { Leave, Flee }
public sealed record EncounterIdentity(string World, long ObservationTick, string GameBuild, string Mods, string Tactic);
public sealed record EncounterGear(string Definition, string Stuff, string Quality, int HitPoints);
public sealed record EncounterHediff(string Definition, string Part, double Severity);
public sealed record EncounterTrait(string Definition, int Degree);
public sealed record EncounterCellRegion(int MinX, int MinZ, int MaxX, int MaxZ, string Terrain, string Roof);
public sealed record EncounterObstacle(string Definition, string Stuff, int X, int Z);
public sealed record EncounterDeparture(int Tick, EncounterDepartureKind Kind);

/// <summary>Health is the complete observed set. Availability ends combat contribution; later arrivals are unsupported.</summary>
public sealed record EncounterActor
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required bool Wild { get; init; }
    public required int X { get; init; }
    public required int Z { get; init; }
    public double Age { get; init; }
    public string Xenotype { get; init; } = "";
    public string Herd { get; init; } = "";
    public string HuntTarget { get; init; } = "";
    public EncounterGear? Weapon { get; init; }
    public IReadOnlyList<EncounterGear> Apparel { get; init; } = [];
    public IReadOnlyDictionary<string, int> Skills { get; init; } = new Dictionary<string, int>();
    public IReadOnlyList<EncounterTrait> Traits { get; init; } = [];
    public IReadOnlyList<EncounterHediff> Health { get; init; } = [];
    public IReadOnlyList<string> MissingParts { get; init; } = [];
    public EncounterDeparture? Departure { get; init; }
}

/// <summary>Fully stated hypothetical scene, not a native object or live RNG state. Unknown required observations exclude the scene.</summary>
public sealed record EncounterRequest
{
    public required EncounterIdentity Identity { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required string Terrain { get; init; }
    public required string Weather { get; init; }
    public required IReadOnlyList<EncounterActor> Actors { get; init; }
    public IReadOnlyList<EncounterCellRegion> Ground { get; init; } = [];
    public IReadOnlyList<EncounterObstacle> Obstacles { get; init; } = [];
    public required int HorizonTicks { get; init; }
    /// <summary>Independent experimental seeds, never the live RNG state. More sampling needs a decision-relevant reason.</summary>
    public required IReadOnlyList<int> Seeds { get; init; }
    public required string SamplingReason { get; init; }
    /// <summary>Unrepresented observed mechanics, including nondefault needs/genes/effects, must be stated and reject.</summary>
    public IReadOnlyList<string> UnsupportedObservations { get; init; } = [];
}

public sealed record EncounterInjury(string Definition, string Part, double Severity, double BleedRate, bool Tendable, bool Tended, bool Permanent);
public sealed record EncounterProducts(string MeatDefinition, double Meat, string LeatherDefinition, double Leather);
public sealed record EncounterActorOutcome(string Id, bool Downed, bool Dead, double HealthFraction,
    double BleedRate, bool NeedsTend, IReadOnlyList<EncounterInjury> Injuries, IReadOnlyList<string> MissingParts,
    IReadOnlyList<string> Kills, EncounterProducts? Products, int? FirstAttackTick, int? LastShotTick,
    int? DepartureTick, int? WithdrawalTick);
public sealed record EncounterSample(int Seed, int DurationTicks, bool HorizonReached, IReadOnlyList<EncounterActorOutcome> Actors);

/// <summary>Empirical samples are not a confidence interval or a safety certificate. Treatment/recovery labor is downstream.</summary>
public sealed record EncounterPrediction
{
    public required EncounterStatus Status { get; init; }
    public required string RequestFingerprint { get; init; }
    public required string Reason { get; init; }
    public string EngineFingerprint { get; init; } = "";
    public IReadOnlyList<EncounterSample> Samples { get; init; } = [];
    public IReadOnlyList<string> ModelUncertainty { get; init; } = [];
    public double? ColdStartMilliseconds { get; init; }
    public double? RequestMilliseconds { get; init; }
    /// <summary>Absent until an implementation has scoped, independently reviewed qualification evidence.</summary>
    public EncounterQualification? Qualification { get; init; }
    public bool QualifiedForSelection => Status == EncounterStatus.Evidence && Qualification is not null;
}


/// <summary>Reference to #2825 retained evidence, not an arbitrary safety flag or a colony risk preference.
/// A consumer must validate scope and accept the named criteria/report before using this capability.</summary>
public sealed record EncounterQualification(string CriteriaRevision, string ReportReference, string SupportedDomain,
    string GameBuild, string Mods, string Tactic, string EngineFingerprint, IReadOnlyList<string> CoveredRequestFingerprints);
