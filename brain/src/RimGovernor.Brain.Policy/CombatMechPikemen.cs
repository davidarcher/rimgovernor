namespace RimGovernor.Brain.Policy;


public static partial class CombatMechRules
{
    /// <summary>
    /// The pikemen's last charge: once every live hostile is a pikeman (Mech_Pikeman), every defender wearing a
    /// shield belt leaves its cell and attacks the nearest pikeman in melee; the belt stops the pikemen's long
    /// shots on the way in. Before that nothing changes.
    /// </summary>
    internal static void PikemenCharge(CombatView view, CombatMemory m)
    {
        var pikemen = CombatThreatRules.RankThreats(view);
        if (pikemen.Count == 0 || pikemen.Any(h => !h.Kind.StartsWith("Mech_Pikeman", StringComparison.Ordinal)))
        {
            return;
        }

        var state = new Dictionary<PawnId, CombatPawnState>();
        foreach (var p in view.Pawns.Items)
        {
            state[p.Id] = p;
        }

        foreach (var r in m.Roles)
        {
            if (!state.TryGetValue(r.Pawn, out var s) || s.Presence is not CombatPresence.Engaged { Row.Shield: ShieldBelt.Worn })
            {
                continue;
            }

            r.Cell = null;
            r.Home = null;
            r.Ranged = false;
            r.Target = pikemen[0].Id;
            var id = NearestHostile(view, s.Cell);
            if (!id.IsEmpty)
            {
                r.Target = id;
            }
        }
    }

    /// <summary>The live positional hostile nearest <paramref name="from"/>; empty with none.</summary>
    internal static PawnId NearestHostile(CombatView view, Cell from)
    {
        var down = CombatManhunterRules.DownPawns(view);
        PawnId best = default;
        long bestD = -1;
        foreach (var t in view.Positional.Items)
        {
            var id = t.Id;
            if (!t.Position.TryGetValue(out var c) || RoundsRules.Positive(t.Dead) || RoundsRules.Positive(t.Downed) || down.Contains(id))
            {
                continue;
            }

            var d = CombatManhunterRules.Distance2(from, c);
            if (bestD < 0 || d < bestD)
            {
                best = id;
                bestD = d;
            }
        }

        return best;
    }
}
