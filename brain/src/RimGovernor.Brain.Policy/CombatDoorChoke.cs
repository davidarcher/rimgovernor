namespace RimGovernor.Brain.Policy;


public static partial class CombatDoorRules
{
    /// <summary>
    /// Uses an observed room doorway when the established defense corridor is unavailable. Ranged threats,
    /// explosions, custody, fire and unknown physical door facts exclude this formation.
    /// </summary>
    internal static RoomDoor? MeleeDoorSite(CombatView view)
    {
        if (view.Hunt || CombatWaitRules.Outmatched(view) || CombatManhunterRules.LiveExploders(view).Count > 0)
        {
            return null;
        }

        if (view.Layout is { } layout && layout.Choke is CombatChoke.Corridor)
        {
            return null;
        }

        if (CombatBlockingRules.Brawlers(view.Defenders.Items).Count == 0|| CombatThreatRules.RankThreats(view).Count == 0 || !view.Defenders.Items.Any(d => SquadDefenseRules.SquadDefenderEligible(d) && d.RangedEquipped))
        {
            return null;
        }

        var down = CombatManhunterRules.DownPawns(view);
        foreach (var threat in view.Threats.Items)
        {
            if (threat.Dead || threat.Downed || down.Contains(CombatDecideRules.ThreatPawn(threat)))
            {
                continue;
            }

            if (threat.Building || threat.RangedEquipped)
            {
                return null;
            }
        }

        var doors = view.DoorStates.Items;
        var choices = new List<RoomDoor>();
        foreach (var room in view.Rooms.Items)
        {
            var role = room.Role;
            if (room.Burning || !room.Roofed || role == RoomRole.PrisonCell || role == RoomRole.PrisonBarracks || role == RoomRole.ContainmentCell)
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
                if (!door.PlayerOwned || door.Forbidden || (door.BlockedOpen && !door.Open))
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
                    if (i < 0 || doors[i].Open || doors[i].HoldOpen)
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

                foreach (var hostile in CombatThreatRules.RankThreats(view))
                {
                    var hostileAt = hostile.Cell;
                    if (room.Contains(hostileAt) || (((hostileAt.X - cell.X) * direction.X) + ((hostileAt.Z - cell.Z) * direction.Z)) <= 0)
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

    private static (List<Cell> Blockers, List<Cell> Support) MeleeDoorCells(CombatView view, RoomDoor door)
    {
        var ranked = CombatThreatRules.RankThreats(view);
        if (ranked.Count == 0)
        {
            return ([], []);
        }

        var from = ranked[0].Cell;
        var blockers = CombatManhunterRules.InsideDoor(view, door.Cell, from);
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

    internal static GeometryRequest MeleeDoorAsk(CombatView view, RoomDoor door)
    {
        var (blockers, support) = MeleeDoorCells(view, door);
        var reserve = new Cell(door.Cell.X + (2 * (door.Cell.X - door.Outside.X)), door.Cell.Z + (2 * (door.Cell.Z - door.Outside.Z)));
        var ask = new GeometryRequest { Cells = [.. blockers, .. support, door.Cell, reserve] };
        foreach (var hostile in CombatThreatRules.RankThreats(view))
        {
            if (ask.Hostiles.Count < CombatDecideRules.MaxGeometryHostiles)
            {
                ask.Hostiles.Add(hostile.Id);
            }
        }

        return ask;
    }

    internal static List<CombatRole> MeleeDoorFormation(CombatView view, GeometryReply geometry, RoomDoor door, IReadOnlyList<PawnId> relieved)
    {
        var (blockers, support) = MeleeDoorCells(view, door);
        if (blockers.Count == 0 || !geometry.Stands(blockers[0]) || !geometry.Stands(door.Cell))
        {
            return [];
        }

        blockers.RemoveAll(c => !geometry.Stands(c));
        support.RemoveAll(c => !geometry.Stands(c));
        var pool = CombatBlockingRules.Rotated(CombatBlockingRules.Brawlers(view.Defenders.Items), relieved);
        if (pool.Count == 0)
        {
            return [];
        }

        var target = CombatThreatRules.RankThreats(view)[0].Id;
        var reserve = new Cell(door.Cell.X + (2 * (door.Cell.X - door.Outside.X)), door.Cell.Z + (2 * (door.Cell.Z - door.Outside.Z)));
        if (geometry.Stands(reserve))
        {
            blockers.Add(reserve);
        }

        var roles = CombatBlockingRules.BrawlerRoles(view, view.Defenders.Items, true, new GeometryReply { Proposals = EquatableArray.From(blockers), Standable = geometry.Standable }, relieved);
        var next = 0;
        foreach (var defender in view.Defenders.Items)
        {
            if (!SquadDefenseRules.SquadDefenderEligible(defender) || !defender.RangedEquipped || next >= support.Count)
            {
                continue;
            }

            var cell = support[next++];
            roles.Add(new CombatRole { Pawn = defender.Id, Cell = cell, Target = target, Ranged = true });
        }

        return CombatDecideRules.SortRoles(roles);
    }

    /// <summary>
    /// Admits the choke only after its healthy, plan-owned blockers and supporting shooters stand in position.
    /// One shooter opens a closed door by walking onto it, then returns to its assigned cell on the next read.
    /// </summary>
    internal static void MeleeDoorTurn(CombatView view, CombatMemory memory)
    {
        var door = memory.ChokeDoor;
        if (door is null)
        {
            return;
        }

        var states = view.DoorStates.Items;
        var index = IndexOfDoor(states, door.Cell);
        if (index < 0 || !CombatDoorExists(view, door.Cell))
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
            if (pawn is null || defender is null || !SquadDefenseRules.SquadDefenderEligible(defender) || !view.Orderable.Items.Contains(role.Pawn) || role.Retreat || role.Cell is null)
            {
                ready = false;
                continue;
            }

            if (role.Duty == CombatDuty.Blocker)
            {
                blockers++;
            }

            if (role.Pawn != door.Opener && pawn.Cell != role.Cell)
            {
                ready = false;
            }
        }

        ready = ready && blockers > 0;
        var open = observed.Open;
        if (observed.Forbidden)
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
