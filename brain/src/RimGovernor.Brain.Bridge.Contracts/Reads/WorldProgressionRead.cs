using RimGovernor.Contracts.Wire.Common;
using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>
/// Go's <c>WorldRouteFact</c>: one native player-home route row from a caravan's home_routes census.
/// <see cref="EstimatedTicksKnown"/> is false when native reports no travel-time estimate for that route.
/// </summary>
public sealed record WorldRouteFact(string SettlementId, int DestinationTile, bool Reachable, long EstimatedTicks, bool EstimatedTicksKnown);

/// <summary>
/// Go's <c>CaravanPawnFact</c>: one crew member's identity and dead/downed status. An absent status stays
/// unknown (<c>DeadKnown</c>/<c>DownedKnown</c> false), which the world-evaluation advisory treats as not proven alive.
/// </summary>
public sealed record CaravanPawnFact(string Id, bool Dead, bool DeadKnown, bool Downed, bool DownedKnown);

/// <summary>
/// Go's <c>CaravanJourney</c>: the validated subset of one player caravan's world-progression census row that
/// travel/arrival tracking and the world-evaluation advisory need. <see cref="Inventory"/> is the full per-caravan
/// cargo census (defName to units); an absent row is a known zero.
/// </summary>
public sealed record CaravanJourney
{
    public int? Destination { get; init; }

    public string Id { get; init; } = "";

    public int Tile { get; init; }

    public bool Moving { get; init; }

    public IReadOnlyList<string> PawnIds { get; init; } = [];

    public IReadOnlyList<CaravanPawnFact> Pawns { get; init; } = [];

    public double FoodDays { get; init; }

    public bool FoodDaysKnown { get; init; }

    public IReadOnlyList<WorldRouteFact> HomeRoutes { get; init; } = [];

    public IReadOnlyList<WorldRouteFact> SettlementRoutes { get; init; } = [];

    public Fact<double> MassUsage { get; init; }

    public Fact<double> MassCapacity { get; init; }

    public Fact<double> FoodRotDays { get; init; }

    public IReadOnlyDictionary<string, long> Inventory { get; init; } = new Dictionary<string, long>(StringComparer.Ordinal);
}

/// <summary>
/// Go's <c>QuestOffer</c>: the validated subset of one world-progression quest row the quest-accept boundary
/// needs. Absent optional fields stay null.
/// </summary>
public sealed record QuestOffer
{
    public double? ThreatPoints { get; init; }

    public string Id { get; init; } = "";

    /// <summary>The root QuestScriptDef defName, empty for a quest built without one.</summary>
    public string ScriptDef { get; init; } = "";

    public string State { get; init; } = "";

    public bool RequiresAccepter { get; init; }

    public bool CanAccept { get; init; }

    public int ChoiceCount { get; init; }

    public IReadOnlyList<QuestTradeRequestFact> TradeRequests { get; init; } = [];

    public IReadOnlyList<string> EligiblePawnIds { get; init; } = [];

    public IReadOnlyList<string> DeparturePawnIds { get; init; } = [];

    public string SnapshotToken { get; init; } = "";

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

    public string Asker { get; init; } = "";

    public bool? AskerFactionPlayer { get; init; }

    public bool? ViolentQuestsAllowed { get; init; }
}

/// <summary>Go's <c>QuestRewardFact</c>.</summary>
public sealed record QuestRewardFact
{
    public int Choice { get; init; }

    public IReadOnlyList<QuestTradeItemFact> Items { get; init; } = [];

    public int Goodwill { get; init; }

    public int Psylink { get; init; }

    public int PermitPoints { get; init; }

    public IReadOnlyList<string> Permits { get; init; } = [];

    public string TitleDef { get; init; } = "";

    public string FactionId { get; init; } = "";
}

/// <summary>Go's <c>QuestTradeItemFact</c>.</summary>
public sealed record QuestTradeItemFact(string Def, long Count);

/// <summary>Go's <c>QuestObjectiveFact</c>: typed native progress; absent fields stay absent.</summary>
public sealed record QuestObjectiveFact
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

/// <summary>Go's <c>QuestLodgerMoodFact</c>.</summary>
public sealed record QuestLodgerMoodFact(string PawnId, double? Mood);

/// <summary>Go's <c>QuestShuttleFact</c>.</summary>
public sealed record QuestShuttleFact
{
    public string Id { get; init; } = "";

    public bool? AutoloadAvailable { get; init; }

    public bool? Autoload { get; init; }

    public bool? Loading { get; init; }

    public bool? AllRequiredLoaded { get; init; }

    public bool? ManualLaunchAvailable { get; init; }

    public IReadOnlyList<string> PawnIds { get; init; } = [];

    public IReadOnlyList<string> LoadedPawnIds { get; init; } = [];

    public IReadOnlyList<string> PendingPawnIds { get; init; } = [];

    public int? RequiredColonistCount { get; init; }
}

/// <summary>Go's <c>QuestFavorFact</c>: the royal favor one reward choice grants in total.</summary>
public sealed record QuestFavorFact(int Choice, int Favor);

/// <summary>Go's <c>FactionFact</c>: the validated subset of one factions row the quest planners join to a quest's faction id.</summary>
public sealed record FactionFact(string Id, bool Player, bool Hostile);

/// <summary>
/// Go's <c>QuestTradeRequestFact</c>: one native settlement trade objective row, the required resource def name
/// and count and the settlement's destination tile.
/// </summary>
public sealed record QuestTradeRequestFact(string Resource, long Count, int DestinationTile);

/// <summary>
/// Go's <c>WorldMap</c>: the validated subset of one maps row that failure-recovery classification needs. Tile is
/// the codebase's only home-tile source: the row with <see cref="Home"/> true.
/// </summary>
public sealed record WorldMap
{
    public int Id { get; init; }

    public int Tile { get; init; }

    public bool Home { get; init; }

    public IReadOnlyList<string> PawnIds { get; init; } = [];

    public IReadOnlyList<QuestWorker> QuestWorkers { get; init; } = [];
}

/// <summary>Go's <c>CaravanAssemblyFact</c>.</summary>
public sealed record CaravanAssemblyFact(string Id, int MapId, IReadOnlyList<string> PawnIds);

/// <summary>
/// Go's <c>WorldProgressionRead</c>: the validated subset of one world progression census the world-site and
/// quest boundaries need. The read and its decoders are wire plumbing and not ported.
/// </summary>
public sealed record WorldProgressionRead
{
    public IReadOnlyList<CaravanAssemblyFact> Assemblies { get; init; } = [];

    public IReadOnlyList<WorldSite> Sites { get; init; } = [];

    public ObservationContext? Context { get; init; }

    public IReadOnlyList<WorldMap> Maps { get; init; } = [];

    public IReadOnlyList<CaravanJourney> Caravans { get; init; } = [];

    public IReadOnlyList<QuestOffer> Quests { get; init; } = [];

    public IReadOnlyList<FactionFact> Factions { get; init; } = [];
}
