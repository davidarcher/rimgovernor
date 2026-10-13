namespace RimGovernor.Brain.Policy;

// Go: combat_lure.go

public static partial class PolicyFunctions
{
    /// <summary>
    /// The hold's lure step against any outranging ranged raid: mechs or sniper parties. While a live hostile
    /// outranges our longest gunner and no live hostile is within that range of any firing cell, each gunner on
    /// a firing cell waits in cover on its inner-line cell with no target, so the raiders walk in instead of
    /// shooting from beyond our reach. Once a hostile comes within our range of the line, the lured gunners
    /// return to their firing cells and focus fire picks their targets. A raid we outrange never triggers it.
    /// Roles already fallen back are left alone.
    /// </summary>
    private static void Lure(CombatView view, CombatMemory m)
    {
        if (m.Tactic != CombatTactic.Hold || !view.Layout.TryGetValue(out var layout)
            || layout.Retreat.Count != layout.Firing.Count || layout.Retreat.Count == 0)
        {
            m.MechLure = false;
            return;
        }

        var state = new Dictionary<PawnId, CombatPawnState>();
        foreach (var p in view.Pawns.Items)
        {
            state[p.Id] = p;
        }

        var ours = 0.0;
        foreach (var r in m.Roles)
        {
            if (r.Ranged)
            {
                ours = Math.Max(ours, state.GetValueOrDefault(r.Pawn)?.WeaponRange ?? 0);
            }
        }

        bool outranged = false, near = false;
        foreach (var h in RankThreats(view))
        {
            outranged = outranged || (ours > 0 && h.WeaponRange > ours);
            var known = h.Cell.TryGetValue(out var c);
            foreach (var f in layout.Firing.Items)
            {
                near = near || (known && Math.Sqrt(((double)(c.X - f.X) * (c.X - f.X)) + ((double)(c.Z - f.Z) * (c.Z - f.Z))) <= ours);
            }
        }

        var lure = outranged && !near;
        if (!lure && !m.MechLure)
        {
            return;
        }

        foreach (var r in m.Roles)
        {
            if (!r.Ranged || r.Retreat || r.Cell is not { } cell)
            {
                continue;
            }

            for (var j = 0; j < layout.Firing.Count; j++)
            {
                if (lure && cell == layout.Firing[j])
                {
                    r.Cell = layout.Retreat[j];
                    r.Target = default;
                }
                else if (lure && cell == layout.Retreat[j])
                {
                    r.Target = default;
                }
                else if (!lure && cell == layout.Retreat[j])
                {
                    r.Cell = layout.Firing[j];
                }
                else
                {
                    continue;
                }

                break;
            }
        }

        m.MechLure = lure;
    }
}
