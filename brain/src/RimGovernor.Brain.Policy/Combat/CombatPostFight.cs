using RimGovernor.Brain.GameFacts.Mirror;

namespace RimGovernor.Brain.Policy;

public static partial class CombatThreatRules
{
    /// <summary>The standing colonist already targeting this raider, else the nearest one; ties use ordinal id.</summary>
    public static CombatPawn? PostFightColonist(IReadOnlyList<CombatPawn> pawns, CombatPawn raider)
    {
        CombatPawn? best = null;
        var bestTargeting = false;
        decimal bestDistance = 0;
        foreach (var pawn in pawns)
        {
            if (pawn.Side != CombatSide.Colonist || pawn.Downed || pawn.Dead)
            {
                continue;
            }
            var targeting = pawn.TargetId == raider.Id.Value;
            decimal dx = (decimal)pawn.Cell.X - raider.Cell.X;
            decimal dz = (decimal)pawn.Cell.Z - raider.Cell.Z;
            var distance = (dx * dx) + (dz * dz);
            if (best is null
                || (targeting && !bestTargeting)
                || (targeting == bestTargeting && ((!targeting && distance < bestDistance)
                    || ((targeting || distance == bestDistance) && string.CompareOrdinal(pawn.Id.Value, best.Id.Value) < 0))))
            {
                best = pawn;
                bestTargeting = targeting;
                bestDistance = distance;
            }
        }
        return best;
    }
}