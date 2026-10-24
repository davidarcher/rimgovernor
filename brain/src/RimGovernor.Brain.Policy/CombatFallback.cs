using System.Collections.Immutable;
using RimGovernor.Contracts.Wire.Clock;

namespace RimGovernor.Brain.Policy;


public static partial class CombatFallbackRules
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
    internal static void Scatter(CombatView view, CombatMemory m)
    {
        var at = new Dictionary<PawnId, Cell>();
        foreach (var p in view.Pawns.Items)
        {
            var c = p.Cell;
            at[p.Id] = c;
        }

        if (!m.Scattered)
        {
            if (view.Layout is not { } layout || m.Tactic != CombatTactic.Hold)
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
            if (!CombatWaitRules.Outmatched(view) || m.LineHeld == 0 || 2 * held >= m.LineHeld)
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
        else if (!CombatWaitRules.Outmatched(view))
        {
            m.Scattered = false;
            m.LineHeld = 0;
            m.Roles = default;
            return;
        }

        double hx = 0, hz = 0;
        var n = 0;
        var down = CombatManhunterRules.DownPawns(view);
        foreach (var t in view.Threats.Items)
        {
            if (!at.TryGetValue(CombatDecideRules.ThreatPawn(t), out var c) || t.Building || t.Dead || t.Downed || down.Contains(t.Id))
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
            r.Post = new CombatPost.Free();
            r.Standby = new CombatPost.Free();
            r.Fire = new RoleFire.Own();
            r.RepairDoor = new RoleRepair.NoDoor();
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

            r.Post = new CombatPost.At(new Cell(
                Math.Max(0, from.X + (int)Math.Round(dx / l * scatter, MidpointRounding.AwayFromZero)),
                Math.Max(0, from.Z + (int)Math.Round(dz / l * scatter, MidpointRounding.AwayFromZero))
            ));
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
    private static void QueueRepairs(CombatView view, CombatMemory m, Dictionary<PawnId, Cell> at)
    {
        m.Repairs.RemoveAll(d => !view.DamagedDoors.Items.Contains(d));
        foreach (var r in m.Roles)
        {
            if (r.RepairDoor is RoleRepair.Door { Cell: var repair } && !m.Repairs.Contains(repair))
            {
                r.RepairDoor = new RoleRepair.NoDoor();
            }
        }

        foreach (var d in m.Repairs)
        {
            if (m.Roles.Any(r => r.RepairDoor is RoleRepair.Door { Cell: var door } && door == d))
            {
                continue;
            }

            var best = -1;
            for (var i = 0; i < m.Roles.Count; i++)
            {
                var r = m.Roles[i];
                if (r.RepairDoor is RoleRepair.Door || !at.TryGetValue(r.Pawn, out var c))
                {
                    continue;
                }

                if (best < 0 || CombatManhunterRules.Distance2(c, d) < CombatManhunterRules.Distance2(at.GetValueOrDefault(m.Roles[best].Pawn), d))
                {
                    best = i;
                }
            }

            if (best >= 0)
            {
                m.Roles[best].RepairDoor = new RoleRepair.Door(d);
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
    internal static bool FallBack(CombatView view, StopEvent stop, CombatMemory m)
    {
        if (m.Tactic != CombatTactic.Hold || view.Layout is not { } layout || layout.Retreat.Count == 0 || layout.Retreat.Count != layout.Firing.Count || FellBack(m))
        {
            return false;
        }

        Func<CombatRole, bool> who;
        if (stop.Kind == CombatEvent.Breach || DefensePositions.HoldCompromised(layout.Firing.Items, layout.Toward, CombatPeelRules.Unpeeled(view, stop, m)))
        {
            who = _ => true;
        }
        else if (stop.Kind == CombatEvent.SeriousInjury && !stop.Pawn.IsEmpty)
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
            if (r.Retreat && r.Posted(out var cell))
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

            if (RetreatCell(layout, r.Post, taken) is { } to)
            {
                r.Post = new CombatPost.At(to);
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
    internal static Cell? RetreatCell(CombatLayout layout, CombatPost from, HashSet<Cell> taken)
    {
        if (from is CombatPost.At { Cell: var f })
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
            if (taken.Add(c))
            {
                return c;
            }
        }

        return null;
    }

    /// <summary>Reports a formation whose every role is on the inner line.</summary>
    private static bool FellBack(CombatMemory m) => m.Roles.Count > 0 && m.Roles.All(r => r.Retreat);

    /// <summary>The line a hold's compromise is judged against: the inner line once the whole formation fell back to it, else the firing line.</summary>
    internal static ImmutableArray<Cell> HoldLine(CombatLayout layout, CombatMemory m) =>
        FellBack(m) && layout.Retreat.Count > 0 ? layout.Retreat.Items : layout.Firing.Items;
}
