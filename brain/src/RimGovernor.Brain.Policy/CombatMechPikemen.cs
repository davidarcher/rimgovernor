namespace RimGovernor.Brain.Policy;

// Go: combat_mech_pikemen.go

public static partial class PolicyFunctions
{
    /// <summary>
    /// The pikemen's last charge: once every live hostile is a pikeman (Mech_Pikeman), every defender wearing a
    /// shield belt leaves its cell and attacks the nearest pikeman in melee; the belt stops the pikemen's long
    /// shots on the way in. Before that nothing changes.
    /// </summary>
    private static void PikemenCharge(CombatView view, CombatMemory m)
    {
        var pikemen = RankThreats(view);
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
            var s = state.GetValueOrDefault(r.Pawn) ?? new CombatPawnState();
            if (!s.ShieldBelt)
            {
                continue;
            }

            r.Cell = null;
            r.Home = null;
            r.Ranged = false;
            r.Target = pikemen[0].Id;
            if (s.Cell.TryGetValue(out var at))
            {
                var id = NearestHostile(view, at);
                if (!id.IsEmpty)
                {
                    r.Target = id;
                }
            }
        }
    }

    /// <summary>The live positional hostile nearest <paramref name="from"/>; empty with none.</summary>
    internal static PawnId NearestHostile(CombatView view, Cell from)
    {
        var down = DownPawns(view);
        PawnId best = default;
        long bestD = -1;
        foreach (var t in view.Positional.Items)
        {
            var id = t.Id;
            if (!t.Position.TryGetValue(out var c) || Positive(t.Dead) || Positive(t.Downed) || down.Contains(id))
            {
                continue;
            }

            var d = Distance2(from, c);
            if (bestD < 0 || d < bestD)
            {
                best = id;
                bestD = d;
            }
        }

        return best;
    }
}
