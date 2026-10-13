namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>
    /// Where the peeler waits: one step behind the first gunner's inner-line cell, clear of both lines, or
    /// nowhere (it waits where it was drafted) on a layout without an inner line.
    /// </summary>
    internal static Cell? PeelerHome(CombatView view)
    {
        if (!view.Layout.TryGetValue(out var layout) || layout.Retreat.Count == 0 || layout.Firing.Count == 0)
        {
            return null;
        }
        var f = layout.Firing[0];
        var r = layout.Retreat[0];
        var cell = new Cell((2 * r.X) - f.X, (2 * r.Z) - f.Z);
        if (cell.X < 0 || cell.Z < 0)
        {
            return null;
        }
        return cell;
    }

    /// <summary>
    /// The reaction table's peel row. On a melee contact stop by a hostile on a gunner, the peeler leaves its
    /// home to attack that hostile; once its target is down or dead it goes home. A peeler already on a live
    /// target keeps it.
    /// </summary>
    internal static CombatMemory Peel(CombatView view, StopEvent stop, CombatMemory m)
    {
        var peeler = -1;
        var gunner = false;
        for (var i = 0; i < m.Roles.Count; i++)
        {
            var r = m.Roles[i];
            if (r.Duty == CombatDuty.Peeler)
            {
                peeler = i;
            }
            if (r.Ranged && r.Pawn == stop.Target)
            {
                gunner = true;
            }
        }
        if (peeler < 0)
        {
            return m;
        }
        var role = m.Roles[peeler];
        if (!role.Target.IsEmpty && view.HostileDown(role.Target))
        {
            role = role with { Target = default, Cell = role.Home };
        }
        // A peeler never melees an exploder.
        var boom = LiveExploders(view).ContainsKey(stop.Pawn);
        if (role.Target.IsEmpty && stop.Kind == CombatStopKind.MeleeContact && gunner && !boom && !view.HostileDown(stop.Pawn))
        {
            role = role with { Target = stop.Pawn, Cell = null };
        }
        var roles = m.Roles.ToList();
        roles[peeler] = role;
        return m with { Roles = EquatableArray.From(roles) };
    }

    /// <summary>
    /// The one melee contact the peeler answers: its live target; or, when it is free, the stop's attacker on
    /// a gunner, else the first hostile in melee on a gunner. Empty without a peeler.
    /// </summary>
    internal static PawnId Peelable(CombatView view, StopEvent stop, CombatMemory m)
    {
        var gunners = new HashSet<PawnId>();
        CombatRole? peeler = null;
        foreach (var r in m.Roles)
        {
            if (r.Ranged)
            {
                gunners.Add(r.Pawn);
            }
            if (r.Duty == CombatDuty.Peeler)
            {
                peeler = r;
            }
        }
        if (peeler is null)
        {
            return default;
        }
        if (!peeler.Target.IsEmpty && !view.HostileDown(peeler.Target))
        {
            return peeler.Target;
        }
        if (stop.Kind == CombatStopKind.MeleeContact && gunners.Contains(stop.Target) && !view.HostileDown(stop.Pawn))
        {
            return stop.Pawn;
        }
        foreach (var p in view.Pawns)
        {
            if (p.Stance == CombatStance.Melee && gunners.Contains(p.Target) && !gunners.Contains(p.Id) && !p.Dead && !p.Downed)
            {
                return p.Id;
            }
        }
        return default;
    }

    /// <summary>
    /// The positional threats less the contact the peeler answers: a melee attacker on a gunner stands on the
    /// cover row, and breaking that contact is the peeler's job, not a compromised hold.
    /// </summary>
    internal static IReadOnlyList<DefensiveThreatFacts> Unpeeled(CombatView view, StopEvent stop, CombatMemory m)
    {
        var id = Peelable(view, stop, m);
        if (id.IsEmpty)
        {
            return view.Positional;
        }
        return view.Positional.Where(t => t.Id != id).ToList();
    }

    /// <summary>Reports a hostile known downed or dead.</summary>
    internal static bool HostileDown(this CombatView v, PawnId id)
    {
        foreach (var t in v.Threats)
        {
            if (t.Id == id && (Positive(t.Dead) || Positive(t.Downed)))
            {
                return true;
            }
        }
        foreach (var p in v.Pawns)
        {
            if (p.Id == id && (p.Dead || p.Downed))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Keeps one melee-only defender of a squad fight beside the colony's shooters as their peeler instead of
    /// charging out alone ahead of them: it engages a raider who reaches a gunner (peel) and otherwise waits.
    /// No shooter on the field, or no known place for them, leaves the squad's melee assignment as it was.
    /// </summary>
    internal static IReadOnlyList<CombatRole> HoldBrawlerBesideGunners(CombatView view, IReadOnlyList<CombatRole> roles)
    {
        Cell? gunner = null;
        foreach (var d in view.Defenders)
        {
            if (!Positive(d.RangedEquipped))
            {
                continue;
            }
            foreach (var p in view.Pawns)
            {
                if (p.Cell.TryGetValue(out var at) && p.Id == d.Id && !p.Dead && !p.Downed && gunner is null)
                {
                    gunner = at;
                }
            }
        }
        if (gunner is null)
        {
            return roles;
        }
        var melee = new HashSet<PawnId>(Brawlers(view.Defenders).Select(d => d.Id));
        var result = roles.ToList();
        for (var i = 0; i < result.Count; i++)
        {
            var r = result[i];
            if (melee.Contains(r.Pawn) && !r.Ranged)
            {
                result[i] = new CombatRole { Pawn = r.Pawn, Duty = CombatDuty.Peeler, Cell = gunner, Home = gunner };
            }
        }
        return result;
    }
}
