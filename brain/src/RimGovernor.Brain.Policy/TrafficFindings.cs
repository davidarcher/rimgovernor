namespace RimGovernor.Brain.Policy;

// Go: traffic_findings.go. Traffic findings check the layout rules against where pawns really walk. They have no
// action of their own yet: the OTLP log ring carries them so a flagged room is visible before a planner acts on it.

/// <summary>Names what a finding flags.</summary>
public enum TrafficFindingKind
{
    [WireName("")]
    None,

    /// <summary>A room that should be a dead end (bedroom, barracks, kitchen, hospital, lab, prison) carrying real colonist or visitor through-traffic.</summary>
    [WireName("thoroughfare")]
    Thoroughfare,

    /// <summary>Colony animal traffic through a kitchen or hospital: tighten the allowed area or build the missing pen.</summary>
    [WireName("animal_in_clean_room")]
    AnimalInCleanRoom,
}

/// <summary>One flagged room with the busiest offending cell count.</summary>
public sealed record TrafficFinding
{
    public TrafficFindingKind Kind { get; init; }

    public string Room { get; init; } = "";

    public RoomRole Role { get; init; }

    public TrafficLayer Layer { get; init; }

    public uint Steps { get; init; }

    public override string ToString() => $"{Wire.Name(Kind)} room={Room} role={Wire.Name(Role)} layer={Wire.Name(Layer)} steps={Steps}";
}

public static partial class PolicyFunctions
{
    // Busiest-cell step counts a room must reach before it is flagged. A resident walking to their own bed stays
    // well under the colonist bound on the two-day half-life; visitors have no business in these rooms at all.
    private const int ThoroughfareColonistSteps = 150;

    private const int ThoroughfareVisitorSteps = 10;

    private const int AnimalCleanRoomSteps = 10;

    private static readonly HashSet<RoomRole> ThoroughfareRoles =
    [
        RoomRole.Bedroom,
        RoomRole.Barracks,
        RoomRole.Shelter,
        RoomRole.Kitchen,
        RoomRole.Hospital,
        RoomRole.Laboratory,
        RoomRole.PrisonCell,
        RoomRole.PrisonBarracks,
    ];

    /// <summary>Flags the census's rooms against the traffic layers, one finding per room, kind and layer, in room order.</summary>
    public static IReadOnlyList<TrafficFinding> TrafficFindings(FlooringObservation v)
    {
        var room = new Dictionary<Cell, int>();
        for (var i = 0; i < v.Rooms.Count; i++)
        {
            foreach (var c in v.Rooms[i].Cells)
            {
                room[c.Cell] = i;
            }
        }
        var busiest = new Dictionary<(int Room, TrafficLayer Layer), uint>();
        foreach (var t in v.Traffic)
        {
            if (!room.TryGetValue(t.Cell, out var i))
            {
                continue;
            }
            var k = (i, t.Layer);
            busiest[k] = Math.Max(busiest.GetValueOrDefault(k), t.Samples);
        }
        var output = new List<TrafficFinding>();
        foreach (var ((index, layer), steps) in busiest)
        {
            if (!v.Rooms[index].Role.TryGetValue(out var role))
            {
                continue;
            }
            var f = new TrafficFinding { Room = v.Rooms[index].Id, Role = role, Layer = layer, Steps = steps };
            if (ThoroughfareRoles.Contains(role) && ((layer == TrafficLayer.Colonist && steps >= ThoroughfareColonistSteps) || (layer == TrafficLayer.Visitor && steps >= ThoroughfareVisitorSteps)))
            {
                f = f with { Kind = TrafficFindingKind.Thoroughfare };
            }
            else if ((role == RoomRole.Kitchen || role == RoomRole.Hospital) && layer == TrafficLayer.Animal && steps >= AnimalCleanRoomSteps)
            {
                f = f with { Kind = TrafficFindingKind.AnimalInCleanRoom };
            }
            else
            {
                continue;
            }
            output.Add(f);
        }
        return
        [
            .. output
                .OrderBy(f => f.Room, StringComparer.Ordinal)
                .ThenBy(f => Wire.Name(f.Kind), StringComparer.Ordinal)
                .ThenBy(f => Wire.Name(f.Layer), StringComparer.Ordinal),
        ];
    }
}
