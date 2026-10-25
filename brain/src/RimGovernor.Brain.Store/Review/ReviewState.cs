using RimGovernor.Brain.Domain;
using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Store;

/// <summary>
/// The review's combat standing (#3311): what the <c>military/combat</c> Need's fight runner (the defense entry) answers.
/// <see cref="Situation"/> is <see cref="Rules.CombatSituation"/> over the review's hostiles and hunt
/// request; <see cref="Prey"/> is the food plan's hunt prey while no hostile stands and <see cref="Hunters"/> the gunners of the
/// roster the plan chose for it (the only pawns the hunt may send). A review files one while the
/// situation is active or the Need still holds open work (a fight's drafts), and none otherwise.
/// </summary>
public sealed record ReviewCombat(Situation Situation, EquatableArray<PawnId> Prey, EquatableArray<PawnId> Hunters);

/// <summary>
/// The durable review cursor and hysteresis history. A field omitted when empty is its empty value.
/// </summary>
public sealed record ReviewState
{
    public WorkAdmissionEvidence? WorkEvidence { get; init; }

    /// <summary>The loose things the last known safety census reported unsafe to haul; the unsafe-loot Safeguard refuses allowing them at dispatch.</summary>
    public EquatableArray<string> Unsafe { get; init; }

    public bool BrewingFinished { get; init; }

    public EquatableArray<ResourceRunwayRecord> ResourceRunways { get; init; }

    public EquatableArray<ReserveSupply> ReserveSupplies { get; init; }

    public EquatableArray<StartingSupply> LarderSupplies { get; init; }

    /// <summary>The forbidden wastepacks the biotech/pollution Need may allow (PollutionAllowSupplies over this enabled review's read).</summary>
    public EquatableArray<StartingSupply> PollutionSupplies { get; init; }

    /// <summary>The one recovery queue: the clearance planner executes its removals against a fresh native census; its held entries are the journalled hold reasons.</summary>
    public RecoveryQueue? RecoveryQueue { get; init; }

    public Recovery? Recovery { get; init; }

    public DisasterHistory? Disaster { get; init; }

    public Mood? Mood { get; init; }

    public EquatableArray<MoodMethod> MoodMethods { get; init; }

    public SleepingHistory Sleeping { get; init; } = new();

    public ulong Revision { get; init; }

    public GenerationSnapshot Snapshot { get; init; }

    public Tick Tick { get; init; }

    /// <summary>The last full enabled inspection, not refreshed by an immediate review. Zero means no full inspection in this world yet.</summary>
    public Tick OrdinaryTick { get; init; }

    public bool Immediate { get; init; }

    public bool Enabled { get; init; }

    public Latches Latches { get; init; } = new();

    public MedicalCareHistory MedicalCare { get; init; } = new();

    public EventLootHistory EventLoot { get; init; } = new();

    public ComfortHistory Comfort { get; init; }

    /// <summary>The combat standing this enabled review filed; null while combat has nothing to answer or settle.</summary>
    public ReviewCombat? Combat { get; init; }

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
public sealed record ReviewRequest
{
    public bool Immediate { get; init; }

    public ulong Revision { get; init; }

    public GenerationSnapshot Current { get; init; }

    public Tick Tick { get; init; }

    public bool Enabled { get; init; }

    public PlannerSettings Policy { get; init; } = new();

    public ReviewFacts Facts { get; init; } = new();
}

/// <summary>What a review committed.</summary>
public sealed record ReviewResult
{
    public ReviewState Review { get; init; } = new();

    public Findings Needs { get; init; } = new();

    /// <summary>What an enabled review passed <c>DetectReview</c>: the journal-enriched facts, the prior latches and the staged policy. Not journalled.</summary>
    public Detection? Detection { get; init; }

    /// <summary>
    /// What the colony's first layout plan holds the clock for (#3652): the station fact its derive found unread, or the
    /// recorded plan no review has planned with yet; empty holds nothing. Not journalled.
    /// </summary>
    public string FirstPlanUnread { get; init; } = "";
}

/// <summary>One <c>InspectReview</c> call's input.</summary>
public sealed record Detection(ReviewFacts Facts, Latches Latches, PlannerSettings Policy);
