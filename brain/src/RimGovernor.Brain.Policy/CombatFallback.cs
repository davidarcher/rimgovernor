namespace RimGovernor.Brain.Policy;

// Go: combat_fallback.go

public static partial class PolicyFunctions
{
    /// <summary>How far a scattering defender runs from the raid.</summary>
    internal const int ScatterCells = 20;

    /// <summary>
    /// The hold's last resort. The line collapses when the fight is outmatched and most of the hold-the-line
    /// cells defenders once stood on are lost (their defenders downed or pushed off): every live role then
    /// drops its target and runs the scatter distance away from the live hostiles, and the damaged doors are
    /// queued. The fight stays scattered while it is outmatched; once it is not, it drops its roles to re-form,
    /// and each queued door still damaged goes to the nearest free role to repair.
    /// </summary>
    private static void Scatter(CombatView view, CombatMemory m)
    {
        var at = new Dictionary<PawnId, Cell>();
        foreach (var p in view.Pawns.Items)
        {
            if (p.Cell.TryGetValue(out var c))
            {
                at[p.Id] = c;
            }
        }

        if (!m.Scattered)
        {
            if (!view.Layout.TryGetValue(out var layout) || m.Tactic != CombatTactic.Hold)
            {
                QueueRepairs(view, m, at);
                return;
            }

            var live = view.Live();
            var held = 0;
            foreach (var c in HoldLine(layout, m))
            {
                foreach (var (id, p) in at)
                {
                    if (p == c && live.GetValueOrDefault(id))
                    {
                        held++;
                        break;
                    }
                }
            }

            m.LineHeld = Math.Max(m.LineHeld, held);
            if (!Outmatched(view) || m.LineHeld == 0 || 2 * held >= m.LineHeld)
            {
                QueueRepairs(view, m, at);
                return;
            }

            m.Scattered = true;
            foreach (var d in view.DamagedDoors.Items)
            {
                if (!m.Repairs.Contains(d))
                {
                    m.Repairs.Add(d);
                }
            }
        }
        else if (!Outmatched(view))
        {
            m.Scattered = false;
            m.LineHeld = 0;
            m.Roles = default;
            return;
        }

        double hx = 0, hz = 0;
        var n = 0;
        var down = DownPawns(view);
        foreach (var t in view.Threats.Items)
        {
            if (!at.TryGetValue(ThreatPawn(t), out var c) || t.Building || Positive(t.Dead) || Positive(t.Downed) || down.Contains(t.Id))
            {
                continue;
            }

            hx += c.X;
            hz += c.Z;
            n++;
        }

        var scatter = m.EffectiveTuning().ScatterCells;
        foreach (var r in m.Roles)
        {
            r.Target = default;
            r.Duty = CombatDuty.None;
            r.Cell = null;
            r.Home = null;
            r.Mortar = null;
            r.Aim = null;
            r.Ground = null;
            r.Repair = null;
            if (!at.TryGetValue(r.Pawn, out var from) || n == 0)
            {
                continue;
            }

            var dx = from.X - (hx / n);
            var dz = from.Z - (hz / n);
            var l = Math.Sqrt((dx * dx) + (dz * dz));
            if (l == 0)
            {
                dx = 0;
                dz = 1;
                l = 1;
            }

            r.Cell = new Cell(
                Math.Max(0, from.X + (int)Math.Round(dx / l * scatter, MidpointRounding.AwayFromZero)),
                Math.Max(0, from.Z + (int)Math.Round(dz / l * scatter, MidpointRounding.AwayFromZero))
            );
            r.Retreat = true;
        }

        m.Kiter = default;
        m.Leading = false;
        m.PotshotDoor = null;
        m.Intercept = false;
        m.Rushing = false;
        m.MechLure = false;
    }

    /// <summary>Drops the queued doors no longer damaged and gives each one still damaged to the nearest role with a known cell and no repair yet.</summary>
    private static void QueueRepairs(CombatView view, CombatMemory m, IReadOnlyDictionary<PawnId, Cell> at)
    {
        m.Repairs.RemoveAll(d => !view.DamagedDoors.Items.Contains(d));
        foreach (var r in m.Roles)
        {
            if (r.Repair is { } repair && !m.Repairs.Contains(repair))
            {
                r.Repair = null;
            }
        }

        foreach (var d in m.Repairs)
        {
            if (m.Roles.Any(r => r.Repair == d))
            {
                continue;
            }

            var best = -1;
            for (var i = 0; i < m.Roles.Count; i++)
            {
                var r = m.Roles[i];
                if (r.Repair is not null || !at.TryGetValue(r.Pawn, out var c))
                {
                    continue;
                }

                if (best < 0 || Distance2(c, d) < Distance2(at.GetValueOrDefault(m.Roles[best].Pawn), d))
                {
                    best = i;
                }
            }

            if (best >= 0)
            {
                m.Roles[best].Repair = d;
            }
        }
    }

    /// <summary>
    /// The reaction table's pull-back row. On a held line whose layout records an inner line: a breach or a
    /// compromised line pulls every defender back to its Retreat cell, and a serious injury pulls the hurt
    /// defender back alone. A role once pulled back stays on the inner line, which becomes the line reform
    /// judges; a line already fallen back has no further line, so its compromise or breach re-forms as before.
    /// It reports whether any role changed; a changed role is the stop's reaction instead of a re-formation.
    /// </summary>
    private static bool FallBack(CombatView view, StopEvent stop, CombatMemory m)
    {
        var ok = view.Layout.TryGetValue(out var layout);
        if (m.Tactic != CombatTactic.Hold || !ok || layout!.Retreat.Count == 0 || layout.Retreat.Count != layout.Firing.Count || FellBack(m))
        {
            return false;
        }

        Func<CombatRole, bool> who;
        if (stop.Kind == CombatStopKind.Breach || HoldCompromised(layout.Firing.Items, layout.Toward, Unpeeled(view, stop, m)))
        {
            who = _ => true;
        }
        else if (stop.Kind == CombatStopKind.SeriousInjury && !stop.Pawn.IsEmpty)
        {
            who = r => r.Pawn == stop.Pawn;
        }
        else
        {
            return false;
        }

        var taken = new HashSet<Cell>();
        foreach (var r in m.Roles)
        {
            if (r.Retreat && r.Cell is { } cell)
            {
                taken.Add(cell);
            }
        }

        var changed = false;
        foreach (var r in m.Roles)
        {
            if (r.Retreat || !who(r))
            {
                continue;
            }

            if (RetreatCell(layout, r.Cell, taken) is { } to)
            {
                r.Cell = to;
                r.Retreat = true;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// The inner-line cell behind <paramref name="from"/>: its own Retreat cell when from is a firing cell,
    /// else (a proposed cover cell, or none) the first Retreat cell not yet taken.
    /// </summary>
    private static Cell? RetreatCell(CombatLayout layout, Cell? from, HashSet<Cell> taken)
    {
        if (from is { } f)
        {
            for (var i = 0; i < layout.Firing.Count; i++)
            {
                if (layout.Firing.Items[i] == f && !taken.Contains(layout.Retreat.Items[i]))
                {
                    taken.Add(layout.Retreat.Items[i]);
                    return layout.Retreat.Items[i];
                }
            }
        }

        foreach (var c in layout.Retreat.Items)
        {
            if (!taken.Contains(c))
            {
                taken.Add(c);
                return c;
            }
        }

        return null;
    }

    /// <summary>Reports a formation whose every role is on the inner line.</summary>
    private static bool FellBack(CombatMemory m) => m.Roles.Count > 0 && m.Roles.All(r => r.Retreat);

    /// <summary>The line a hold's compromise is judged against: the inner line once the whole formation fell back to it, else the firing line.</summary>
    private static IReadOnlyList<Cell> HoldLine(CombatLayout layout, CombatMemory m) =>
        FellBack(m) && layout.Retreat.Count > 0 ? layout.Retreat.Items : layout.Firing.Items;
}
