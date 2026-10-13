namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>
    /// Separates the eligible defenders wearing a charged shield (the tanks, best armored first) from the rest.
    /// A shield belt blocks its wearer's own shots, so a tank is never a gunner, whatever it holds.
    /// </summary>
    internal static (List<SquadDefenderFacts> Others, List<SquadDefenderFacts> Tanks) SplitTanks(CombatView view)
    {
        var charged = new HashSet<PawnId>();
        foreach (var p in view.Pawns)
        {
            if (p.Shield.TryGetValue(out var s) && s > 0)
            {
                charged.Add(p.Id);
            }
        }
        var rest = new List<SquadDefenderFacts>();
        var tanks = new List<SquadDefenderFacts>();
        foreach (var d in view.Defenders)
        {
            if (charged.Contains(d.Id) && SquadDefenderEligible(d))
            {
                tanks.Add(d);
            }
            else
            {
                rest.Add(d);
            }
        }
        return (rest, ByArmor(tanks));
    }

    /// <summary>
    /// Reports a fight a tank is worth placing in: the live hostiles are mostly ranged (a belt stops bullets,
    /// not blades) and none carries an EMP weapon, which breaks the belt at once.
    /// </summary>
    internal static bool TankThreat(CombatView view)
    {
        var live = 0;
        var ranged = 0;
        foreach (var t in view.Threats)
        {
            if (t.Dead.ValueOrDefault || t.Building)
            {
                continue;
            }
            if (t.Downed.ValueOrDefault)
            {
                continue;
            }
            live++;
            if (t.RangedEquipped.ValueOrDefault)
            {
                ranged++;
            }
        }
        var emp = RankThreats(view).Any(h => h.WeaponFacts.EMP);
        return ranged * 2 > live && !emp;
    }

    /// <summary>The cells a tank may take ahead of a gunner at <paramref name="g"/>, nearest first: the front cell, then the cells one and two steps toward the approach within one cell sideways.</summary>
    internal static List<Cell> TankCells(Cell g, Cell v)
    {
        var side = new Cell(v.Z, v.X);
        var result = new List<Cell>();
        foreach (var (forward, across) in new[] { (1, 0), (1, -1), (1, 1), (2, 0), (2, -1), (2, 1) })
        {
            result.Add(new Cell(g.X - (forward * v.X) + (across * side.X), g.Z - (forward * v.Z) + (across * side.Z)));
        }
        return result;
    }

    /// <summary>
    /// Puts one tank ahead of each gunner, toward the approach, in the gunners' line order. The tank takes the
    /// cell in front of the gunner when the game reported it standable; when that cell is cover instead, the
    /// nearest standable cell ahead of the gunner, one with cover toward the approach first, else open ground.
    /// Gunners stay put. A gunner with no free cell ahead gets no tank. The cell behind the gunner is the
    /// tank's Home when standable. Tanks beyond the gunners, and every tank outside a TankThreat fight, get no role.
    /// </summary>
    internal static List<CombatRole> TankRoles(CombatView view, IReadOnlyList<SquadDefenderFacts> tanks, IReadOnlyList<DefensivePosition> gunners, Rotation toward, GeometryReply geometry, IReadOnlyList<Cell> taken)
    {
        var ok = TowardVector(toward, out var v);
        if (!ok || !TankThreat(view))
        {
            return [];
        }
        var covered = new Dictionary<Cell, bool>();
        foreach (var s in geometry.Scored)
        {
            covered[s.Cell] = s.Lines.Any(l => l.Cover > 0);
        }
        var used = taken.ToList();
        foreach (var g in gunners)
        {
            used.Add(g.Cell);
        }
        var roles = new List<CombatRole>();
        foreach (var g in gunners)
        {
            if (roles.Count == tanks.Count)
            {
                break;
            }
            var ahead = TankCells(g.Cell, v);
            var free = ahead.Where(c => geometry.Stands(c) && !used.Contains(c)).ToList();
            if (free.Count == 0)
            {
                continue;
            }
            var cell = free[0];
            if (cell != ahead[0])
            {
                // The front cell is taken or cover: covered ground beats open.
                var i = free.FindIndex(c => covered.GetValueOrDefault(c));
                if (i >= 0)
                {
                    cell = free[i];
                }
            }
            used.Add(cell);
            var role = new CombatRole { Pawn = tanks[roles.Count].Id, Cell = cell, Duty = CombatDuty.Tank };
            var back = new Cell(g.Cell.X + v.X, g.Cell.Z + v.Z);
            if (geometry.Stands(back))
            {
                role = role with { Home = back };
            }
            roles.Add(role);
        }
        return roles;
    }

    /// <summary>
    /// The reaction table's shield row: on a shield broken stop for a tank, the tank leaves the tank duty and
    /// retreats to its Home behind its gunner, or holds where it stands without one.
    /// </summary>
    internal static CombatMemory PullBackTank(StopEvent stop, CombatMemory m)
    {
        if (stop.Kind != CombatStopKind.ShieldBroken)
        {
            return m;
        }
        return m with
        {
            Roles = EquatableArray.From(m.Roles.Select(r => r.Pawn == stop.Pawn && r.Duty == CombatDuty.Tank ? r with { Cell = r.Home, Duty = CombatDuty.None, Retreat = true } : r)),
        };
    }
}
