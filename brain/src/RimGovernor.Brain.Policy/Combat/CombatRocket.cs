namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    // A clump is RocketClumpMin live hostiles within RocketClumpRadius cells of a hostile's cell: a sapper
    // team at its wall, a siege camp. No colonist may stand within RocketSafeRadius of the aim.
    internal const int RocketClumpMin = 3;
    internal const double RocketClumpRadius = 3.0;
    internal const double RocketSafeRadius = 5.0;

    /// <summary>Reports a weapon spent by its shot: a rocket launcher.</summary>
    internal static bool RocketLauncher(WeaponDef weapon) => weapon.OneUse;

    /// <summary>
    /// Gives every orderable rocket carrier a ground shot at the densest hostile clump in its range, nearest
    /// on a tie; a carrier with no clump in range keeps its role. A mortar crew or a rescuer is left alone.
    /// </summary>
    internal static CombatMemory RocketClumps(CombatView view, CombatMemory m)
    {
        var roles = m.Roles.Select(r => r with { Ground = null }).ToList();
        var live = view.Live();
        var hostile = new Dictionary<PawnId, bool>();
        foreach (var t in view.Threats)
        {
            hostile[t.Id] = !t.Building && !Positive(t.Dead) && !Positive(t.Downed);
        }
        var colonist = new HashSet<PawnId>();
        foreach (var d in view.Defenders)
        {
            colonist.Add(d.Id);
        }
        var foes = new List<Cell>();
        var friends = new List<Cell>();
        foreach (var p in view.Pawns)
        {
            if (!p.Cell.TryGetValue(out var c) || p.Dead || p.Downed)
            {
                continue;
            }
            if (hostile.GetValueOrDefault(p.Id))
            {
                foes.Add(c);
            }
            else if (colonist.Contains(p.Id))
            {
                friends.Add(c);
            }
        }
        foreach (var p in view.Pawns)
        {
            if (!p.Cell.TryGetValue(out var from) || !RocketLauncher(p.WeaponFacts) || !view.Orderable.Contains(p.Id) || !live.GetValueOrDefault(p.Id) || (m.Rescue?.Carrying(p.Id) ?? false))
            {
                continue;
            }
            var reach = p.WeaponRange;
            if (reach <= 0)
            {
                reach = p.WeaponFacts.Range;
            }
            var i = roles.FindIndex(r => r.Pawn == p.Id);
            if (i >= 0 && roles[i].Mortar is not null)
            {
                continue;
            }
            Cell aim = default;
            var found = false;
            var most = 0;
            foreach (var c in foes)
            {
                if (Dist(from, c) > reach || friends.Any(f => Dist(f, c) <= RocketSafeRadius))
                {
                    continue;
                }
                var n = 0;
                foreach (var o in foes)
                {
                    if (Dist(c, o) <= RocketClumpRadius)
                    {
                        n++;
                    }
                }
                if (n > most || (n == most && found && Dist(from, c) < Dist(from, aim)))
                {
                    aim = c;
                    found = true;
                    most = n;
                }
            }
            if (most < RocketClumpMin)
            {
                continue;
            }
            if (i < 0)
            {
                roles.Add(new CombatRole { Pawn = p.Id, Ranged = true });
                i = roles.Count - 1;
            }
            roles[i] = roles[i] with { Ground = aim };
        }
        return m with { Roles = EquatableArray.From(SortRoles(roles)) };
    }
}
