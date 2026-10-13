namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>
    /// The share of its weapon range a harasser stands off its target; it harasses only when that stand-off
    /// still clears every hostile's range.
    /// </summary>
    internal const double HarassReach = 0.9;

    /// <summary>A tribal's reach when its weapon range is unknown: a great bow's.</summary>
    internal const double TribalBowRange = 30;

    /// <summary>
    /// Makes <paramref name="r"/> a harasser of the target nearest <paramref name="at"/>, standing off it at
    /// HarassReach of <paramref name="reach"/>. With <paramref name="back"/>, a gunner already closer than that
    /// falls back along the same line to the stand-off.
    /// </summary>
    internal static CombatRole HarassTarget(CombatRole r, Cell at, double reach, IReadOnlyList<CombatPawnState> targets, bool back)
    {
        var target = targets[0];
        foreach (var h in targets.Skip(1))
        {
            h.Cell.TryGetValue(out var c);
            target.Cell.TryGetValue(out var b);
            if (Distance2(c, at) < Distance2(b, at))
            {
                target = h;
            }
        }
        var want = HarassReach * reach;
        var cell = StandOffAt(target, at, want);
        target.Cell.TryGetValue(out var t);
        if (back && cell == at && at != t)
        {
            double dx = at.X - t.X;
            double dz = at.Z - t.Z;
            var dist = Math.Sqrt((dx * dx) + (dz * dz));
            cell = new Cell(t.X + (int)Math.Round(dx / dist * want, MidpointRounding.AwayFromZero), t.Z + (int)Math.Round(dz / dist * want, MidpointRounding.AwayFromZero));
        }
        return r with { Duty = CombatDuty.Harasser, Target = target.Id, Cell = cell };
    }

    /// <summary>
    /// Turns the siege gunners that outrange the camp into harassers: each moves to the cell on the line from
    /// its nearest besieger toward itself at HarassReach of its range and attacks that besieger. The harm makes
    /// the lord assault, and the raid_phase stop re-forms onto the killbox. Gunners that do not outrange keep
    /// their home role.
    /// </summary>
    internal static List<CombatRole> HarassRoles(CombatView view, IReadOnlyList<CombatRole> roles)
    {
        var besiegers = LiveBesiegers(view);
        var camp = new List<CombatPawnState>();
        var theirs = 0.0;
        var state = new Dictionary<PawnId, CombatPawnState>();
        foreach (var p in view.Pawns)
        {
            state[p.Id] = p;
            if (besiegers.ContainsKey(p.Id) && p.Cell.IsKnown)
            {
                camp.Add(p);
                theirs = Math.Max(theirs, p.WeaponRange);
            }
        }
        var result = roles.ToList();
        if (camp.Count == 0)
        {
            return result;
        }
        for (var i = 0; i < result.Count; i++)
        {
            var r = result[i];
            state.TryGetValue(r.Pawn, out var s);
            if (s is null || !s.Cell.TryGetValue(out var at) || !r.Ranged || s.WeaponRange <= 0 || HarassReach * s.WeaponRange <= theirs)
            {
                continue;
            }
            result[i] = HarassTarget(r, at, s.WeaponRange, camp, false);
        }
        return result;
    }

    /// <summary>
    /// The siege stand-off generalized to tribals: while every live hostile with a known cell is a Tribal_
    /// pawn, each free gunner whose gun reaches the loadout tribal range and, at HarassReach, still clears
    /// every tribal's range stands off its nearest tribal and shoots it. A gunner that does not outrange the
    /// bows and pila keeps its formation role.
    /// </summary>
    internal static CombatMemory TribalStandoff(CombatView view, CombatMemory m)
    {
        var tribals = new List<CombatPawnState>();
        var theirs = 0.0;
        foreach (var h in RankThreats(view))
        {
            if (!h.Cell.IsKnown)
            {
                continue;
            }
            if (!h.Kind.StartsWith("Tribal_", StringComparison.Ordinal))
            {
                return m;
            }
            tribals.Add(h);
            var reach = h.WeaponRange;
            if (reach <= 0)
            {
                reach = TribalBowRange;
            }
            theirs = Math.Max(theirs, reach);
        }
        if (tribals.Count == 0)
        {
            return m;
        }
        var state = new Dictionary<PawnId, CombatPawnState>();
        foreach (var p in view.Pawns)
        {
            state[p.Id] = p;
        }
        var roles = m.Roles.ToList();
        for (var i = 0; i < roles.Count; i++)
        {
            var r = roles[i];
            state.TryGetValue(r.Pawn, out var s);
            if (s is null || !s.Cell.TryGetValue(out var at) || !r.Ranged || r.Duty != CombatDuty.None || r.Mortar is not null || s.WeaponRange < LoadoutTribalRange || HarassReach * s.WeaponRange <= theirs)
            {
                continue;
            }
            roles[i] = HarassTarget(r, at, s.WeaponRange, tribals, true) with { Retreat = false };
        }
        return m with { Roles = EquatableArray.From(roles) };
    }

    /// <summary>
    /// Hit-and-run on a crashed ship part: once no mech is left alive, every free gunner stands off the
    /// nearest ship part at HarassReach of its range and shoots it. The part has no guns, so there is no
    /// outrange check; the stand-off keeps the gunner clear of any mechs the part wakes next.
    /// </summary>
    internal static CombatMemory ShipPartHitAndRun(CombatView view, CombatMemory m)
    {
        var parts = new List<CombatPawnState>();
        foreach (var s in view.Structures)
        {
            if (EmergencyThreat.ShipPart(s.Def))
            {
                parts.Add(new CombatPawnState { Id = s.Id, Cell = Known(s.Cell) });
            }
        }
        if (parts.Count == 0)
        {
            return m;
        }
        foreach (var h in RankThreats(view))
        {
            if (h.Mech)
            {
                return m;
            }
        }
        var state = new Dictionary<PawnId, CombatPawnState>();
        foreach (var p in view.Pawns)
        {
            state[p.Id] = p;
        }
        var roles = m.Roles.ToList();
        for (var i = 0; i < roles.Count; i++)
        {
            var r = roles[i];
            state.TryGetValue(r.Pawn, out var s);
            if (s is null || !s.Cell.TryGetValue(out var at) || !r.Ranged || r.Duty != CombatDuty.None || r.Mortar is not null || s.WeaponRange <= 0)
            {
                continue;
            }
            roles[i] = HarassTarget(r, at, s.WeaponRange, parts, false) with { Retreat = false };
        }
        return m with { Roles = EquatableArray.From(roles) };
    }
}
