namespace RimGovernor.Brain.Policy;

/// <summary>
/// A desired vanilla latch setting. Passage is separate: a closed held door still needs a pawn to open it. To is
/// the cooler room's adjacent floor cell, used only for emergency heat relief.
/// </summary>
public sealed record DoorChange
{
    public RoomDoor Door { get; init; } = new();

    public bool HoldOpen { get; init; }

    public bool Heat { get; init; }

    public bool Passage { get; init; }

    public Cell To { get; init; }

    public EquatableArray<PawnId> Occupants { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// Owns routine door settings. Only measured internal traffic links are held open; emergency heat relief may
    /// use another safe internal link. Physical barriers, custody, perishables and fire separation prevail.
    /// Combat owns its doors while its Incident is active.
    /// </summary>
    public static IReadOnlyList<DoorChange> DoorChanges(Fact<RoomObservation> fact, Fact<RoutesObservation> traffic, RoundsPolicy p)
    {
        if (!fact.TryGetValue(out var observed))
        {
            return [];
        }
        var travelled = new HashSet<Cell>();
        if (traffic.TryGetValue(out var routes))
        {
            foreach (var row in routes.Traffic)
            {
                if (row.Layer == TrafficLayer.Colonist && row.Samples > 0)
                {
                    travelled.Add(row.Cell);
                }
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
                var pk = door.PlayerOwned.TryGetValue(out var owned);
                var hk = door.HoldOpen.TryGetValue(out var held);
                var ok = door.Open.TryGetValue(out var open);
                var fk = door.Forbidden.TryGetValue(out var forbidden);
                if (!pk || !owned || !hk || !ok || !fk)
                {
                    continue;
                }
                var change = new DoorChange { Door = door };
                var far = DoorDestination(rooms, door);
                var blockedKnown = door.BlockedOpen.TryGetValue(out _);
                if (far is not null && blockedKnown && !forbidden && SafeDoorRoom(room) && SafeDoorRoom(far))
                {
                    var ht = room.Temperature.TryGetValue(out var here);
                    var tt = far.Temperature.TryGetValue(out var there);
                    if (ht && tt)
                    {
                        // Use the existing colony heat band, retaining an active opening until recovery
                        // rather than oscillating at the entry threshold.
                        var threshold = held ? p.HotExit : p.HotEnter;
                        var (hot, cool) = (room, far);
                        var (from, to) = (here, there);
                        if (there > here)
                        {
                            (hot, cool, from, to) = (far, room, there, here);
                        }
                        hot.Role.TryGetValue(out var hotRole);
                        if (from > threshold && to < from && to >= p.ColdExit && !Positive(cool.TemperatureControl)
                            && !(hotRole == RoomRole.Storeroom && Positive(hot.TemperatureControl)) && (hot.Pawns.Count > 0 || hot.Beds.Count > 0))
                        {
                            change = change with
                            {
                                HoldOpen = true,
                                Heat = true,
                                Passage = !open,
                                Occupants = hot.Pawns,
                                To = hot.Id == room.Id ? door.Outside : new Cell(2 * door.Cell.X - door.Outside.X, 2 * door.Cell.Z - door.Outside.Z),
                            };
                        }
                        else if (travelled.Contains(door.Cell) && LogisticsRoom(room) && LogisticsRoom(far)
                            && !Positive(room.TemperatureControl) && !Positive(far.TemperatureControl)
                            && here >= p.ColdExit && here <= p.HotExit && there >= p.ColdExit && there <= p.HotExit)
                        {
                            change = change with { HoldOpen = true, Passage = !open };
                        }
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

    private static Room? DoorDestination(IReadOnlyList<Room> rooms, RoomDoor door)
    {
        if (!door.Outdoors.TryGetValue(out var outside) || outside)
        {
            return null;
        }
        foreach (var room in rooms)
        {
            if (room.Cells.Contains(door.Outside))
            {
                return room;
            }
        }
        return null;
    }

    private static bool SafeDoorRoom(Room room)
    {
        var rk = room.Role.TryGetValue(out var role);
        var ek = room.Enclosed.TryGetValue(out var enclosed);
        var bk = room.Burning.TryGetValue(out var burning);
        var pk = room.PerishableContents.TryGetValue(out var perishable);
        var tk = room.TemperatureControl.TryGetValue(out _);
        var secure = true;
        foreach (var door in room.Doors)
        {
            var known = door.Outdoors.TryGetValue(out var outdoor);
            if (!known || (outdoor && (!DoorFactFalse(door.Open) || !DoorFactFalse(door.HoldOpen))))
            {
                secure = false;
            }
        }
        return secure && rk && ek && enclosed && bk && !burning && pk && !perishable && tk
            && role != RoomRole.PrisonCell && role != RoomRole.PrisonBarracks && role != RoomRole.ContainmentCell
            && role != RoomRole.IsolationRoom && role != RoomRole.Tomb;
    }

    private static bool LogisticsRoom(Room room)
    {
        room.Role.TryGetValue(out var role);
        return role is RoomRole.Workshop or RoomRole.Storeroom or RoomRole.Laboratory or RoomRole.Room or RoomRole.None;
    }
}
