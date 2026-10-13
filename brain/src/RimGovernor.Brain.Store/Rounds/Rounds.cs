using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store;

/// <summary>Binds the Concern a Standard serves to the Standard row the review filed for it (Go <c>RoundsStandard</c>).</summary>
public sealed record RoundsStandard(ConcernId Concern, StandardId Standard);

/// <summary>
/// Binds identity and access direction. Its current position is resolved by the supply census and checked by
/// Hands before a write (Go <c>ReserveSupply</c>).
/// </summary>
public sealed record ReserveSupply(string Thing, string Definition, bool Forbid);

/// <summary>
/// The durable review cursor and hysteresis history (Go <c>Rounds</c>). Standard bindings retain semantic needs
/// while old executable plans keep their identity. A field Go omitted when empty is its empty value here.
/// </summary>
public sealed record Rounds
{
    /// <summary>Emergency findings guide response planning and reporting.</summary>
    public EquatableArray<ConcernId> Emergency { get; init; }

    /// <summary>The loose things the last known safety census reported unsafe to haul; the unsafe-loot Safeguard refuses allowing them at dispatch.</summary>
    public EquatableArray<string> Unsafe { get; init; }

    public bool BrewingFinished { get; init; }

    public EquatableArray<ResourceRunwayRecord> ResourceRunways { get; init; }

    public EquatableArray<ReserveSupply> ReserveSupplies { get; init; }

    public EquatableArray<StartingSupply> LarderSupplies { get; init; }

    /// <summary>The one recovery queue: the clearance planner executes its removals against a fresh native census; its held entries are the journalled hold reasons.</summary>
    public RecoveryQueue? RecoveryQueue { get; init; }

    public EquatableArray<ShrineHold> ShrineHolds { get; init; }

    /// <summary>The shrine planner's last step: the shrine it held on and why, and the candidates it passed over. A world change clears it; otherwise a review keeps the last one.</summary>
    public RoundsShrineStep? ShrineStep { get; init; }

    public RoundsRecovery? Recovery { get; init; }

    public DisasterHistory? Disaster { get; init; }

    public RoundsMood? Mood { get; init; }

    public EquatableArray<RoundsMoodMethod> MoodMethods { get; init; }

    public SleepingHistory Sleeping { get; init; } = new();

    public ulong Revision { get; init; }

    public GenerationSnapshot Snapshot { get; init; }

    public Tick Tick { get; init; }

    /// <summary>The last full enabled inspection, not refreshed by an immediate review. Zero means no full inspection in this world yet.</summary>
    public Tick OrdinaryTick { get; init; }

    public bool Immediate { get; init; }

    public bool Enabled { get; init; }

    public RoundsLatches Latches { get; init; } = new();

    public MedicalCareHistory MedicalCare { get; init; } = new();

    public EventLootHistory EventLoot { get; init; } = new();

    public ComfortHistory Comfort { get; init; }

    /// <summary>The Standards this review assessed.</summary>
    public EquatableArray<RoundsStandard> Standards { get; init; }

    /// <summary>Binds the Responses this review assessed to their open occurrences; those needs have no entry in <see cref="Standards"/>.</summary>
    public EquatableArray<RoundsIncident> Incidents { get; init; }

    /// <summary>The roster planner's last recorded report: coverage, decaying skills and pawn profiles as of its tick. A disabled review keeps the last one.</summary>
    public WorkRosterReport? Roster { get; init; }

    /// <summary>Every active goal's progress record, keyed by need.</summary>
    public EquatableArray<ConcernProgress> Progress { get; init; }

    /// <summary>The colony stage the review derived, with the first unmet condition of the next stage; a disabled review keeps the last one.</summary>
    public ColonyStageRecord? Stage { get; init; }

    /// <summary>The shadow ready-work projection of this enabled review's plans and unserved goals. Diagnostics only: no admission reads it.</summary>
    public ReadyWorkReport? ReadyWork { get; init; }

    /// <summary>Every live building action standing built, by geometry, in the last complete construction census, sorted. Admission reads it for building prerequisites.</summary>
    public EquatableArray<ActionId> Built { get; init; }

    /// <summary>For every cell a live plan's wall removal names, whether the last complete construction census still had a colony Wall on it, sorted by cell.</summary>
    public EquatableArray<WallCell> WallCells { get; init; }

    /// <summary>The live shortfall edges planners recorded (<c>RecordDependency</c>); each review drops the settled or stale ones.</summary>
    public EquatableArray<DependencyRecord> Dependencies { get; init; }

    /// <summary>Every inspection that raised nothing in the last enabled review, with its typed reason; a disabled review keeps the last.</summary>
    public EquatableArray<NoOpRecord> NoOps { get; init; }
}

/// <summary>One requested inspection (Go <c>RoundsRequest</c>).</summary>
public sealed record RoundsRequest
{
    public bool Immediate { get; init; }

    public ulong Revision { get; init; }

    public GenerationSnapshot Current { get; init; }

    public Tick Tick { get; init; }

    public bool Enabled { get; init; }

    public RoundsPolicy Policy { get; init; } = new();

    public RoundsFacts Facts { get; init; } = new();
}

/// <summary>What a review committed (Go <c>RoundsResult</c>).</summary>
public sealed record RoundsResult
{
    public Rounds Review { get; init; } = new();

    public RoundsFindings Needs { get; init; } = new();

    public IReadOnlyList<StandardState> Standards { get; init; } = [];

    /// <summary>The occurrences <see cref="Rounds.Incidents"/> binds.</summary>
    public IReadOnlyList<IncidentState> Incidents { get; init; } = [];

    /// <summary>Emergency findings guide response planning and reporting.</summary>
    public IReadOnlyList<ConcernId> Emergency { get; init; } = [];

    /// <summary>What an enabled review passed <c>DetectRounds</c>: the journal-enriched facts, the prior latches and the staged policy. Not journalled.</summary>
    public RoundsDetection? Detection { get; init; }
}

/// <summary>One <c>InspectRounds</c> call's input (Go <c>RoundsDetection</c>).</summary>
public sealed record RoundsDetection(RoundsFacts Facts, RoundsLatches Latches, RoundsPolicy Policy);
