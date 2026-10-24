using RimGovernor.Contracts.Wire.Observations;
using PolicyCreepJoiner = RimGovernor.Brain.Policy.CreepJoiner;
using PolicyCreepJoinerPawn = RimGovernor.Brain.Policy.CreepJoinerPawn;

namespace RimGovernor.Brain.Bridge.Contracts;

public static class CreepJoinerFacts
{
    /// <summary>
    /// Lifts a pawn row into the downside check's facts. A row with inactive Anomaly is a game without Anomaly:
    /// no creepjoiner. A failed creepjoiner read stays unknown. The hediffs are the visible ones.
    /// </summary>
    public static PolicyCreepJoinerPawn CreepJoinerPawn(PawnState row)
    {
        var result = new PolicyCreepJoinerPawn
        {
            CreepJoiner = Known<PolicyCreepJoiner?>(null),
            Traits = Known(EquatableArray.From(row.Biography.Traits.Select(trait => trait.DefName))),
            Hediffs = Known(EquatableArray.From(row.Health.Hediffs.Where(hediff => hediff.Visible).Select(hediff => hediff.DefName))),
        };
        // The fact's known flag is discarded: an inactive anomaly reads as the zero value, no creepjoiner fact.
        if (AnomalyFactsRules.PawnAnomaly(row.Anomaly).TryGetValue(out var lifted))
        {
            result = result with { CreepJoiner = lifted.CreepJoiner };
        }
        return result;
    }
}
