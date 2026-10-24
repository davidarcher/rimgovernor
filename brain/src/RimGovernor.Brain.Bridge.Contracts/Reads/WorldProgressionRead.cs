using RimGovernor.Contracts.Wire.Common;
using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>The crews of the world census rows: a caravan's and a map's pawns, and a forming caravan's.</summary>
public static class WorldProgressionCrews
{
    public static IReadOnlyList<PawnId> Crew(this CaravanState caravan) => [.. caravan.Pawns.Select(pawn => new PawnId(pawn.Pawn.Id))];

    public static IReadOnlyList<PawnId> Colonists(this WorldMap map) => [.. map.Pawns.Select(pawn => new PawnId(pawn.Pawn.Id))];
}

/// <summary>
/// The validated subset of one world-progression quest row the quest-accept boundary
/// needs. Absent optional fields stay null.
/// </summary>
public sealed partial record QuestOffer
{
    public double? ThreatPoints { get; init; }

    public string Id { get; init; } = "";

    /// <summary>The root QuestScriptDef defName, empty for a quest built without one.</summary>
    public string ScriptDef { get; init; } = "";

    public string State { get; init; } = "";

    public bool RequiresAccepter { get; init; }

    public bool CanAccept { get; init; }

    public int ChoiceCount { get; init; }

    public IReadOnlyList<string> EligiblePawnIds { get; init; } = [];

    public IReadOnlyList<string> DeparturePawnIds { get; init; } = [];

    /// <summary>The first non-player faction the quest involves, empty when none.</summary>
    public string FactionId { get; init; } = "";

    public int MapId { get; init; }

    /// <summary>False for a quest anchored only to a world object.</summary>
    public bool MapKnown { get; init; }

    /// <summary>The Empire favor each reward choice grants, ascending by choice, omitting choices that grant none.</summary>
    public IReadOnlyList<QuestFavorFact> Favor { get; init; } = [];

    public IReadOnlyList<QuestObjectiveFact> Objectives { get; init; } = [];

    public IReadOnlyList<QuestShuttleFact> Shuttles { get; init; } = [];

    public long? ExpiresInTicks { get; init; }

    public int? RewardChoiceParts { get; init; }

    public IReadOnlyList<QuestRewardFact> Rewards { get; init; } = [];

    public bool? AskerFactionPlayer { get; init; }

    public bool? ViolentQuestsAllowed { get; init; }
}

public sealed record QuestRewardFact
{
    public int Choice { get; init; }

    public IReadOnlyList<QuestTradeItemFact> Items { get; init; } = [];

    public int Goodwill { get; init; }

    public int Psylink { get; init; }

    public int PermitPoints { get; init; }

    public IReadOnlyList<string> Permits { get; init; } = [];
}

public sealed record QuestTradeItemFact(string Def, long Count);

/// <summary>Typed native progress; absent fields stay absent.</summary>
public sealed partial record QuestObjectiveFact
{
    public QuestMonument? Monument { get; init; }

    public QuestGravEngine? GravEngine { get; init; }

    public QuestSurveyScanner? SurveyScanner { get; init; }

    public IReadOnlyList<QuestHackTarget> HackTargets { get; init; } = [];

    public QuestGiftRequest? Gift { get; init; }

    public QuestHackRisk? HackRisk { get; init; }

    public QuestObjectiveKind Kind { get; init; }

    public string Def { get; init; } = "";

    public string Stuff { get; init; } = "";

    public long? Count { get; init; }

    public long? Produced { get; init; }

    public long? DeadlineTicks { get; init; }

    public long? DurationTicks { get; init; }

    public QuestWorkload? Workload { get; init; }

    public string UnmetRequirement { get; init; } = "";

    public IReadOnlyList<string> PawnIds { get; init; } = [];

    public bool? Active { get; init; }

    public double? MinimumMood { get; init; }

    public IReadOnlyList<QuestLodgerMoodFact> LodgerMoods { get; init; } = [];
}

public sealed record QuestLodgerMoodFact(double? Mood);

public sealed record QuestShuttleFact
{
    public bool? AutoloadAvailable { get; init; }

    public bool? Autoload { get; init; }

    public bool? Loading { get; init; }

    public bool? AllRequiredLoaded { get; init; }

    public bool? ManualLaunchAvailable { get; init; }

    public IReadOnlyList<string> LoadedPawnIds { get; init; } = [];

    public IReadOnlyList<string> PendingPawnIds { get; init; } = [];
}

/// <summary>The royal favor one reward choice grants in total.</summary>
public sealed record QuestFavorFact(int Choice, int Favor);

/// <summary>The validated subset of one factions row the quest planners join to a quest's faction id.</summary>
public sealed record FactionFact(string Id, bool Player, bool Hostile);

/// <summary>
/// The validated subset of one world progression census the world-site and
/// quest boundaries need. The maps, caravans and assemblies are the census rows themselves; a map's Tile is the
/// codebase's only home-tile source (the row with Home true).
/// </summary>
public sealed partial record WorldProgressionRead
{
    public IReadOnlyList<CaravanAssembly> Assemblies { get; init; } = [];

    public IReadOnlyList<WorldSite> Sites { get; init; } = [];

    public ObservationContext? Context { get; init; }

    public IReadOnlyList<WorldMap> Maps { get; init; } = [];

    public IReadOnlyList<CaravanState> Caravans { get; init; } = [];

    public IReadOnlyList<QuestOffer> Quests { get; init; } = [];

    public IReadOnlyList<FactionFact> Factions { get; init; } = [];
}
