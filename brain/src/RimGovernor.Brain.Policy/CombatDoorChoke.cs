namespace RimGovernor.Brain.Policy;

// Go: combat_door_choke.go

public static partial class PolicyFunctions
{
    /// <summary>
    /// Uses an observed room doorway when the established defense corridor is unavailable. Ranged threats,
    /// explosions, custody, fire and unknown physical door facts exclude this formation.
    /// </summary>
    private static RoomDoor? MeleeDoorSite(CombatView view)
    {
        if (view.Hunt || Outmatched(view) || LiveExploders(view).Count > 0)
        {
            return null;
        }

        if (view.Layout.TryGetValue(out var layout) && layout.Choke.IsKnown)
        {
            return null;
        }

        if (Brawlers(view.Defenders.Items).Count == 0|| RankThreats(view).Count == 0 || !view.Defenders.Items.Any(d => SquadDefenderEligible(d) && Positive(d.RangedEquipped)))
        {
            return null;
        }

        var down = DownPawns(view);
        foreach (var threat in view.Threats.Items)
        {
            if (Positive(threat.Dead) || Positive(threat.Downed) || down.Contains(ThreatPawn(threat)))
            {
                continue;
            }

            var known = threat.RangedEquipped.TryGetValue(out var ranged);
            if (threat.Building || !known || ranged)
            {
                return null;
            }
        }

        if (!view.DoorStates.TryGetValue(out var doorArray))
        {
            return null;
        }

        var doors = doorArray.Items;
        var choices = new List<RoomDoor>();
        foreach (var room in view.Rooms.Items)
        {
            var rk = room.Role.TryGetValue(out var role);
            var bk = room.Burning.TryGetValue(out var burning);
            if (!rk || !bk || burning || !room.Roofed || role == RoomRole.PrisonCell || role == RoomRole.PrisonBarracks || role == RoomRole.ContainmentCell || role == RoomRole.IsolationRoom)
            {
                continue;
            }

            foreach (var cell in room.Doors.Items)
            {
                var at = IndexOfDoor(doors, cell);
                if (at < 0)
                {
                    continue;
                }

                var door = doors[at];
                var pk = door.PlayerOwned.TryGetValue(out var owned);
                var fk = door.Forbidden.TryGetValue(out var forbidden);
                var ok = door.Open.TryGetValue(out var open);
                var hk = door.HoldOpen.IsKnown;
                var ck = door.BlockedOpen.TryGetValue(out var blocked);
                if (!pk || !owned || !fk || forbidden || !ok || !hk || !ck || (blocked && !open))
                {
                    continue;
                }

                var safe = true;
                foreach (var other in room.Doors.Items)
                {
                    if (other == cell)
                    {
                        continue;
                    }

                    var i = IndexOfDoor(doors, other);
                    if (i < 0 || !DoorFactFalse(doors[i].Open) || !DoorFactFalse(doors[i].HoldOpen))
                    {
                        safe = false;
                    }
                }

                var inside = new Cell((2 * cell.X) - door.Outside.X, (2 * cell.Z) - door.Outside.Z);
                var direction = new Cell(door.Outside.X - cell.X, door.Outside.Z - cell.Z);
                if (!room.Contains(inside) || Math.Abs(direction.X) + Math.Abs(direction.Z) != 1)
                {
                    continue;
                }

                foreach (var hostile in RankThreats(view))
                {
                    var known = hostile.Cell.TryGetValue(out var hostileAt);
                    if (!known || room.Contains(hostileAt) || (((hostileAt.X - cell.X) * direction.X) + ((hostileAt.Z - cell.Z) * direction.Z)) <= 0)
                    {
                        safe = false;
                    }
                }

                if (safe)
                {
                    choices.Add(door);
                }
            }
        }

        choices = PolicySort.ByLess(
            choices,
            (a, b) =>
            {
                var di = HostileDistance(view, a.Cell);
                var dj = HostileDistance(view, b.Cell);
                if (di != dj)
                {
                    return di < dj;
                }

                return a.Cell.X < b.Cell.X || (a.Cell.X == b.Cell.X && a.Cell.Z < b.Cell.Z);
            }
        );

        return choices.Count == 0 ? null : choices[0];
    }

    private static int IndexOfDoor(IReadOnlyList<RoomDoor> doors, Cell cell)
    {
        for (var i = 0; i < doors.Count; i++)
        {
            if (doors[i].Cell == cell)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool DoorFactFalse(Fact<bool> f) => f.TryGetValue(out var v) && !v;

    private static (List<Cell> Blockers, List<Cell> Support) MeleeDoorCells(CombatView view, RoomDoor door)
    {
        var ranked = RankThreats(view);
        if (ranked.Count == 0)
        {
            return ([], []);
        }

        if (!ranked[0].Cell.TryGetValue(out var from))
        {
            return ([], []);
        }

        var blockers = InsideDoor(view, door.Cell, from);
        var inward = new Cell(door.Cell.X - door.Outside.X, door.Cell.Z - door.Outside.Z);
        var support = new List<Cell>();
        for (var step = 3; step <= 4; step++)
        {
            for (var offset = -2; offset <= 2; offset++)
            {
                var cell = new Cell(door.Cell.X + (step * inward.X) + (offset * inward.Z), door.Cell.Z + (step * inward.Z) + (offset * inward.X));
                if (view.Rooms.Items.Any(r => r.Doors.Items.Contains(door.Cell) && r.Contains(cell)))
                {
                    support.Add(cell);
                }
            }
        }

        return (blockers, support);
    }

    private static GeometryRequest MeleeDoorAsk(CombatView view, RoomDoor door)
    {
        var (blockers, support) = MeleeDoorCells(view, door);
        var reserve = new Cell(door.Cell.X + (2 * (door.Cell.X - door.Outside.X)), door.Cell.Z + (2 * (door.Cell.Z - door.Outside.Z)));
        var ask = new GeometryRequest { Cells = [.. blockers, .. support, door.Cell, reserve] };
        foreach (var hostile in RankThreats(view))
        {
            if (ask.Hostiles.Count < MaxGeometryHostiles)
            {
                ask.Hostiles.Add(hostile.Id);
            }
        }

        return ask;
    }

    private static List<CombatRole> MeleeDoorFormation(CombatView view, GeometryReply geometry, RoomDoor door, IReadOnlyList<PawnId> relieved)
    {
        var (blockers, support) = MeleeDoorCells(view, door);
        if (blockers.Count == 0 || !geometry.Stands(blockers[0]) || !geometry.Stands(door.Cell))
        {
            return [];
        }

        blockers.RemoveAll(c => !geometry.Stands(c));
        support.RemoveAll(c => !geometry.Stands(c));
        var pool = Rotated(Brawlers(view.Defenders.Items), relieved);
        if (pool.Count == 0)
        {
            return [];
        }

        var target = RankThreats(view)[0].Id;
        var reserve = new Cell(door.Cell.X + (2 * (door.Cell.X - door.Outside.X)), door.Cell.Z + (2 * (door.Cell.Z - door.Outside.Z)));
        if (geometry.Stands(reserve))
        {
            blockers.Add(reserve);
        }

        var roles = BrawlerRoles(view, view.Defenders.Items, true, new GeometryReply { Proposals = EquatableArray.From(blockers), Standable = geometry.Standable }, relieved);
        var next = 0;
        foreach (var defender in view.Defenders.Items)
        {
            if (!SquadDefenderEligible(defender) || !Positive(defender.RangedEquipped) || next >= support.Count)
            {
                continue;
            }

            var cell = support[next++];
            roles.Add(new CombatRole { Pawn = defender.Id, Cell = cell, Target = target, Ranged = true });
        }

        return SortRoles(roles);
    }

    /// <summary>
    /// Admits the choke only after its healthy, plan-owned blockers and supporting shooters stand in position.
    /// One shooter opens a closed door by walking onto it, then returns to its assigned cell on the next read.
    /// </summary>
    private static void MeleeDoorTurn(CombatView view, CombatMemory memory)
    {
        var door = memory.ChokeDoor;
        if (door is null)
        {
            return;
        }

        var known = view.DoorStates.TryGetValue(out var statesArray);
        IReadOnlyList<RoomDoor> states = known ? statesArray.Items : Array.Empty<RoomDoor>();
        var index = IndexOfDoor(states, door.Cell);
        if (!known || index < 0 || !CombatDoorExists(view, door.Cell))
        {
            memory.ChokeDoor = null;
            return;
        }

        long openR = memory.EffectiveTuning().DoorOpenRange;
        var observed = states[index];
        var ready = memory.Tactic == CombatTactic.DoorChoke && !memory.Wait && !memory.Scattered;
        var blockers = 0;
        foreach (var role in memory.Roles)
        {
            if (role.Duty != CombatDuty.Blocker && !role.Ranged)
            {
                continue;
            }

            var pawn = view.Pawns.Items.FirstOrDefault(p => p.Id == role.Pawn);
            var defender = view.Defenders.Items.FirstOrDefault(p => p.Id == role.Pawn);
            if (pawn is null || defender is null || !SquadDefenderEligible(defender) || !view.Orderable.Items.Contains(role.Pawn) || role.Retreat || role.Cell is null)
            {
                ready = false;
                continue;
            }

            var cellKnown = pawn.Cell.TryGetValue(out var at);
            if (role.Duty == CombatDuty.Blocker)
            {
                blockers++;
            }

            if (role.Pawn != door.Opener && (!cellKnown || at != role.Cell.Value))
            {
                ready = false;
            }
        }

        ready = ready && blockers > 0;
        var ok = observed.Open.TryGetValue(out var open);
        if (!ok || !DoorFactFalse(observed.Forbidden))
        {
            ready = false;
        }

        if (!open && HostileDistance(view, door.Cell) <= openR * openR)
        {
            ready = false;
        }

        var mode = ready ? DoorMode.HoldOpen : DoorMode.Close;
        if (mode != door.Mode)
        {
            door.Mode = mode;
            door.Sent = false;
        }

        if (!ready || open)
        {
            door.Opener = default;
            return;
        }

        if (HostileDistance(view, door.Cell) <= openR * openR)
        {
            return;
        }

        if (!door.Opener.IsEmpty)
        {
            return;
        }

        foreach (var role in memory.Roles)
        {
            if (role.Ranged && view.Orderable.Items.Contains(role.Pawn) && !memory.Unreachable.Contains(door.Cell))
            {
                door.Opener = role.Pawn;
                return;
            }
        }
    }

    /// <summary>
    /// Releases every latch this fight opened, even if a later formation dropped its door selection. Routine
    /// policy may then reopen safe logistics links; native decides when an unblocked door physically closes.
    /// </summary>
    public static List<CombatOrder> CombatDoorClears(CombatMemory memory)
    {
        var cells = PolicySort.ByLess(memory.HeldDoors, (a, b) => a.X < b.X || (a.X == b.X && a.Z < b.Z));
        return [.. cells.Select(cell => new CombatOrder { Kind = CombatOrderKind.Door, Cell = cell, Door = DoorMode.Close, Reason = CombatOrderReason.Formation })];
    }
}
