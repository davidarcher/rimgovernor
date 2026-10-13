namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>
    /// A ranged weapon that is not explosive: an escapee is shot, and may die, but a warden never lobs area
    /// damage into the prison.
    /// </summary>
    internal static bool PrisonSafeRanged(WeaponDef facts) => facts.Ranged && !facts.Explosive;

    /// <summary>The live prison-breaking prisoners in id order.</summary>
    internal static List<CombatPawnState> Escapees(CombatView view) =>
        view.Pawns.Where(p => p.Prisoner && !p.Dead && !p.Downed).ToList();

    /// <summary>An escapee below full summary health.</summary>
    internal static bool Injured(CombatPawnState p) => p.Health.TryGetValue(out var h) && h < 1;

    /// <summary>
    /// Runs the prison-break tactic when the view has a live escapee: it reports false otherwise and leaves
    /// memory alone. The formation is recomputed every stop (it is deterministic, so a pawn already doing its
    /// role gets nothing), and the orders are the roles' plus the ranged wardens' fire modes.
    /// </summary>
    internal static (CombatMemory Memory, List<CombatOrder> Orders, bool Ran) PrisonBreakTurn(
        CombatView view,
        CombatMemory next,
        IReadOnlyDictionary<PawnId, CombatPawnState> state,
        IReadOnlySet<PawnId> orderable)
    {
        var escapees = Escapees(view);
        if (escapees.Count == 0)
        {
            return (next, [], false);
        }
        if (next.Tactic != CombatTactic.PrisonBreak)
        {
            next = next with { Formed = view.Tick };
        }
        var roles = PrisonFormation(view, escapees);
        next = next with { Tactic = CombatTactic.PrisonBreak, Refusal = "", Roles = EquatableArray.From(roles) };
        var blocked = PrisonBlocked(roles, escapees, state);
        var lastHold = new Dictionary<PawnId, bool>();
        foreach (var o in next.Issued)
        {
            lastHold[o.CombatOrder.Pawn] = o.CombatOrder.Kind == CombatOrderKind.FireMode && o.CombatOrder.FireMode == HoldFire;
        }
        var orders = new List<CombatOrder>();
        for (var i = 0; i < roles.Count; i++)
        {
            var role = roles[i];
            state.TryGetValue(role.Pawn, out var s);
            if (!orderable.Contains(role.Pawn) || s is null || s.Dead || s.Downed)
            {
                continue;
            }
            if (role.Ranged)
            {
                var held = s.FireMode == HoldFire || (s.FireMode.Length == 0 && lastHold.GetValueOrDefault(role.Pawn));
                var target = FirstBlocked(escapees, blocked);
                roles[i] = role with { Target = target };
                if (target.IsEmpty && !held)
                {
                    orders.Add(new CombatOrder { Pawn = role.Pawn, Kind = CombatOrderKind.FireMode, FireMode = HoldFire, Reason = CombatOrderReason.PrisonFire });
                    continue;
                }
                if (target.IsEmpty)
                {
                    continue;
                }
                if (held)
                {
                    orders.Add(new CombatOrder { Pawn = role.Pawn, Kind = CombatOrderKind.FireMode, FireMode = FireAtWill, Reason = CombatOrderReason.PrisonFire });
                }
                role = roles[i];
            }
            var (want, ok) = role.Want(s);
            if (!ok || next.Doing(want, s) || InterruptsAim(s))
            {
                continue;
            }
            orders.Add(want);
        }
        return (next with { Roles = EquatableArray.From(roles) }, orders, true);
    }

    /// <summary>The first blocked escapee, empty when none is.</summary>
    internal static PawnId FirstBlocked(IReadOnlyList<CombatPawnState> escapees, IReadOnlyDictionary<PawnId, bool> blocked)
    {
        foreach (var e in escapees)
        {
            if (blocked.GetValueOrDefault(e.Id))
            {
                return e.Id;
            }
        }
        return default;
    }

    /// <summary>The escapees a blocker holds: a blocker assigned to it stands next to it or is in melee with it.</summary>
    internal static Dictionary<PawnId, bool> PrisonBlocked(IReadOnlyList<CombatRole> roles, IReadOnlyList<CombatPawnState> escapees, IReadOnlyDictionary<PawnId, CombatPawnState> state)
    {
        var cells = new Dictionary<PawnId, Cell>();
        foreach (var e in escapees)
        {
            if (e.Cell.TryGetValue(out var c))
            {
                cells[e.Id] = c;
            }
        }
        var blocked = new Dictionary<PawnId, bool>();
        foreach (var r in roles)
        {
            if (r.Ranged || r.Target.IsEmpty)
            {
                continue;
            }
            state.TryGetValue(r.Pawn, out var s);
            var ok = s is not null && s.Cell.TryGetValue(out var at);
            var known = cells.TryGetValue(r.Target, out var escapee);
            if (s is not null && ((s.Target == r.Target && s.Stance == CombatStance.Melee) || (ok && known && Adjacent8(s.Cell.Value, escapee))))
            {
                blocked[r.Target] = true;
            }
        }
        return blocked;
    }

    /// <summary>
    /// Assigns the blockers, injured escapees first, then the ranged wardens. Blockers are eligible defenders
    /// without a gun: for an injured escapee the best brawler (blunt weapon, then unarmed, then any melee
    /// weapon), for a healthy one two unarmed wardens (unarmed first, then blunt), each rank wardens first,
    /// then best armored. Every eligible defender left with a non-explosive gun is a ranged warden; the rest
    /// stay out.
    /// </summary>
    internal static List<CombatRole> PrisonFormation(CombatView view, IReadOnlyList<CombatPawnState> escapees)
    {
        var weapon = new Dictionary<PawnId, string>();
        var facts = new Dictionary<PawnId, WeaponDef>();
        foreach (var p in view.Pawns)
        {
            weapon[p.Id] = p.Weapon;
            facts[p.Id] = p.WeaponFacts;
        }
        var melee = new List<SquadDefenderFacts>();
        var ranged = new List<SquadDefenderFacts>();
        foreach (var d in ByArmor(view.Defenders.ToList()))
        {
            facts.TryGetValue(d.Id, out var f);
            if (!SquadDefenderEligible(d))
            {
                continue;
            }
            if (Positive(d.RangedEquipped) || (f?.Ranged ?? false))
            {
                if (f is not null && PrisonSafeRanged(f))
                {
                    ranged.Add(d);
                }
            }
            else
            {
                melee.Add(d);
            }
        }
        int Grip(SquadDefenderFacts d)
        {
            if (facts.TryGetValue(d.Id, out var f) && f.Blunt)
            {
                return 0;
            }
            if ((weapon.GetValueOrDefault(d.Id) ?? "").Length == 0 && !Positive(d.Armed))
            {
                return 1;
            }
            return 2;
        }
        static List<SquadDefenderFacts> Rank(IEnumerable<SquadDefenderFacts> pool, Func<SquadDefenderFacts, int> order) =>
            pool.OrderByDescending(d => d.Warden).ThenBy(order).ToList();
        var ordered = escapees.OrderBy(e => !Injured(e)).ToList();
        var used = new HashSet<PawnId>();
        var roles = new List<CombatRole>();
        int[] healthyOrder = [1, 0, 2];
        foreach (var e in ordered)
        {
            var need = 2;
            Func<SquadDefenderFacts, int> order = d => healthyOrder[Grip(d)];
            if (Injured(e))
            {
                need = 1;
                order = Grip;
            }
            var pool = Rank(melee.Where(d => !used.Contains(d.Id)), order);
            foreach (var d in pool.Take(need))
            {
                used.Add(d.Id);
                roles.Add(new CombatRole { Pawn = d.Id, Target = e.Id, Duty = CombatDuty.Blocker });
            }
        }
        foreach (var d in ranged)
        {
            roles.Add(new CombatRole { Pawn = d.Id, Ranged = true });
        }
        return SortRoles(roles);
    }
}
