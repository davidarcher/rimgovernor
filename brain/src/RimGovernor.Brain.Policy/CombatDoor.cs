namespace RimGovernor.Brain.Policy;

// Go: combat_door.go

public static partial class PolicyFunctions
{
    // Door potshot constants, in cells.

    /// <summary>A hostile this close to the door shuts it.</summary>
    internal const int DoorCloseRange = 3;

    /// <summary>The nearest hostile back beyond this reopens it.</summary>
    internal const int DoorOpenRange = 6;

    /// <summary>How many gunners shoot from inside the door.</summary>
    internal const int DoorGunners = 2;

    /// <summary>
    /// The hit-and-run from a perimeter door, for a manhunter pack, a humanoid raid on squad defense and a hunt
    /// once its prey turns manhunter or hunts a colonist (the door stays once chosen); a killbox hold fights
    /// from its firing line instead. On a formation without blockers, the planned-room door nearest the hostiles
    /// becomes the potshot door: the door-gunner count of gunners nearest it take the cells just inside it and
    /// the door is held open. Every stop after, a hostile within the close range of the door closes it and the
    /// door gunners hold their cells without a target; the nearest hostile back beyond the open range opens it
    /// again.
    /// </summary>
    private static void DoorPotshot(CombatView view, bool formed, CombatMemory m)
    {
        if (m.Tactic != CombatTactic.Manhunter && m.Tactic != CombatTactic.Squad && !(m.Tactic == CombatTactic.Hunt && (m.PotshotDoor is not null || ProvokedPrey(view))))
        {
            m.PotshotDoor = null;
            return;
        }

        if (formed)
        {
            m.PotshotDoor = PotshotDoor(view, [.. m.Roles], m.EffectiveTuning().DoorGunners);
        }

        var door = m.PotshotDoor;
        if (door is null)
        {
            return;
        }

        if (!CombatDoorExists(view, door.Cell))
        {
            m.PotshotDoor = null;
            foreach (var role in m.Roles)
            {
                if (role.Duty == CombatDuty.Doorway)
                {
                    role.Target = default;
                }
            }

            return;
        }

        var t = m.EffectiveTuning();
        long closeR = t.DoorCloseRange, openR = t.DoorOpenRange;
        var near = HostileDistance(view, door.Cell);
        var mode = door.Mode;
        if (near <= closeR * closeR)
        {
            mode = DoorMode.Close;
        }
        else if (near > openR * openR)
        {
            mode = DoorMode.HoldOpen;
        }

        if (mode != door.Mode)
        {
            door.Mode = mode;
            door.Sent = false;
        }

        door.Repairer = default;
        if (near > openR * openR && view.DamagedDoors.Items.Contains(door.Cell))
        {
            door.Repairer = DoorRepairer(view, m.Roles, door.Cell);
        }

        if (door.Mode == DoorMode.Close)
        {
            foreach (var role in m.Roles)
            {
                if (role.Duty == CombatDuty.Doorway)
                {
                    role.Target = default;
                }
            }
        }
    }

    /// <summary>Uses the current native room boundary, never the saved layout or a door retained by an earlier combat stop.</summary>
    private static bool CombatDoorExists(CombatView view, Cell cell)
    {
        if (view.DoorStates.TryGetValue(out var doors))
        {
            return doors.Items.Any(d => d.Cell == cell);
        }

        return view.Rooms.Items.Any(room => room.Doors.Items.Contains(cell));
    }

    /// <summary>A live prey that turned manhunter or is hunting a colonist: the hunt falls back to the door loop.</summary>
    private static bool ProvokedPrey(CombatView view)
    {
        var live = new HashSet<PawnId>();
        foreach (var p in RankThreats(view))
        {
            live.Add(p.Id);
        }

        return view.Threats.Items.Any(t => live.Contains(ThreatPawn(t)) && (Positive(t.Manhunter) || Positive(t.Hunting)));
    }

    /// <summary>The door gunner nearest the door with a known cell, or none.</summary>
    private static PawnId DoorRepairer(CombatView view, IReadOnlyList<CombatRole> roles, Cell door)
    {
        PawnId best = default;
        long bestD = 0;
        foreach (var p in view.Pawns.Items)
        {
            if (!p.Cell.TryGetValue(out var c) || !roles.Any(r => r.Pawn == p.Id && r.Duty == CombatDuty.Doorway))
            {
                continue;
            }

            var d = Distance2(c, door);
            if (best.IsEmpty || d < bestD)
            {
                best = p.Id;
                bestD = d;
            }
        }

        return best;
    }

    /// <summary>Picks the door and posts the door gunners, or null: a formation with blockers, no planned door, or no gunner has none.</summary>
    private static PodDoor? PotshotDoor(CombatView view, List<CombatRole> roles, int gunnerCount)
    {
        if (roles.Any(r => r.Duty == CombatDuty.Blocker))
        {
            return null;
        }

        var doors = new List<Cell>();
        foreach (var r in view.Rooms.Items)
        {
            doors.AddRange(r.Doors.Items);
        }

        var pack = new List<Cell>();
        foreach (var h in RankThreats(view))
        {
            if (h.Cell.TryGetValue(out var c))
            {
                pack.Add(c);
            }
        }

        if (doors.Count == 0 || pack.Count == 0)
        {
            return null;
        }

        doors = PolicySort.ByLess(doors, (a, b) => NearestDistance(a, pack) < NearestDistance(b, pack));
        var door = doors[0];
        var cells = InsideDoor(view, door, pack[NearestIndex(door, pack)]);
        var at = new Dictionary<PawnId, Cell>();
        foreach (var p in view.Pawns.Items)
        {
            if (p.Cell.TryGetValue(out var c))
            {
                at[p.Id] = c;
            }
        }

        // A gunner is a ranged role or, on squad defense against melee raiders, a defender carrying a ranged
        // weapon: it shoots from the door instead of charging.
        var armed = new Dictionary<PawnId, bool>();
        foreach (var d in view.Defenders.Items)
        {
            armed[d.Id] = Positive(d.RangedEquipped);
        }

        var gunners = new List<int>();
        for (var i = 0; i < roles.Count; i++)
        {
            if (roles[i].Ranged || armed.GetValueOrDefault(roles[i].Pawn))
            {
                gunners.Add(i);
            }
        }

        gunners = PolicySort.ByLess(
            gunners,
            (a, b) =>
            {
                var oka = at.TryGetValue(roles[a].Pawn, out var ca);
                var okb = at.TryGetValue(roles[b].Pawn, out var cb);
                if (oka != okb)
                {
                    return oka;
                }

                return Distance2(ca, door) < Distance2(cb, door);
            }
        );

        var n = Math.Min(gunnerCount, Math.Min(cells.Count, gunners.Count));
        if (n == 0)
        {
            return null;
        }

        for (var k = 0; k < n; k++)
        {
            var role = roles[gunners[k]];
            role.Cell = cells[k];
            role.Duty = CombatDuty.Doorway;
            role.Ranged = true;
        }

        return new PodDoor { Cell = door, Mode = DoorMode.HoldOpen };
    }

    /// <summary>The squared distance from <paramref name="c"/> to the nearest live hostile with a known cell.</summary>
    private static long HostileDistance(CombatView view, Cell c)
    {
        var cells = new List<Cell>();
        foreach (var h in RankThreats(view))
        {
            if (h.Cell.TryGetValue(out var at))
            {
                cells.Add(at);
            }
        }

        return NearestDistance(c, cells);
    }
}
