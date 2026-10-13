namespace RimGovernor.Brain.Policy;

// Go: combat_infestation.go
//
// CombatTactic.Infestation is an infestation's own formation: a fight with a live hive, or whose live hostile
// pawns are all insects, picks it. Every fighter commits at once, brawlers block the tunnel or door with
// relief, grenadiers throw at the hive, and no mortar is crewed: hives only spawn under overhead mountain,
// which shells cannot reach.

public static partial class PolicyFunctions
{
    /// <summary>A colonist's job while it works a deep drill.</summary>
    internal const string DeepDrillJob = "OperateDeepDrill";

    /// <summary>Reports a fight with a live hive, or with at least one live hostile pawn and every live hostile pawn an insect.</summary>
    public static bool Infestation(CombatView view)
    {
        if (view.Structures.Items.Any(s => s.Hive()))
        {
            return true;
        }

        var insects = new Dictionary<PawnId, bool>();
        foreach (var p in view.Pawns.Items)
        {
            insects[p.Id] = p.Insect;
        }

        var down = DownPawns(view);
        var n = 0;
        foreach (var t in view.Threats.Items)
        {
            if (t.Building || Positive(t.Dead) || Positive(t.Downed) || down.Contains(ThreatPawn(t)))
            {
                continue;
            }

            if (!insects.GetValueOrDefault(ThreatPawn(t)))
            {
                return false;
            }

            n++;
        }

        return n > 0;
    }

    /// <summary>
    /// Commits every armed fighter at once: gunners on the line, brawlers blocking the choke with a relief
    /// reserve. These are the manhunter formation's roles (insects hold no exploder), less the peeler waiting
    /// at home: it charges the top insect, never a trickle.
    /// </summary>
    private static List<CombatRole> InfestationFormation(CombatView view, GeometryReply geometry, IReadOnlyList<PawnId> relieved)
    {
        var roles = ManhunterFormation(view, geometry, relieved);

        // The top insect, else (no insect out) the hive itself.
        PawnId top = default;
        var ranked = RankThreats(view);
        if (ranked.Count > 0)
        {
            top = ranked[0].Id;
        }
        else
        {
            var hive = view.Structures.Items.FirstOrDefault(s => s.Hive());
            if (hive is not null)
            {
                top = hive.Id;
            }
        }

        for (var i = 0; i < roles.Count; i++)
        {
            if (roles[i].Duty == CombatDuty.Peeler)
            {
                roles[i] = new CombatRole { Pawn = roles[i].Pawn, Target = top };
            }
            else if (roles[i].Duty == CombatDuty.None && roles[i].Target.IsEmpty)
            {
                roles[i].Target = top;
            }
        }

        return roles;
    }

    /// <summary>
    /// Aims each infestation role holding a frag or molotov primary at the nearest live hive in its range and
    /// clear of colonists (the grenade scatter rule); with none it falls back to GrenadeTarget on the insects.
    /// Every mortar role is dropped: the hive is under overhead mountain.
    /// </summary>
    private static void HiveGrenade(CombatView view, CombatMemory m)
    {
        if (m.Tactic != CombatTactic.Infestation)
        {
            return;
        }

        var state = new Dictionary<PawnId, CombatPawnState>();
        foreach (var p in view.Pawns.Items)
        {
            state[p.Id] = p;
        }

        var colonists = new List<Cell>();
        foreach (var d in view.Defenders.Items)
        {
            if (state.TryGetValue(d.Id, out var s) && !s.Dead && s.Cell.TryGetValue(out var c))
            {
                colonists.Add(c);
            }
        }

        var hostiles = RankThreats(view);
        foreach (var r in m.Roles)
        {
            r.Mortar = null;
            r.Aim = null;
            if (m.Rescue?.Carrying(r.Pawn) == true)
            {
                continue;
            }

            var carrier = state.GetValueOrDefault(r.Pawn) ?? new CombatPawnState();
            if (carrier.WeaponFacts.Burner())
            {
                // Molotovs belong to the burn-out alone: flame on an insect may send the hive to assault.
                r.Ground = null;
                continue;
            }

            if (carrier.WeaponFacts.Blast <= 0 || carrier.WeaponFacts.Emp)
            {
                continue;
            }

            if (!carrier.Cell.TryGetValue(out var from) || (r.Cell is { } held && from != held))
            {
                continue;
            }

            var reach = carrier.WeaponRange;
            if (reach <= 0)
            {
                reach = carrier.WeaponFacts.Range;
            }

            Cell? best = null;
            foreach (var s in view.Structures.Items)
            {
                var c = s.Cell;
                if (!s.Hive() || Dist(from, c) > reach || NearColonist(c, colonists))
                {
                    continue;
                }

                if (best is not { } b || Dist(from, c) < Dist(from, b) || (Dist(from, c) == Dist(from, b) && (c.X < b.X || (c.X == b.X && c.Z < b.Z))))
                {
                    best = c;
                }
            }

            best ??= GrenadeTarget(carrier, hostiles, colonists);
            r.Ground = best;
        }
    }

    /// <summary>
    /// Holds every melee fighter of an infestation fight in place while the hive reads above the heat-entry
    /// ceiling, instead of sending it at an insect or the hive; a downed insect, the burn-out's finish, is
    /// still fair game. An unknown temperature changes nothing.
    /// </summary>
    private static void HeatEntry(CombatView view, CombatMemory m)
    {
        var known = view.HiveTemperatureC.TryGetValue(out var temp);
        if (m.Tactic != CombatTactic.Infestation || !known || temp <= HeatEntryMaxC)
        {
            return;
        }

        var state = new Dictionary<PawnId, CombatPawnState>();
        foreach (var p in view.Pawns.Items)
        {
            state[p.Id] = p;
        }

        var down = DownPawns(view);
        foreach (var r in m.Roles)
        {
            if (!r.Ranged && r.Cell is null && !r.Target.IsEmpty && !down.Contains(r.Target))
            {
                r.Target = default;
                if (state.GetValueOrDefault(r.Pawn) is { } s && s.Cell.TryGetValue(out var c))
                {
                    r.Cell = c;
                }
            }
        }
    }

    /// <summary>
    /// The deep-drill spawn: insects tunnel up near a working drill, with no hive. The colonist working the
    /// drill when the fight starts is latched as the driller and evacuates: it drops its target and duty and
    /// retreats to the rearmost inner-line cell, else the kite lead range straight away from the nearest
    /// insect, while the formation's brawlers melee-block. Later stops keep the latch although the driller's
    /// job has changed.
    /// </summary>
    private static void DrillEvacuate(CombatView view, CombatMemory m, ISet<PawnId> orderable)
    {
        if (m.Tactic != CombatTactic.Infestation || view.Structures.Items.Any(s => s.Hive()))
        {
            m.Driller = default;
            return;
        }

        var state = new Dictionary<PawnId, CombatPawnState>();
        foreach (var p in view.Pawns.Items)
        {
            state[p.Id] = p;
        }

        if (m.Driller.IsEmpty)
        {
            foreach (var d in view.Defenders.Items)
            {
                var ds = state.GetValueOrDefault(d.Id) ?? new CombatPawnState();
                if (ds.Job == DeepDrillJob && !ds.Dead && !ds.Downed)
                {
                    m.Driller = d.Id;
                    break;
                }
            }
        }

        var s = state.GetValueOrDefault(m.Driller) ?? new CombatPawnState();
        if (m.Driller.IsEmpty || s.Dead || s.Downed)
        {
            return;
        }

        Cell to;
        if (RearmostRetreat(view) is { } rear)
        {
            to = rear;
        }
        else
        {
            var known = s.Cell.TryGetValue(out var at);
            Cell? near = null;
            foreach (var h in RankThreats(view))
            {
                if (h.Cell.TryGetValue(out var c) && known && (near is not { } n || Distance2(c, at) < Distance2(n, at)))
                {
                    near = c;
                }
            }

            if (near is not { } nearest)
            {
                return;
            }

            var lead = m.EffectiveTuning().KiteLeadRange;
            to = new Cell(at.X + (lead * Sign(at.X - nearest.X)), at.Z + (lead * Sign(at.Z - nearest.Z)));
        }

        if (m.Kiter == m.Driller)
        {
            m.Kiter = default;
            m.Leading = false;
        }

        var driller = m.Driller;
        var roles = m.Roles.ToList();
        var i = roles.FindIndex(r => r.Pawn == driller);
        if (i < 0)
        {
            if (!orderable.Contains(m.Driller))
            {
                return;
            }

            roles.Add(new CombatRole());
            i = roles.Count - 1;
        }

        roles[i] = new CombatRole { Pawn = m.Driller, Cell = to, Retreat = true };
        m.Roles = EquatableArray.From(roles);
    }
}
