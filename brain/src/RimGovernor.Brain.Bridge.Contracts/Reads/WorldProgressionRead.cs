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
/// The validated subset of one world progression census the world-site boundaries
/// need. The maps, caravans, assemblies and quests are the census rows themselves; a map's Tile is the
/// codebase's only home-tile source (the row with Home true).
/// </summary>
public sealed partial record WorldProgressionRead
{
    public IReadOnlyList<CaravanAssembly> Assemblies { get; init; } = [];

    public IReadOnlyList<WorldSite> Sites { get; init; } = [];

    public required ObservationContext Context { get; init; }

    public IReadOnlyList<WorldMap> Maps { get; init; } = [];

    public IReadOnlyList<CaravanState> Caravans { get; init; } = [];

    public IReadOnlyList<QuestState> Quests { get; init; } = [];
}
