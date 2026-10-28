#nullable enable
using System.Collections.Generic;

namespace RimGovernor.Fixtures.Contracts;

// The replies of the served root cases' bespoke fixture ops (#3398): each prepares physical facts the stage primitives
// cannot express and records what the native game did while the brain service drove it. A record carries only what a
// case reads.

/// <summary>The reply of a fixture op that only prepares or injects: whether it ran.</summary>
public sealed record FixtureDone
{
    public bool Success { get; init; }
}

/// <summary>
/// <c>test/coordinated_work_prepare</c>: whether it ran and, for construction, the staged barracks site (#3537): the
/// south-west cell of its 3x3 interior, whose centre is a lit campfire, and its door cell mid south.
/// </summary>
public sealed record CoordinationPrepared
{
    public bool Success { get; init; }

    public int X { get; init; }

    public int Z { get; init; }

    public int DoorX { get; init; }

    public int DoorZ { get; init; }

    public float OutdoorTemperature { get; init; }

    public IReadOnlyList<CoordinationComfort> Comfort { get; init; } = [];
}

/// <summary>The native comfort and hypothermia-onset minimum for a prepared pawn, including its worn apparel.</summary>
public sealed record CoordinationComfort(string Pawn, float ComfortableMin, float SafeMin);

/// <summary><c>test/coordinated_work_read</c>: a coordinated food or construction chain as the native game ran it.</summary>
public sealed record CoordinationRead
{
    public int Tick { get; init; }

    /// <summary>Planner work became player-forced.</summary>
    public bool Forced { get; init; }

    /// <summary>Harvested food lying at the selected storage cell.</summary>
    public int PhysicalFood { get; init; }

    /// <summary>Player beds and walls standing.</summary>
    public int PhysicalBuildings { get; init; }

    /// <summary>"Kind:Job" of each step of an operation the native game completed.</summary>
    public IReadOnlyList<string> FinishedKinds { get; init; } = [];

    /// <summary>One batch handed tasks to more than one pawn.</summary>
    public bool Joint { get; init; }

    /// <summary>Items delivered or supplied by completed operations.</summary>
    public int Delivered { get; init; }

    /// <summary>Participants still owned by the coordinated Method.</summary>
    public int Active { get; init; }

    public IReadOnlyList<string> Participants { get; init; } = [];

    public IReadOnlyList<string> Released { get; init; } = [];

    /// <summary>The fixture ended one carrying delivery's job.</summary>
    public bool Interrupted { get; init; }

    /// <summary>A later revision of the interrupted assignment carried the delivery again.</summary>
    public bool Recovered { get; init; }
}

/// <summary><c>test/clothing_activity_read</c>: the garment the native recipe made from the planner's material.</summary>
public sealed record ClothingActivityRead
{
    public int Tick { get; init; }

    /// <summary>One garment of the selected product and stuff exists.</summary>
    public bool Produced { get; init; }

    /// <summary>Loose, carried and unfinished input equals the prepared stock less the recipe's need.</summary>
    public bool Conserved { get; init; }

    /// <summary>Vanilla DoBill ran without an exact pawn activity.</summary>
    public bool NativeExecution { get; init; }

    /// <summary>The fixture interrupted the unfinished garment once.</summary>
    public bool Interrupted { get; init; }

    /// <summary>Workers that resumed the interrupted unfinished garment.</summary>
    public int Resumed { get; init; }
}

/// <summary><c>test/need_capacity_read</c>: vanilla reservations on one meal stack and then one single-slot bed.</summary>
public sealed record NeedCapacityRead
{
    public int Tick { get; init; }

    /// <summary>Pawns that ate from the stack.</summary>
    public int Ate { get; init; }

    /// <summary>The most pawns holding the stack's reservation at once.</summary>
    public int MaxStackReservers { get; init; }

    /// <summary>The bed's native sleeping slots.</summary>
    public int BedSlots { get; init; }

    public int MaxBedReservers { get; init; }

    public int MaxBedSleepers { get; init; }

    /// <summary>Pawns that slept in the bed.</summary>
    public int BedSlept { get; init; }

    /// <summary>Ticks since both pawns were tired; -1 before.</summary>
    public int SinceBothTired { get; init; }
}

/// <summary><c>test/shared_stack_read</c>: two Methods eating from one meal stack, the second bound while the first still owes bites of it (#3194).</summary>
public sealed record SharedStackRead
{
    public int Tick { get; init; }

    /// <summary>Units left in the stack; 0 once it is gone.</summary>
    public int Units { get; init; }

    /// <summary>Coupled Methods whose applied tasks eat from the stack.</summary>
    public IReadOnlyList<string> Methods { get; init; } = [];

    /// <summary>Pawns that ingested the stack under a coupled Method.</summary>
    public int Eaters { get; init; }

    /// <summary>A second Method's bite was applied while the first pawn's Method still owed bites of the stack.</summary>
    public bool SharedWhileOwned { get; init; }
}

/// <summary><c>test/maintenance_read</c>: the role holder's native practice and draft interruption.</summary>
public sealed record MaintenanceRead
{
    public int Tick { get; init; }

    /// <summary>Methods that owned the holder's practice.</summary>
    public IReadOnlyList<string> Methods { get; init; } = [];

    /// <summary>The holder's skill reached its native floor.</summary>
    public bool Earned { get; init; }

    /// <summary>The holder's native activity state, "unowned" when none.</summary>
    public string Activity { get; init; } = "";

    public bool Drafted { get; init; }

    /// <summary>The holder's original weapon is back in hand after the draft.</summary>
    public bool Restored { get; init; }
}

/// <summary><c>test/service_shelter_prepare</c>: the cold room's 3x3 interior corner and its door, for the case's layout plan.</summary>
public sealed record ServiceShelterPrepared
{
    public bool Success { get; init; }

    public int X { get; init; }

    public int Z { get; init; }

    public int DoorX { get; init; }

    public int DoorZ { get; init; }

}

/// <summary><c>test/service_shelter_read</c>: the heater frame's native work, power and the room's temperature.</summary>
public sealed record ServiceShelterRead
{
    public int Tick { get; init; }

    /// <summary>A coupled Method's pawn did FinishFrame work on the heater.</summary>
    public bool Worked { get; init; }

    public bool Completed { get; init; }

    public bool Powered { get; init; }

    public float Temperature { get; init; }

    /// <summary>The unrelated spare wall frame still stands.</summary>
    public bool SparePresent { get; init; }

    public float SpareRemainingWork { get; init; }
}

/// <summary><c>test/productive_cooking_read</c>: the prepared cooking development as native recipes, skill and ownership.</summary>
public sealed record ProductiveCookingRead
{
    public int Tick { get; init; }

    /// <summary>Coupled Methods whose pawns learned Cooking on the prepared bill.</summary>
    public IReadOnlyList<string> Methods { get; init; } = [];

    /// <summary>Native learning that differed from the counterfactual rates.</summary>
    public IReadOnlyList<string> LearningFailures { get; init; } = [];

    /// <summary>Meals of the bill's product exist.</summary>
    public bool Output { get; init; }

    /// <summary>A cook learned on the bill at least twice.</summary>
    public bool SubsequentWork { get; init; }

    public int Completions { get; init; }

    /// <summary>Completions per colonist load id.</summary>
    public IReadOnlyDictionary<string, int> CompletedBy { get; init; } = new Dictionary<string, int>();

    /// <summary>The highest colonist Cooking level.</summary>
    public int MaxCookingLevel { get; init; }

    /// <summary>A colonist slept after the rest urge.</summary>
    public bool Slept { get; init; }

    /// <summary>Cooking jobs begun while critically tired after the urge.</summary>
    public int TiredCooks { get; init; }

    /// <summary>Tasks already handed out at the urge.</summary>
    public int CommittedAtUrge { get; init; }

    /// <summary>Cooking jobs begun rested after the urge.</summary>
    public int RestedCooksAfterUrge { get; init; }

    public bool BillActive { get; init; }

    /// <summary>Repeats left on the bill.</summary>
    public int BillRemaining { get; init; }

    /// <summary>The Method owning each colonist a native activity owns, by load id.</summary>
    public IReadOnlyDictionary<string, string> Owners { get; init; } = new Dictionary<string, string>();

    /// <summary>Colonists on the bill's job.</summary>
    public int Cooking { get; init; }
}

/// <summary><c>test/portfolio_needs_read</c>: the hungry, tired worker's harvest, meal, sleep and recreation.</summary>
public sealed record PortfolioNeedsRead
{
    public int Tick { get; init; }

    /// <summary>Coupled Methods the native activities applied.</summary>
    public IReadOnlyList<string> Methods { get; init; } = [];

    /// <summary>A delivery or meal step bound to the harvested rice.</summary>
    public bool Rebound { get; init; }

    public bool Ate { get; init; }

    public bool Slept { get; init; }

    public bool Woke { get; init; }

    public bool Played { get; init; }
}

/// <summary><c>test/portfolio_thermal_prepare</c> and <c>_read</c>: the leaky room's roof, heat and occupied sleep.</summary>
public sealed record ThermalRead
{
    public int Tick { get; init; }

    /// <summary>The bed cell's temperature after the prepared leaky baseline.</summary>
    public float Baseline { get; init; }

    /// <summary>The warmest bed cell while the sleeper slept under the closed roof.</summary>
    public float OccupiedPeak { get; init; }

    public bool RoofComplete { get; init; }

    public int OpenRoof { get; init; }

    public bool Slept { get; init; }

    /// <summary>Coupled Methods with a BuildRoof step on a missing cell.</summary>
    public IReadOnlyList<string> Methods { get; init; } = [];

    public bool Powered { get; init; }
}

/// <summary>
/// <c>test/sleeping_setup</c>: the warm dormitory beside the colonists. With <c>couple</c>, the bedless lovers and their
/// vacant double bed; with <c>connectedRooms</c>, the corridor joining two bed chambers and the cell one past their east
/// wall (inside the Home margin).
/// </summary>
public sealed record SleepingPrepared
{
    public bool Success { get; init; }

    public string Pawn { get; init; } = "";

    public string? Partner { get; init; }

    public string? DoubleBed { get; init; }

    public StageCell Corridor { get; init; }

    public StageCell Outside { get; init; }
}
