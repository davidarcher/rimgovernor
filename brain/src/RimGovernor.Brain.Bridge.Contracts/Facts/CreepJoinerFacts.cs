using RimGovernor.Contracts.Wire.Observations;
using PolicyCreepJoiner = RimGovernor.Brain.Policy.CreepJoiner;
using PolicyCreepJoinerPawn = RimGovernor.Brain.Policy.CreepJoinerPawn;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go creepjoiner_pawn.go.
public static partial class BridgeFunctions
{
    /// <summary>
    /// Go's <c>CreepJoinerPawn</c>: lifts a combat-detail pawn row into the downside check's facts. A row with no
    /// Anomaly block is a game without Anomaly: no creepjoiner. A failed creepjoiner read stays unknown. The
    /// hediffs are the visible ones, known only from a complete hediff read.
    /// </summary>
    public static PolicyCreepJoinerPawn CreepJoinerPawn(PawnState row)
    {
        var result = new PolicyCreepJoinerPawn
        {
            CreepJoiner = Known<PolicyCreepJoiner?>(null),
            Traits = Unknown<EquatableArray<string>>(),
            Hediffs = Unknown<EquatableArray<string>>(),
        };
        if (row.Anomaly is { } anomaly)
        {
            // Go discards the fact's known flag: an unknown anomaly reads as the zero value, no creepjoiner fact.
            if (PawnAnomaly(anomaly).TryGetValue(out var lifted))
            {
                result = result with { CreepJoiner = lifted.CreepJoiner };
            }
        }
        if (row.Biography is { } biography && !FailedFields(biography.Issues).Contains("traits"))
        {
            result = result with { Traits = Known(EquatableArray.From(biography.Traits.Select(trait => (trait.DefName ?? "")))) };
        }
        if (row.Health is { } health
            && !FailedFields(row.Issues).Contains("health")
            && !FailedFields(health.Issues).Contains("hediffs")
            && health.HediffCompleteness is { Filtered: 0 })
        {
            var hediffs = new List<string>();
            var known = true;
            foreach (var hediff in health.Hediffs)
            {
                if (hediff.Visible is null)
                {
                    known = false;
                    break;
                }
                if (hediff.Visible.GetValueOrDefault())
                {
                    hediffs.Add((hediff.DefName ?? ""));
                }
            }
            if (known)
            {
                result = result with { Hediffs = Known(EquatableArray.From(hediffs)) };
            }
        }
        return result;
    }
}
