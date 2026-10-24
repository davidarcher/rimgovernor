using RimGovernor.Contracts.Wire.Observations;
using PolicyCreepJoinerPawn = RimGovernor.Brain.Policy.CreepJoinerPawn;

namespace RimGovernor.Brain.Bridge.Contracts;

public static class CreepJoinerFacts
{
    /// <summary>
    /// Lifts a pawn row into the downside check's facts. A row with inactive Anomaly is a game without Anomaly:
    /// no creepjoiner. The hediffs are the visible ones.
    /// </summary>
    public static PolicyCreepJoinerPawn CreepJoinerPawn(PawnState row) => new()
    {
        CreepJoiner = row.Anomaly is PawnAnomalyState.Active { Facts: var anomaly } ? anomaly.Creepjoiner : null,
        Traits = EquatableArray.From(row.Biography.Traits.Select(trait => trait.DefName)),
        Hediffs = EquatableArray.From(row.Health.Hediffs.Where(hediff => hediff.Visible).Select(hediff => hediff.DefName)),
    };
}
