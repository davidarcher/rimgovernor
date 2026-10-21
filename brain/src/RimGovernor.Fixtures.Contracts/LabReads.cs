#nullable enable
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RimGovernor.Fixtures.Contracts;

// The replies of the lab and environment cases' bespoke fixture ops (#3403): native recorders, equality checks and
// benchmarks the stage primitives cannot express. A record carries only what a case reads.

/// <summary><c>test/grid_things</c>: the native cell-grid encoding of one staged row of every thing category.</summary>
public sealed record GridThingsRead
{
    /// <summary>The row as a base64 wire <c>mirror.CellGrid</c>.</summary>
    public string Grid { get; init; } = "";

    /// <summary>The wall frame's thing id, which the frame cell must list.</summary>
    public long FrameId { get; init; }

    /// <summary>The whole-map grid read's wall time (reported, never gated).</summary>
    public double ReadMs { get; init; }
}

/// <summary>The stacks a bill-ingredient pick and a meal pick chose.</summary>
public sealed record SpoilagePicks
{
    public string Bill { get; init; } = "";

    public string Meal { get; init; } = "";
}

/// <summary><c>test/spoilage_pick</c>: the vanilla-path picks with the spoilage preference off, on, and on with the old stacks forbidden.</summary>
public sealed record SpoilagePickRead
{
    public string FreshMeat { get; init; } = "";

    public string OldMeat { get; init; } = "";

    public string FreshMeal { get; init; } = "";

    public string OldMeal { get; init; } = "";

    public SpoilagePicks Off { get; init; } = new();

    public SpoilagePicks On { get; init; } = new();

    public SpoilagePicks Forbidden { get; init; } = new();
}

/// <summary>One forecast food read at a stockpile size and plant count.</summary>
public sealed record FoodRouteRow
{
    public int Cells { get; init; }

    public int Plants { get; init; }

    public int Searches { get; init; }

    public int Options { get; init; }

}

/// <summary><c>test/food_storage_routes</c>: the forecast food read's cost as the stockpile grows.</summary>
public sealed record FoodStorageRoutesRead
{
    public int Colonists { get; init; }

    public IReadOnlyList<FoodRouteRow> Rows { get; init; } = [];
}

/// <summary>One sampled leg priced by its origin's flood and by the game's A* search, with the same pawn.</summary>
public sealed record RouteFloodPair
{
    /// <summary>"(x, z) -> (x, z)".</summary>
    public string Leg { get; init; } = "";

    /// <summary>The flood's cost, or null when the flood did not reach the target.</summary>
    public double? Flood { get; init; }

    /// <summary>The A* search's cost, or null when it found no path.</summary>
    public double? Search { get; init; }

    /// <summary>The A* path's diagonal steps.</summary>
    public int Diagonal { get; init; }
}

/// <summary><c>test/route_floods</c>: a route matrix by per-origin floods against A* on sampled pairs.</summary>
public sealed record RouteFloodsRead
{
    public int Origins { get; init; }

    public int Floods { get; init; }

    /// <summary>The game-thread ms of the whole matrix (reported, never gated).</summary>
    public double MatrixMs { get; init; }

    public IReadOnlyList<RouteFloodPair> Pairs { get; init; } = [];
}

/// <summary><c>test/skill_learning_probe</c>: the learning projection against native SkillRecord learning on detached records.</summary>
public sealed record SkillLearningProbeRead
{
    public int Samples { get; init; }

    /// <summary>"skill/passion/saturated" of each sample the projection missed, and read_mutated_pawn.</summary>
    public IReadOnlyList<string> Failures { get; init; } = [];
}

/// <summary><c>test/skill_stat_curve_prepare</c>: the biography's medical stat curves against live native stat calls.</summary>
public sealed record SkillStatCurvePrepared
{
    /// <summary>The healthy colonist whose curves were compared.</summary>
    public string Qualified { get; init; } = "";

    /// <summary>The anesthetized colonist, whose pipeline no longer qualifies.</summary>
    public string Disqualified { get; init; } = "";

    /// <summary>Stat x level values compared.</summary>
    public int Samples { get; init; }

    /// <summary>Findings on the healthy colonist, "stat/level: curve vs live" disagreements and read_mutated_pawn.</summary>
    public IReadOnlyList<string> Failures { get; init; } = [];
}

/// <summary><c>test/skill_learning_verify</c>: the native XP callbacks the verifier checked against the projection.</summary>
public sealed record SkillLearningVerifyRead
{
    public int Recipe { get; init; }

    public int Sparring { get; init; }

    /// <summary>Surgery completions: the recipe callback of a medical bill (#3632).</summary>
    public int Surgery { get; init; }

    /// <summary>Completed tends (#3632).</summary>
    public int Tend { get; init; }

    public IReadOnlyList<string> Failures { get; init; } = [];
}

/// <summary><c>test/medical_learning</c>: whether the staged patient lies in bed and the doctor's native job was handed over.</summary>
public sealed record MedicalLearningRead
{
    public bool InBed { get; init; }

    public bool Started { get; init; }
}

/// <summary><c>test/training_prepare</c>: the staged range column(s) and their shooters.</summary>
public sealed record TrainingPrepared
{
    public string Pawn { get; init; } = "";

    public string Dummy { get; init; } = "";

    /// <summary>The shooter's own weapon, which every session must restore.</summary>
    public string Weapon { get; init; } = "";

    public int DummyMax { get; init; }

    /// <summary>The second column's shooter; empty without one.</summary>
    public string Neighbour { get; init; } = "";

    public string NeighbourDummy { get; init; } = "";

    /// <summary>The drafted mid-lane colonist; empty without one.</summary>
    public string Bystander { get; init; } = "";

    /// <summary>The sentinel walls' ids, comma-joined.</summary>
    public string Sentinels { get; init; } = "";
}

/// <summary><c>test/training_inspect</c>: one shooter's skill, hands and job and the range's hit points.</summary>
public sealed record TrainingRead
{
    public int Tick { get; init; }

    /// <summary>The native learning read left the skill record unchanged.</summary>
    public bool LearningReadOnly { get; init; }

    public bool ProjectionKnown { get; init; }

    public int ProjectedLevel { get; init; }

    public double ProjectedXp { get; init; }

    public int ShootingLevel { get; init; }

    public double ShootingXp { get; init; }

    public double ShootingMidnightXp { get; init; }

    public string Primary { get; init; } = "";

    /// <summary>Every def the shooter holds, equipped or carried.</summary>
    public IReadOnlyList<string> Held { get; init; } = [];

    public int PracticeOnMap { get; init; }

    public string Job { get; init; } = "";

    public int ShotsFired { get; init; }

    public int DummyHp { get; init; }

    public int SentinelHp { get; init; }

    public int SentinelMax { get; init; }

    /// <summary>The shooter holds the shared "trained with" thought.</summary>
    public bool TrainedWith { get; init; }

    public string OtherJob { get; init; } = "";

    public int BystanderHurt { get; init; }

    public double BystanderHealth { get; init; } = 1;
}

/// <summary><c>test/training_act</c> chat: vanilla's chat rule between two drillers and whether one chatted.</summary>
public sealed record TrainingChat
{
    /// <summary>Within 6 cells with a line of sight.</summary>
    public bool Good { get; init; }

    public bool Chatted { get; init; }
}

/// <summary><c>test/environment_temperature</c>: the sealed colonist and the ideoligion facts the card evaluator reads.</summary>
public sealed record EnvironmentTemperatureHeld
{
    public string Pawn { get; init; } = "";

    public bool Ideology { get; init; }

    public bool Tough { get; init; }

    public IReadOnlyList<string> Precepts { get; init; } = [];

    public IReadOnlyList<string> Traits { get; init; } = [];
}

/// <summary>
/// <c>test/hypothermia_tuning</c>: the Verse.HealthTuning hypothermia constants as the loaded game compiles them, and the
/// onset the first colonist's own SafeTemperatureRange applies (ComfortableTemperatureRange min less SafeTemperatureRange min).
/// </summary>
public sealed record HypothermiaTuningRead
{
    public double GrowthPerDegreeUnder { get; init; }

    public double MinProgress { get; init; }

    public double HarmfulOffsetC { get; init; }

    /// <summary>The pawn's comfortable minimum less its safe minimum: the onset HediffGiver_Hypothermia applies.</summary>
    public double SafeMinOffsetC { get; init; }

    /// <summary>HealthTuning.HediffGiverUpdateInterval: the ticks between hediff giver steps.</summary>
    public int IntervalTicks { get; init; }
}

/// <summary><c>test/deadmans_wear</c>: the redressed colonist, its worn and corpse-worn garment counts and its traits.</summary>
public sealed record DeadMansWorn
{
    public string Pawn { get; init; } = "";

    public int Worn { get; init; }

    public int Tainted { get; init; }

    public bool Bloodlust { get; init; }

    public bool Inhumanized { get; init; }

    public IReadOnlyList<string> Traits { get; init; } = [];
}

/// <summary>
/// The payload of the production <c>rimgovernor/acceptance_read_capture_profile</c> read (snake-case spans, as the
/// native profile writes them).
/// </summary>
public sealed record CaptureProfileRead
{
    public IReadOnlyDictionary<string, CaptureSpan> Spans { get; init; } = new Dictionary<string, CaptureSpan>();
}

/// <summary>One profiled span's median allocation.</summary>
public sealed record CaptureSpan
{
    [JsonPropertyName("bytes_median")]
    public long BytesMedian { get; init; }
}
