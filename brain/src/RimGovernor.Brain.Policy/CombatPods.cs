namespace RimGovernor.Brain.Policy;

// Go: combat_pods.go
//
// Port note: PodDoor and PodArrival are records because CombatMemory holds them as mutable working state and
// copies them with `with`; Go's in-place marking of PodDoor.Sent becomes a returned copy (KeepSent) or an
// assignment on the working memory's door (PodDoorOrders).

/// <summary>A drop-pod raid's arrival: the cells the pods land on and the tick the last pod opens (spawn + 520).</summary>
public sealed record PodArrival
{
    public EquatableArray<Cell> Landing { get; init; }

    public Tick Open { get; init; }
}

/// <summary>One standing room on the map: its floor and the doors in its walls. Roofed is every floor cell roofed.</summary>
public sealed record CombatRoom
{
    public Rectangle Interior { get; init; }

    public EquatableArray<Cell> Doors { get; init; }

    public bool Roofed { get; init; }

    public Fact<RoomRole> Role { get; init; }

    public Fact<bool> Burning { get; init; }

    /// <summary>Reports c on the room's floor.</summary>
    internal bool Contains(Cell c)
    {
        var inside = Interior;
        return c.X >= inside.X && c.X < inside.X + inside.Width && c.Z >= inside.Z && c.Z < inside.Z + inside.Height;
    }
}

/// <summary>A door the pods tactic wants in a mode, and whether that order went out.</summary>
public sealed record PodDoor
{
    public Cell Cell { get; set; }

    public DoorMode Mode { get; set; }

    public bool Sent { get; set; }

    /// <summary>The door gunner sent to repair the manhunter potshot door, while it is damaged and no animal is near.</summary>
    public PawnId Repairer { get; set; }

    public PawnId Opener { get; set; }
}

public static partial class PolicyFunctions
{
    /// <summary>How many armed colonists answer each landing cell.</summary>
    internal const int PodRespondersPerPod = 2;

    /// <summary>How close to a landing cell a non-combatant is moved away, in cells.</summary>
    internal const int PodDangerRadius = 12;

    /// <summary>
    /// The pods tactic's roles: the nearest armed colonists respond, each on the nearest live hostile once one is
    /// out; with a landing cell inside the perimeter they take the layout's inner line instead. Two responders
    /// flank each door of a landing room on its far side, the door held open, close-range fighters first.
    /// Non-combatants in a landing room or near a landing cell move to the landing-free room cell farthest from
    /// the pods.
    /// </summary>
    internal static (List<CombatRole> Roles, List<PodDoor> Doors) PodFormation(CombatView view, PodArrival pods, GeometryReply geometry)
    {
        var at = new Dictionary<PawnId, Cell>();
        var reach = new Dictionary<PawnId, double>();
        foreach (var p in view.Pawns.Items)
        {
            if (p.Cell.TryGetValue(out var c))
            {
                at[p.Id] = c;
            }

            reach[p.Id] = p.WeaponRange;
        }

        var armed = new List<SquadDefenderFacts>();
        var civilians = new List<SquadDefenderFacts>();
        foreach (var d in view.Defenders.Items)
        {
            if (SquadDefenderEligible(d) && Positive(d.Armed))
            {
                armed.Add(d);
            }
            else if (Evacuable(d))
            {
                civilians.Add(d);
            }
        }

        // Nearest to a landing cell first; an unknown cell ranks last.
        long Near(PawnId id) => at.TryGetValue(id, out var c) ? NearestDistance(c, pods.Landing.Items) : 1L << 62;

        var roles = new List<CombatRole>();
        var doors = new List<PodDoor>();
        var taken = new HashSet<PawnId>();
        var pool = new Queue<SquadDefenderFacts>(CloseRange(armed, reach, Near));
        foreach (var door in LandingDoors(view, pods))
        {
            var held = false;
            foreach (var cell in door.Slots)
            {
                if (!geometry.Stands(cell))
                {
                    continue;
                }

                if (pool.Count == 0)
                {
                    break;
                }

                var d = pool.Dequeue();
                taken.Add(d.Id);
                roles.Add(new CombatRole { Pawn = d.Id, Cell = cell, Target = NearestHostile(view, cell), Ranged = Positive(d.RangedEquipped), Duty = CombatDuty.Doorway });
                held = true;
            }

            if (held)
            {
                doors.Add(new PodDoor { Cell = door.Cell, Mode = DoorMode.HoldOpen });
            }
        }

        armed.RemoveAll(d => taken.Contains(d.Id));
        armed = PolicySort.ByLess(armed, (a, b) => Near(a.Id) < Near(b.Id));
        var n = Math.Max(Math.Min(Math.Max(PodRespondersPerPod * pods.Landing.Count, PodRespondersPerPod), MaxSquadDefenders) - taken.Count, 0);
        if (armed.Count > n)
        {
            armed = armed.GetRange(0, n);
        }

        var inner = new List<Cell>();
        if (view.Layout.TryGetValue(out var layout) && layout.Retreat.Count > 0 && PodsInside(layout, pods))
        {
            inner = [.. layout.Retreat.Items];
        }

        foreach (var d in armed)
        {
            at.TryGetValue(d.Id, out var from);
            var role = new CombatRole { Pawn = d.Id, Target = NearestHostile(view, from), Ranged = Positive(d.RangedEquipped) };
            if (inner.Count > 0)
            {
                var i = NearestIndex(from, inner);
                var cell = inner[i];
                inner.RemoveAt(i);
                role.Cell = cell;
                role.Retreat = true;
            }

            roles.Add(role);
        }

        roles.AddRange(Evacuees(view, pods, civilians, at));
        return (SortRoles(roles), doors);
    }

    /// <summary>
    /// Ranks the armed for the doorway slots: melee-only brawlers first, best armored first, then the ranged by
    /// shortest weapon range (a shotgun before a rifle), an unknown range last; nearest the pods on a tie.
    /// </summary>
    private static List<SquadDefenderFacts> CloseRange(List<SquadDefenderFacts> armed, Dictionary<PawnId, double> reach, Func<PawnId, long> near)
    {
        static bool Melee(SquadDefenderFacts d) => Positive(d.MeleeEquipped) && !Positive(d.RangedEquipped);
        double Range(SquadDefenderFacts d) => reach.TryGetValue(d.Id, out var r) && r > 0 ? r : 1 << 20;

        return PolicySort.ByLess(
            ByArmor([.. armed]),
            (a, b) =>
            {
                bool ma = Melee(a), mb = Melee(b);
                if (ma != mb)
                {
                    return ma;
                }

                if (ma)
                {
                    return false; // ByArmor's order
                }

                double ra = Range(a), rb = Range(b);
                if (ra != rb)
                {
                    return ra < rb;
                }

                long na = near(a.Id), nb = near(b.Id);
                if (na != nb)
                {
                    return na < nb;
                }

                return a.Id < b.Id;
            });
    }

    /// <summary>A landing room's door and the cells flanking it on its far side.</summary>
    internal sealed record PodDoorway(Cell Cell, List<Cell> Slots);

    /// <summary>
    /// The doors of every room holding a landing cell, each with the two far-side cells beside the cell straight
    /// out that are in no landing room and on no room's wall.
    /// </summary>
    private static List<PodDoorway> LandingDoors(CombatView view, PodArrival pods)
    {
        bool Landing(Cell c) => view.Rooms.Items.Any(r => r.Contains(c) && pods.Landing.Items.Any(r.Contains));

        bool Wall(Cell c)
        {
            foreach (var r in view.Rooms.Items)
            {
                var inside = r.Interior;
                var ring = c.X >= inside.X - 1 && c.X <= inside.X + inside.Width && c.Z >= inside.Z - 1 && c.Z <= inside.Z + inside.Height && !r.Contains(c);
                if (ring && !r.Doors.Items.Contains(c))
                {
                    return true;
                }
            }

            return false;
        }

        var output = new List<PodDoorway>();
        foreach (var r in view.Rooms.Items)
        {
            if (!pods.Landing.Items.Any(r.Contains))
            {
                continue;
            }

            foreach (var door in r.Doors.Items)
            {
                if (Outward(r.Interior, door) is not { } n || output.Any(p => p.Cell == door))
                {
                    continue;
                }

                var o = new Cell(door.X + n.X, door.Z + n.Z);
                var t = new Cell(n.Z, n.X);
                var w = new PodDoorway(door, []);
                foreach (var c in new[] { new Cell(o.X + t.X, o.Z + t.Z), new Cell(o.X - t.X, o.Z - t.Z) })
                {
                    if (c.X >= 0 && c.Z >= 0 && !Landing(c) && !Wall(c))
                    {
                        w.Slots.Add(c);
                    }
                }

                output.Add(w);
            }
        }

        return output;
    }

    /// <summary>
    /// A pods formation's geometry ask: every doorway flank cell named for its standability, then the shooters'
    /// cells for their lines of fire, against the live hostiles (none before the pods open). Without a
    /// landing-room door there is nothing to ask.
    /// </summary>
    private static GeometryRequest? PodsAsk(CombatView view, PodArrival pods)
    {
        var ask = new GeometryRequest();
        foreach (var door in LandingDoors(view, pods))
        {
            foreach (var c in door.Slots)
            {
                if (!ask.Cells.Contains(c))
                {
                    ask.Cells.Add(c);
                }
            }
        }

        if (ask.Cells.Count == 0)
        {
            return null;
        }

        foreach (var h in RankThreats(view))
        {
            if (ask.Hostiles.Count < MaxGeometryHostiles)
            {
                ask.Hostiles.Add(h.Id);
            }
        }

        if (ask.Hostiles.Count > 0)
        {
            foreach (var c in ShooterCells(view))
            {
                if (!ask.Cells.Contains(c))
                {
                    ask.Cells.Add(c);
                }
            }
        }

        if (ask.Cells.Count > MaxGeometryCells)
        {
            ask.Cells = ask.Cells.GetRange(0, MaxGeometryCells);
        }

        return ask;
    }

    /// <summary>The door orders the pods tactic (and the manhunter potshot door) has not sent yet.</summary>
    internal static List<CombatOrder> PodDoorOrders(CombatView view, CombatMemory m)
    {
        var output = new List<CombatOrder>();
        m.PodDoors.RemoveAll(d => !CombatDoorExists(view, d.Cell));

        PodDoor Send(PodDoor d)
        {
            if (view.DoorStates.TryGetValue(out var states))
            {
                var index = states.Items.ToList().FindIndex(s => s.Cell == d.Cell);
                if (index >= 0)
                {
                    var state = states[index].HoldOpen;
                    var want = d.Mode == DoorMode.HoldOpen;
                    if (d.Mode == DoorMode.Forbid || d.Mode == DoorMode.Allow)
                    {
                        state = states[index].Forbidden;
                        want = d.Mode == DoorMode.Forbid;
                    }

                    if (state.TryGetValue(out var got))
                    {
                        d = d with { Sent = got == want };
                    }
                }
            }

            if (!d.Sent)
            {
                output.Add(new CombatOrder { Kind = CombatOrderKind.Door, Cell = d.Cell, Door = d.Mode, Reason = CombatOrderReason.Formation });
                d = d with { Sent = true };
                if (d.Mode == DoorMode.HoldOpen && !m.HeldDoors.Contains(d.Cell))
                {
                    m.HeldDoors.Add(d.Cell);
                }
            }

            return d;
        }

        for (var i = 0; i < m.PodDoors.Count; i++)
        {
            m.PodDoors[i] = Send(m.PodDoors[i]);
        }

        var wait = m.WaitDoors.Items.Select(Send).ToList();
        m.WaitDoors = EquatableArray.From(wait);
        if (m.PotshotDoor is not null)
        {
            m.PotshotDoor = Send(m.PotshotDoor);
        }

        if (m.ChokeDoor is not null)
        {
            m.ChokeDoor = Send(m.ChokeDoor);
        }

        // A formation that releases a door closes it until a fresh read confirms the latch is clear. Retained
        // intent supplies ownership; observed state supplies reconciliation, including uncertain receipts.
        if (view.DoorStates.TryGetValue(out var observed))
        {
            bool Active(Cell cell) =>
                m.PodDoors.Concat(m.WaitDoors.Items).Any(d => d.Cell == cell && d.Mode == DoorMode.HoldOpen)
                || (m.PotshotDoor is { } potshot && potshot.Cell == cell && potshot.Mode == DoorMode.HoldOpen)
                || (m.ChokeDoor is { } choke && choke.Cell == cell && choke.Mode == DoorMode.HoldOpen);

            m.HeldDoors.RemoveAll(cell =>
            {
                if (Active(cell))
                {
                    return false;
                }

                var index = observed.Items.ToList().FindIndex(d => d.Cell == cell);
                if (index < 0 || DoorFactFalse(observed[index].HoldOpen))
                {
                    return true;
                }

                if (!output.Any(o => o.Kind == CombatOrderKind.Door && o.Cell == cell && o.Door == DoorMode.Close))
                {
                    output.Add(new CombatOrder { Kind = CombatOrderKind.Door, Cell = cell, Door = DoorMode.Close, Reason = CombatOrderReason.Formation });
                }

                return false;
            });
        }

        return output;
    }

    /// <summary>Retires an unapplied door selection. A later selection must come from a new native room observation rather than this retained order.</summary>
    public static CombatMemory RefuseDoor(this CombatMemory memory, Cell cell)
    {
        var m = memory.Copy();
        m.PodDoors.RemoveAll(d => d.Cell == cell);
        m.WaitDoors = EquatableArray.From(m.WaitDoors.Items.Where(d => d.Cell != cell));
        if (m.PotshotDoor is { } potshot && potshot.Cell == cell)
        {
            m.PotshotDoor = null;
        }

        if (m.ChokeDoor is { } choke && choke.Cell == cell)
        {
            m.ChokeDoor = null;
        }

        return m;
    }

    /// <summary>The want list with each door already sent in the same mode marked sent, so a re-formation does not repeat it.</summary>
    internal static List<PodDoor> KeepSent(IEnumerable<PodDoor> had, IEnumerable<PodDoor> want)
    {
        var prior = had.ToList();
        return [.. want.Select(w => prior.Any(h => h.Cell == w.Cell && h.Mode == w.Mode && h.Sent) ? w with { Sent = true } : w)];
    }

    /// <summary>A colonist the pods tactic moves but never fights with: alive, up, sane and free, but unarmed or incapable of violence.</summary>
    private static bool Evacuable(SquadDefenderFacts d)
    {
        var (busy, known) = SquadDraftBusy(d);
        return !Positive(d.Dead) && !Positive(d.Downed) && !Positive(d.MentalState) && known && !busy
            && (!Positive(d.ViolenceCapable) || !Positive(d.Armed));
    }

    /// <summary>
    /// The non-combatants' moves: each one in a landing room or within the danger radius of a landing cell takes
    /// the free cell of a landing-free room farthest from the pods. Without such a room nobody moves.
    /// </summary>
    private static List<CombatRole> Evacuees(CombatView view, PodArrival pods, List<SquadDefenderFacts> civilians, Dictionary<PawnId, Cell> at)
    {
        var safe = new List<Cell>();
        bool LandingRoom(Cell c) => view.Rooms.Items.Any(r => r.Contains(c) && pods.Landing.Items.Any(r.Contains));
        foreach (var r in view.Rooms.Items)
        {
            if (pods.Landing.Items.Any(r.Contains))
            {
                continue;
            }

            var inside = r.Interior;
            for (var z = inside.Z; z < inside.Z + inside.Height; z++)
            {
                for (var x = inside.X; x < inside.X + inside.Width; x++)
                {
                    safe.Add(new Cell(x, z));
                }
            }
        }

        // Farthest from the pods first, then by cell.
        safe = PolicySort.ByLess(
            safe,
            (a, b) =>
            {
                long da = NearestDistance(a, pods.Landing.Items), db = NearestDistance(b, pods.Landing.Items);
                return da != db ? da > db : CellLess(a, b);
            });
        var output = new List<CombatRole>();
        foreach (var d in civilians)
        {
            if (!at.TryGetValue(d.Id, out var c) || safe.Count == 0 || (!LandingRoom(c) && NearestDistance(c, pods.Landing.Items) > (long)PodDangerRadius * PodDangerRadius))
            {
                continue;
            }

            var cell = safe[0];
            safe.RemoveAt(0);
            output.Add(new CombatRole { Pawn = d.Id, Cell = cell, Duty = CombatDuty.Evacuee });
        }

        return output;
    }

    /// <summary>Reports a landing cell at or behind the layout's firing line: the pods came down inside the perimeter.</summary>
    private static bool PodsInside(CombatLayout layout, PodArrival pods) =>
        pods.Landing.Items.Any(c => BehindFiringLine(layout.Firing.Items, layout.Toward, c));

    /// <summary>The pods tactic's re-formation row: a responder's target is down, or a responder has none while a hostile is out. A fight waiting behind closed doors re-forms only on a raid phase change.</summary>
    internal static bool ReformPods(CombatView view, CombatMemory m)
    {
        if (m.PodWait)
        {
            return false;
        }

        foreach (var r in m.Roles.Items)
        {
            if (r.Duty == CombatDuty.Evacuee)
            {
                continue;
            }

            if (r.Target.IsEmpty)
            {
                if (!NearestHostile(view, default).IsEmpty)
                {
                    return true;
                }

                continue;
            }

            if (view.Pawns.Items.Any(p => p.Id == r.Target && (p.Dead || p.Downed)))
            {
                return true;
            }

            if (view.Positional.Items.Any(t => t.Id == r.Target && (Positive(t.Dead) || Positive(t.Downed))))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The squared distance from c to the nearest of cells; huge with none.</summary>
    internal static long NearestDistance(Cell c, IReadOnlyList<Cell> cells) =>
        cells.Count == 0 ? 1L << 62 : Distance2(c, cells[NearestIndex(c, cells)]);
}
