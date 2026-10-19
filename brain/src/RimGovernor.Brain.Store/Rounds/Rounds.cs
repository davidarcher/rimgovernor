using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store;

/// <summary>
/// Binds identity and access direction. Its current position is resolved by the supply census and checked by
/// Hands before a write.
/// </summary>
public sealed record ReserveSupply(string Thing, string Definition, bool Forbid);

/// <summary>
/// The review's combat standing (#3311): what the <c>military/combat</c> Need's fight runner (the defense entry) answers.
/// <see cref="Situation"/> is <see cref="RoundsRules.CombatSituation"/> over the review's hostiles and hunt
/// request; <see cref="Prey"/> is the food plan's hunt prey while no hostile stands and <see cref="Hunters"/> the gunners of the
/// roster the plan chose for it (the only pawns the hunt may send). A review files one while the
/// situation is active or the Need still holds open work (a fight's drafts), and none otherwise.
/// </summary>
public sealed record RoundsCombat(Situation Situation, EquatableArray<PawnId> Prey, EquatableArray<PawnId> Hunters);

/// <summary>
/// The durable review cursor and hysteresis history. A field omitted when empty is its empty value.
/// </summary>
public sealed record Rounds
{
    public WorkAdmissionEvidence? WorkEvidence { get; init; }

    /// <summary>The loose things the last known safety census reported unsafe to haul; the unsafe-loot Safeguard refuses allowing them at dispatch.</summary>
    public EquatableArray<string> Unsafe { get; init; }

    public bool BrewingFinished { get; init; }

    public EquatableArray<ResourceRunwayRecord> ResourceRunways { get; init; }

    public EquatableArray<ReserveSupply> ReserveSupplies { get; init; }

    public EquatableArray<StartingSupply> LarderSupplies { get; init; }

    /// <summary>The one recovery queue: the clearance planner executes its removals against a fresh native census; its held entries are the journalled hold reasons.</summary>
    public RecoveryQueue? RecoveryQueue { get; init; }

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

    /// <summary>The combat standing this enabled review filed; null while combat has nothing to answer or settle.</summary>
    public RoundsCombat? Combat { get; init; }

    /// <summary>The roster planner's last recorded report: coverage and pawn profiles as of its tick. A disabled review keeps the last one.</summary>
    public WorkRosterReport? Roster { get; init; }

    /// <summary>Every active goal's progress record, keyed by need.</summary>
    public EquatableArray<ConcernProgress> Progress { get; init; }

    /// <summary>The shadow ready-work projection of this enabled review's plans and unserved goals. Diagnostics only: no admission reads it.</summary>
    public ReadyWorkReport? ReadyWork { get; init; }

    /// <summary>Every live building action standing built, by geometry, in the last complete construction census, sorted. Admission reads it for building prerequisites.</summary>
    public EquatableArray<ActionId> Built { get; init; }

    /// <summary>For every cell a live plan's wall removal names, whether the last complete construction census still had a colony Wall on it, sorted by cell.</summary>
    public EquatableArray<WallCell> WallCells { get; init; }
}

/// <summary>One requested inspection.</summary>
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

/// <summary>What a review committed.</summary>
public sealed record RoundsResult
{
    public Rounds Review { get; init; } = new();

    public RoundsFindings Needs { get; init; } = new();

    /// <summary>What an enabled review passed <c>DetectRounds</c>: the journal-enriched facts, the prior latches and the staged policy. Not journalled.</summary>
    public RoundsDetection? Detection { get; init; }

    /// <summary>
    /// The colonists whose activity orders the review's admission committed or released: the coupled selection's and
    /// standby's (#3599). Not journalled.
    /// </summary>
    public IReadOnlySet<string> Assigned { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>One <c>InspectRounds</c> call's input.</summary>
public sealed record RoundsDetection(RoundsFacts Facts, RoundsLatches Latches, RoundsPolicy Policy);
