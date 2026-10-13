namespace RimGovernor.Brain.Observation;

public static partial class ObservationPackage
{
    internal static Fact<RoomObservation> TemperatureRooms(O.RoomsSnapshot rooms, IReadOnlyDictionary<string, IReadOnlyList<Cell>> cells, Fact<SleepingObservation> sleeping)
    {
        var result = new RoomObservation();
        if (sleeping.TryGetValue(out var known))
        {
            var eligible = new List<string>();
            var complete = true;
            foreach (var bed in known.Beds)
            {
                var hk = bed.Humanlike.TryGetValue(out var human);
                var mk = bed.Medical.TryGetValue(out var medical);
                var pk = bed.Prisoners.TryGetValue(out var prisoners);
                if (!hk || !mk || !pk)
                {
                    complete = false;
                    break;
                }
                if (human && !medical && !prisoners)
                {
                    eligible.Add(bed.Id);
                }
            }
            if (complete)
            {
                result = result with { EligibleBeds = Known(EquatableArray.From(eligible)) };
            }
        }
        var output = new List<Room>(rooms.Rooms.Count);
        foreach (var room in rooms.Rooms)
        {
            var pawns = new List<PawnId>(room.Pawns.Count);
            foreach (var pawn in room.Pawns)
            {
                pawns.Add(new PawnId((pawn.Id ?? "")));
            }
            var beds = new List<string>(room.Beds.Count);
            foreach (var bed in room.Beds)
            {
                beds.Add((bed.Id ?? ""));
            }
            var contents = new List<Amount>(room.Contents.Count);
            foreach (var q in room.Contents)
            {
                contents.Add(new Amount(new Resource((q.DefName ?? "")), q.Units.GetValueOrDefault()));
            }
            var doors = new List<Policy.RoomDoor>(room.Doors.Count);
            foreach (var door in room.Doors)
            {
                doors.Add(RoomDoorFacts(door));
            }
            output.Add(new Room
            {
                Id = room.Id ?? "",
                Role = RoomRoleOf(room),
                Temperature = Optional(room.TemperatureC is not null, room.TemperatureC.GetValueOrDefault()),
                Enclosed = Known(room.ProperRoom.GetValueOrDefault() && !room.Doorway.GetValueOrDefault() && !room.Outdoors.GetValueOrDefault() && !room.PsychologicallyOutdoors.GetValueOrDefault() && !room.TouchesMapEdge.GetValueOrDefault() && room.OpenRoofCount.GetValueOrDefault() == 0),
                Burning = Optional(room.Burning is not null, room.Burning.GetValueOrDefault()),
                TemperatureControl = Optional(room.TemperatureControl is not null, room.TemperatureControl.GetValueOrDefault()),
                PerishableContents = Optional(room.PerishableContents is not null, room.PerishableContents.GetValueOrDefault()),
                Pawns = EquatableArray.From(pawns),
                Beds = EquatableArray.From(beds),
                Contents = Known(EquatableArray.From(contents)),
                Cleanliness = RoomStat(room, "Cleanliness"),
                Cells = EquatableArray.From(cells.TryGetValue((room.Id ?? ""), out var found) ? found : []),
                Roofed = room.OpenRoofCount is not null ? Known(room.OpenRoofCount.GetValueOrDefault() == 0) : Unknown<bool>(),
                Doors = EquatableArray.From(doors),
            });
        }
        return Known(PolicyFunctions.MarkEnemyDoors(result with { Rooms = EquatableArray.From(output) }, []));
    }

    /// <summary>Reads one named native room stat; a missing or unavailable stat is unknown, never zero.</summary>
    internal static Fact<double> RoomStat(O.RoomState room, string name)
    {
        foreach (var stat in room.Stats)
        {
            if ((stat.DefName ?? "") != name)
            {
                continue;
            }
            if (stat.Unavailable is not null || stat.Value is null)
            {
                return Unknown<double>();
            }
            return Known(stat.Value.GetValueOrDefault());
        }
        return Unknown<double>();
    }

    // The native census names Room.Role by RoomRoleDef defName; a missing role is an unknown fact, never a generic room.
    private static Fact<RoomRole> RoomRoleOf(O.RoomState room) =>
        room.Role is not null ? Known(WireParse<RoomRole>(room.Role)) : Unknown<RoomRole>();
}
