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
/// needs. Rewards holds one row per option of the quest's reward choice (its position is the QuestAccept
/// reward index); Favor is the royal favor the quest grants whatever option is taken.
/// </summary>
public sealed partial record QuestOffer
{
    public required QuestThreat Threat { get; init; }

    public required string Id { get; init; }

    /// <summary>The root QuestScriptDef defName.</summary>
    public required string ScriptDef { get; init; }

    public required string State { get; init; }

    public required bool RequiresAccepter { get; init; }

    public required bool CanAccept { get; init; }

    public required IReadOnlyList<string> EligiblePawnIds { get; init; }

    public required IReadOnlyList<string> DeparturePawnIds { get; init; }

    /// <summary>The first non-player faction the quest involves, empty when none.</summary>
    public required string FactionId { get; init; }

    public required QuestPlace Place { get; init; }

    public required int Favor { get; init; }

    public required IReadOnlyList<QuestObjectiveFact> Objectives { get; init; }

    public required IReadOnlyList<QuestShuttleState> Shuttles { get; init; }

    public required QuestExpiry Expiry { get; init; }


    public required int RewardChoiceParts { get; init; }

    public required IReadOnlyList<QuestReward> Rewards { get; init; }

    public required DecreeAsker Asker { get; init; }

    public required bool ViolentQuestsAllowed { get; init; }
}

/// <summary>Typed native progress; the monument, grav-engine, scanner, hack, gift and workload blocks stay absent for other kinds.</summary>
public sealed partial record QuestObjectiveFact
{
    public QuestMonument? Monument { get; init; }

    public QuestGravEngine? GravEngine { get; init; }

    public QuestSurveyScanner? SurveyScanner { get; init; }

    public IReadOnlyList<QuestHackTarget> HackTargets { get; init; } = [];

    public QuestGiftRequest? Gift { get; init; }

    public QuestHackRisk? HackRisk { get; init; }

    public required QuestObjectiveKind Kind { get; init; }

    public required QuestObjectiveGoal Goal { get; init; }

    public required QuestObjectiveTiming Timing { get; init; }

    public QuestWorkload? Workload { get; init; }

    public required IReadOnlyList<string> PawnIds { get; init; }

    public required bool Active { get; init; }

    public required QuestMoodFloor MinimumMood { get; init; }

    public required IReadOnlyList<QuestLodgerMood> LodgerMoods { get; init; }
}


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

    public required ObservationContext Context { get; init; }

    public IReadOnlyList<WorldMap> Maps { get; init; } = [];

    public IReadOnlyList<CaravanState> Caravans { get; init; } = [];

    public IReadOnlyList<QuestOffer> Quests { get; init; } = [];

    public IReadOnlyList<FactionFact> Factions { get; init; } = [];
}
