namespace RimGovernor.Brain.Policy;

// Go: combat_fire.go
//
// Spacing and friendly fire: firing cells a tile apart, no attack along a line a colonist stands on, and hold
// fire on a hostile locked in melee with one of our blockers.

/// <summary>Combat.geometry's answer for one (cell, hostile) pair.</summary>
public sealed record SightLine
{
    public Cell Cell { get; init; }

    public PawnId Hostile { get; init; }

    public bool LineOfFire { get; init; }

    public bool ColonistInPath { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>Fire mode: shoot at will, as the combat pawn row reports it.</summary>
    public const string FireAtWill = "fire_at_will";

    /// <summary>Fire mode: hold fire, as the combat pawn row reports it.</summary>
    public const string HoldFire = "hold_fire";

    /// <summary>Bridge.CombatGeometryMaxCells.</summary>
    internal const int MaxGeometryCells = 64;

    private readonly record struct SightKey(Cell Cell, PawnId Hostile);

    /// <summary>
    /// Orders candidate firing cells so the first ones keep at least <paramref name="gap"/> empty tiles (8-way)
    /// between each other: a greedy spaced pick in the given order, then, one tile tighter at a time down to
    /// one, greedy picks from what is left, then the rest in order. Formation assigns cells front to back, so
    /// defenders stand spaced while the candidates allow it and pack only past that, rather than stand idle.
    /// </summary>
    private static List<Cell> SpaceCells(IReadOnlyList<Cell> cells, int gap)
    {
        var spaced = new List<Cell>();
        IReadOnlyList<Cell> rest = cells;
        for (var g = gap; g >= 1; g--)
        {
            var left = new List<Cell>();
            foreach (var c in rest)
            {
                var gg = g;
                if (spaced.Any(s => Within(s, c, gg)))
                {
                    left.Add(c);
                }
                else
                {
                    spaced.Add(c);
                }
            }

            rest = left;
        }

        spaced.AddRange(rest);
        return spaced;
    }

    /// <summary>Reports b at most d tiles from a (8-way).</summary>
    private static bool Within(Cell a, Cell b, int d)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return dx >= -d && dx <= d && dz >= -d && dz <= d;
    }

    private static bool Adjacent8(Cell a, Cell b) => Within(a, b, 1);

    /// <summary>
    /// The known cells of the orderable, live pawns: where an attack order at this stop fires from, named in
    /// the stop's geometry ask so its answer carries their lines of fire.
    /// </summary>
    private static List<Cell> ShooterCells(CombatView view)
    {
        var live = view.Live();
        var orderable = new HashSet<PawnId>(view.Orderable.Items);
        var output = new List<Cell>();
        foreach (var p in view.Pawns.Items)
        {
            if (p.Cell.TryGetValue(out var c) && orderable.Contains(p.Id) && live.GetValueOrDefault(p.Id) && !output.Contains(c))
            {
                output.Add(c);
            }
        }

        return output;
    }

    /// <summary>
    /// The stop's geometry ask when its attack orders have no lines yet: the shooters' cells against the
    /// top-scored hostiles. When an attacker's every line from its cell was blocked or refused before (walled
    /// in), it also asks firing cells around that attacker.
    /// </summary>
    private static GeometryRequest? LineAsk(CombatView view, IReadOnlyList<CombatOrder> orders, CombatMemory m)
    {
        if (!orders.Any(o => o.Kind == CombatOrderKind.Attack))
        {
            return null;
        }

        var ask = new GeometryRequest { Cells = ShooterCells(view) };
        var targets = new List<Cell>();
        foreach (var h in RankThreats(view))
        {
            if (ask.Hostiles.Count < MaxGeometryHostiles)
            {
                ask.Hostiles.Add(h.Id);
                if (h.Cell.TryGetValue(out var c) && !targets.Contains(c))
                {
                    targets.Add(c);
                }
            }
        }

        if (ask.Cells.Count == 0 || ask.Hostiles.Count == 0)
        {
            return null;
        }

        if (ask.Cells.Count > MaxGeometryCells - 1)
        {
            ask.Cells = ask.Cells.GetRange(0, MaxGeometryCells - 1);
        }

        var cells = new Dictionary<PawnId, Cell>();
        foreach (var p in view.Pawns.Items)
        {
            if (p.Cell.TryGetValue(out var c))
            {
                cells[p.Id] = c;
            }
        }

        foreach (var o in orders)
        {
            var ok = cells.TryGetValue(o.Pawn, out var from);
            bool Open(PawnId h) => !m.RefusedHit(o.Pawn, h, from);
            if (o.Kind == CombatOrderKind.Attack && ok && targets.Count > 0 && !ask.Hostiles.Any(Open))
            {
                ask.Propose = FormationRole.FiringCells;
                ask.From = from;
                ask.Targets = targets;
                break;
            }
        }

        return ask;
    }

    /// <summary>
    /// Drops or retargets an attack order that cannot hit from the shooter's cell: its line of fire crosses a
    /// colonist, a ranged role's line is blocked (a wall), or native refused it cannot_hit from this cell
    /// before. It retargets to the top-scored hostile with a clear, answered line from that cell not refused
    /// from it, else gives no order. An unanswered pair keeps the order. A retarget becomes the role's target,
    /// so the focus holds on it.
    ///
    /// A ranged attacker with no clear line is walled in: its walled lines are remembered as refused hits, so
    /// the next stop asks firing cells around it, and once proposed firing cells are answered it moves to the
    /// nearest free one with a clear line; that cell becomes its role's cell. It returns the orders, the roles
    /// and the refused hits.
    /// </summary>
    private static (List<CombatOrder> Orders, List<CombatRole> Roles, List<HitRefusal> Refused) ClearLines(
        CombatView view,
        IReadOnlyList<CombatOrder> orders,
        GeometryReply geometry,
        IReadOnlyList<CombatRole> inputRoles,
        CombatMemory m
    )
    {
        var sight = new Dictionary<SightKey, SightLine>();
        foreach (var l in geometry.Lines.Items)
        {
            sight[new SightKey(l.Cell, l.Hostile)] = l;
        }

        var cells = new Dictionary<PawnId, Cell>();
        var taken = new HashSet<Cell>();
        foreach (var p in view.Pawns.Items)
        {
            if (p.Cell.TryGetValue(out var c))
            {
                cells[p.Id] = c;
                taken.Add(c);
            }
        }

        var refused = new List<HitRefusal>(m.CannotHit);
        var ranged = new Dictionary<PawnId, bool>();
        foreach (var r in inputRoles)
        {
            ranged[r.Pawn] = r.Ranged;
        }

        bool Blocked(PawnId pawn, PawnId target, Cell from)
        {
            if (m.RefusedHit(pawn, target, from))
            {
                return true;
            }

            return sight.TryGetValue(new SightKey(from, target), out var l) && (l.ColonistInPath || (ranged.GetValueOrDefault(pawn) && !l.LineOfFire));
        }

        var ranked = RankThreats(view);
        var roles = inputRoles.Select(r => r with { }).ToList();
        var output = new List<CombatOrder>();
        foreach (var original in orders)
        {
            var o = original;
            var known = cells.TryGetValue(o.Pawn, out var from);
            if (o.Kind != CombatOrderKind.Attack || !known || !Blocked(o.Pawn, o.Target, from))
            {
                output.Add(o);
                continue;
            }

            o = o with { Target = default };
            foreach (var h in ranked)
            {
                if (sight.TryGetValue(new SightKey(from, h.Id), out var l) && l.LineOfFire && !Blocked(o.Pawn, h.Id, from))
                {
                    o = o with { Target = h.Id };
                    break;
                }
            }

            var pawn = o.Pawn;
            var i = roles.FindIndex(r => r.Pawn == pawn);
            if (o.Target.IsEmpty)
            {
                if (!ranged.GetValueOrDefault(o.Pawn) || i < 0)
                {
                    output = KeepInFormation(output, roles, i, from);
                    continue;
                }

                foreach (var h in ranked)
                {
                    if (sight.TryGetValue(new SightKey(from, h.Id), out var l) && !l.LineOfFire && !m.RefusedHit(o.Pawn, h.Id, from))
                    {
                        refused.Add(new HitRefusal(o.Pawn, h.Id, from));
                    }
                }

                if (FiringCell(from, geometry, sight, ranked, taken) is { } found)
                {
                    taken.Add(found.Cell);
                    roles[i].Cell = found.Cell;
                    roles[i].Target = found.Target;
                    output.Add(new CombatOrder { Pawn = o.Pawn, Kind = CombatOrderKind.Move, Cell = found.Cell, Reason = CombatOrderReason.Formation });
                }
                else
                {
                    output = KeepInFormation(output, roles, i, from);
                }

                continue;
            }

            if (i >= 0)
            {
                roles[i].Target = o.Target;
            }

            output.Add(o);
        }

        return (output, roles, refused);
    }

    /// <summary>
    /// Keeps a fighter with no attack to make in the formation: it moves to its role's cell (else its standby
    /// cell) instead of being left with no order, which would hand it to the game's own response and send it
    /// at the insects alone while the rest of the line waits.
    /// </summary>
    private static List<CombatOrder> KeepInFormation(List<CombatOrder> output, IReadOnlyList<CombatRole> roles, int i, Cell from)
    {
        if (i < 0)
        {
            return output;
        }

        var cell = roles[i].Cell ?? roles[i].Home;
        if (cell is not { } c || c == from)
        {
            return output;
        }

        output.Add(new CombatOrder { Pawn = roles[i].Pawn, Kind = CombatOrderKind.Move, Cell = c, Reason = CombatOrderReason.Formation });
        return output;
    }

    /// <summary>
    /// The proposed firing cell nearest <paramref name="from"/>, free, with a clear line to a ranked hostile,
    /// and that hostile (the top-scored one); null when there is none.
    /// </summary>
    private static (Cell Cell, PawnId Target)? FiringCell(
        Cell from,
        GeometryReply geometry,
        IReadOnlyDictionary<SightKey, SightLine> sight,
        IReadOnlyList<CombatPawnState> ranked,
        HashSet<Cell> taken
    )
    {
        if (geometry.Role != FormationRole.FiringCells)
        {
            return null;
        }

        Cell best = default;
        PawnId target = default;
        foreach (var c in geometry.Proposals.Items)
        {
            if (taken.Contains(c) || (!target.IsEmpty && Distance2(from, c) >= Distance2(from, best)))
            {
                continue;
            }

            foreach (var h in ranked)
            {
                if (sight.TryGetValue(new SightKey(c, h.Id), out var l) && l.LineOfFire && !l.ColonistInPath)
                {
                    best = c;
                    target = h.Id;
                    break;
                }
            }
        }

        return target.IsEmpty ? null : (best, target);
    }

    /// <summary>
    /// Reports <paramref name="p"/> fighting its target hand to hand: in the melee stance, or between swings
    /// (warmup, cooldown) standing next to it. A melee attacker reports cooldown after each swing; reading that
    /// as the melee's end flipped hold fire every other stop.
    /// </summary>
    private static bool InMelee(CombatPawnState p, CombatPawnState target)
    {
        if (p.Stance == CombatStance.Melee)
        {
            return true;
        }

        if (p.Target.IsEmpty || !InterruptsAim(p))
        {
            return false;
        }

        return p.Cell.TryGetValue(out var a) && target.Cell.TryGetValue(out var b) && Adjacent8(a, b);
    }

    /// <summary>The hostiles in melee with one of our blockers (a blocker or melee role), either side swinging.</summary>
    private static HashSet<PawnId> MeleeLocked(CombatView view, IReadOnlyList<CombatRole> roles)
    {
        var state = new Dictionary<PawnId, CombatPawnState>();
        foreach (var p in view.Pawns.Items)
        {
            state[p.Id] = p;
        }

        var blockers = new HashSet<PawnId>();
        foreach (var r in roles)
        {
            if (r.Duty == CombatDuty.Blocker || !r.Ranged)
            {
                blockers.Add(r.Pawn);
            }
        }

        var locked = new HashSet<PawnId>();
        foreach (var p in view.Pawns.Items)
        {
            if (p.Dead || p.Downed || !InMelee(p, state.GetValueOrDefault(p.Target) ?? new CombatPawnState()))
            {
                continue;
            }

            if (blockers.Contains(p.Target))
            {
                locked.Add(p.Id); // a hostile on a blocker
            }

            if (blockers.Contains(p.Id) && !p.Target.IsEmpty)
            {
                locked.Add(p.Target); // a blocker on a hostile
            }
        }

        return locked;
    }

    /// <summary>
    /// Gives hold-fire to a gunner whose target is in melee with one of our blockers (a blocker or melee role),
    /// with a stop when the gunner is already shooting it, in place of its other orders; and fire-at-will back
    /// to a held gunner once that melee ends. A pawn whose fire mode is unknown counts as held when its last
    /// order was hold-fire.
    ///
    /// <paramref name="quiet"/> holds every gunner: the only hostiles left are spared bleeders, whom
    /// fire-at-will would otherwise shoot. <paramref name="suppressNativeFire"/> holds an ordinary autonomous
    /// holder with target exclusions and no safe target; cancelling its job alone cannot prevent reacquisition.
    /// </summary>
    private static List<CombatOrder> HoldFireOrders(
        CombatView view,
        IReadOnlyList<CombatRole> roles,
        IReadOnlyList<CombatOrder> inputOrders,
        CombatMemory m,
        bool quiet,
        ISet<PawnId> suppressNativeFire
    )
    {
        var orders = new List<CombatOrder>(inputOrders);
        var state = new Dictionary<PawnId, CombatPawnState>();
        foreach (var p in view.Pawns.Items)
        {
            state[p.Id] = p;
        }

        var locked = MeleeLocked(view, roles);
        var orderable = new HashSet<PawnId>(view.Orderable.Items);
        var lastHold = new Dictionary<PawnId, bool>();
        foreach (var o in m.Issued)
        {
            lastHold[o.Pawn] = o.CombatOrder.Kind == CombatOrderKind.FireMode && o.CombatOrder.FireMode == HoldFire;
        }

        foreach (var r in roles)
        {
            var s = state.GetValueOrDefault(r.Pawn) ?? new CombatPawnState();
            var quietPawn = quiet || suppressNativeFire.Contains(r.Pawn);
            if (!r.Ranged || r.Duty == CombatDuty.Blocker || !orderable.Contains(r.Pawn) || s.Dead || s.Downed)
            {
                continue;
            }

            var held = s.FireMode == HoldFire || (s.FireMode.Length == 0 && lastHold.GetValueOrDefault(r.Pawn));
            var pawn = r.Pawn;

            // A pawn mid-aim keeps its fire mode unless its shot is at the hostile our blocker is fighting.
            if (InterruptsAim(s) && !locked.Contains(s.Target) && !quietPawn)
            {
                continue;
            }

            if (quietPawn || locked.Contains(r.Target) || (locked.Contains(s.Target) && (r.Target.IsEmpty || s.Stance != CombatStance.Idle)))
            {
                orders.RemoveAll(o => o.Pawn == pawn);
                if (((quietPawn && !s.Target.IsEmpty) || locked.Contains(s.Target)) && s.Stance != CombatStance.Idle)
                {
                    orders.Add(new CombatOrder { Pawn = r.Pawn, Kind = CombatOrderKind.Stop, Reason = CombatOrderReason.HoldFire });
                }

                if (!held)
                {
                    orders.Add(new CombatOrder { Pawn = r.Pawn, Kind = CombatOrderKind.FireMode, FireMode = HoldFire, Reason = CombatOrderReason.HoldFire });
                }
            }
            else if (held)
            {
                orders.RemoveAll(o => o.Pawn == pawn);
                orders.Add(new CombatOrder { Pawn = r.Pawn, Kind = CombatOrderKind.FireMode, FireMode = FireAtWill, Reason = CombatOrderReason.HoldFire });
            }
        }

        return orders;
    }
}
