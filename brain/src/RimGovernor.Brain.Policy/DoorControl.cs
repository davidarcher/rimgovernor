namespace RimGovernor.Brain.Policy;

/// <summary>
/// A desired vanilla latch setting. Passage is separate: a closed held door still needs a pawn to open it.
/// </summary>
public sealed record DoorChange
{
    public required RoomDoor Door { get; init; }

    public bool HoldOpen { get; init; }

    public bool Heat { get; init; }

    public bool Passage { get; init; }
}

public static class DoorControlRules
{
    /// <summary>
    /// Owns routine door settings. Only measured internal traffic links are held open; emergency heat relief may
    /// use another safe internal link. Physical barriers, custody, perishables and fire separation prevail.
    /// Combat owns its doors while its Incident is active.
    /// </summary>
    public static IReadOnlyList<DoorChange> DoorChanges(RoomObservation fact, RoutesObservation traffic, RoundsPolicy p)
    {
        var observed = fact;
        var travelled = new HashSet<Cell>();
        foreach (var row in traffic.Traffic)
        {
            if (row.Layer == TrafficLayer.Colonist && row.Samples > 0)
            {
                travelled.Add(row.Cell);
            }
        }
        var rooms = observed.Rooms.OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
        var seen = new HashSet<Cell>();
        var changes = new List<DoorChange>();
        foreach (var room in rooms)
        {
            var doors = room.Doors.OrderBy(d => d.Cell.X).ThenBy(d => d.Cell.Z).ToList();
            foreach (var door in doors)
            {
                if (!seen.Add(door.Cell))
                {
                    continue;
                }
                var held = door.HoldOpen;
                var open = door.Open;
                if (!door.PlayerOwned)
                {
                    continue;
                }
                var change = new DoorChange { Door = door };
                var far = DoorDestination(observed, rooms, door);
                if (far is not null && !door.Forbidden && SafeDoorRoom(room) && SafeDoorRoom(far))
                {
                    var here = room.TemperatureC;
                    var there = far.TemperatureC;
                    // Use the existing colony heat band, retaining an active opening until recovery
                    // rather than oscillating at the entry threshold.
                    var threshold = held ? p.HotExit : p.HotEnter;
                    var (hot, cool) = (room, far);
                    var (from, to) = (here, there);
                    if (there > here)
                    {
                        (hot, cool, from, to) = (far, room, there, here);
                    }
                    if (from > threshold && to < from && to >= p.ColdExit && !cool.TemperatureControl
                        && !(hot.Role == RoomRole.Storeroom && hot.TemperatureControl) && (hot.Pawns.Count > 0 || hot.Beds.Count > 0))
                    {
                        change = change with
                        {
                            HoldOpen = true,
                            Heat = true,
                            Passage = !open,
                        };
                    }
                    else if (travelled.Contains(door.Cell) && LogisticsRoom(room) && LogisticsRoom(far)
                        && !room.TemperatureControl && !far.TemperatureControl
                        && here >= p.ColdExit && here <= p.HotExit && there >= p.ColdExit && there <= p.HotExit)
                    {
                        change = change with { HoldOpen = true, Passage = !open };
                    }
                }
                if (held != change.HoldOpen || change.Passage)
                {
                    changes.Add(change);
                }
            }
        }
        return changes
            .OrderBy(c => c.HoldOpen)
            .ThenByDescending(c => c.Heat)
            .ThenBy(c => c.Door.Cell.X)
            .ThenBy(c => c.Door.Cell.Z)
            .ToList();
    }

    private static RoomState? DoorDestination(RoomObservation observed, IReadOnlyList<RoomState> rooms, RoomDoor door)
    {
        if (door.Outdoors)
        {
            return null;
        }
        foreach (var room in rooms)
        {
            if (observed.CellsOf(room).Contains(door.Outside))
            {
                return room;
            }
        }
        return null;
    }

    private static bool SafeDoorRoom(RoomState room)
    {
        var secure = room.Doors.All(door => !door.Outdoors || (!door.Open && !door.HoldOpen));
        return secure && room.Enclosed() && !room.Burning && !room.PerishableContents
            && room.Role != RoomRole.PrisonCell && room.Role != RoomRole.PrisonBarracks && room.Role != RoomRole.ContainmentCell
            && room.Role != RoomRole.Tomb;
    }

    private static bool LogisticsRoom(RoomState room)
    {
        var role = room.Role;
        return role is RoomRole.Workshop or RoomRole.Storeroom or RoomRole.Laboratory or RoomRole.Room or RoomRole.None;
    }
}
