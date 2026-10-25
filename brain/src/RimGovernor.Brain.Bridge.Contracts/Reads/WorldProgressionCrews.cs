using RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Bridge.Contracts;

/// <summary>The crews of the world census rows: a caravan's and a map's pawns, and a forming caravan's.</summary>
public static class WorldProgressionCrews
{
    public static IReadOnlyList<PawnId> Crew(this CaravanState caravan) => [.. caravan.Pawns.Select(pawn => new PawnId(pawn.Pawn.Id))];

    public static IReadOnlyList<PawnId> Colonists(this WorldMap map) => [.. map.Pawns.Select(pawn => new PawnId(pawn.Pawn.Id))];
}

