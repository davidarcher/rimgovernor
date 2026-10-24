using RimGovernor.Brain.Policy;
using Common = RimGovernor.Contracts.Wire.Common;
using Observations = RimGovernor.Contracts.Wire.Observations;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// The shared native room census rows the planner tests stand rooms with: a complete RoomState (every required
/// member) and a census of rows with the cells the frame cell grid would give them.
/// </summary>
internal static class RoomFixtures
{
    /// <summary>
    /// One census row. An unenclosed room touches the map edge; an unroofed one has one open roof cell, so it is
    /// not enclosed either.
    /// </summary>
    public static RoomState State(
        string id,
        RoomRole role = RoomRole.Room,
        bool enclosed = true,
        bool roofed = true,
        IEnumerable<string>? beds = null,
        double temperatureC = 21,
        IEnumerable<Observations.RoomDoor>? doors = null,
        IEnumerable<Observations.RoomContent>? contents = null,
        IEnumerable<Observations.RoomStat>? stats = null,
        uint cellCount = 0,
        int gridRoom = 0) => new()
    {
        Id = id,
        Role = role,
        ProperRoom = true,
        Doorway = false,
        Outdoors = false,
        PsychologicallyOutdoors = false,
        TouchesMapEdge = !enclosed,
        Fogged = false,
        TemperatureC = temperatureC,
        OpenRoofCount = roofed ? 0u : 1u,
        CellCount = cellCount,
        Extents = new() { Minimum = new(0, 0), Maximum = new(0, 0) },
        Center = new(0, 0),
        Stats = EquatableArray.From(stats ?? []),
        Pawns = [],
        Beds = EquatableArray.From((beds ?? []).Select(b => new Common.Ref { Id = b })),
        Contents = EquatableArray.From(contents ?? []),
        Doors = EquatableArray.From(doors ?? []),
        GridRoom = gridRoom,
        Burning = false,
        TemperatureControl = false,
        PerishableContents = false,
    };

    /// <summary>A census of the rows, each with its cells.</summary>
    public static RoomObservation Census(params (RoomState Room, IEnumerable<Cell> Cells)[] rooms) => new()
    {
        Rooms = EquatableArray.From(rooms.Select(r => r.Room)),
        Cells = rooms.ToDictionary(r => r.Room.Id, r => (IReadOnlyList<Cell>)[.. r.Cells], StringComparer.Ordinal),
    };

    /// <summary>A census of rows that hold no grid cells.</summary>
    public static RoomObservation Census(params RoomState[] rooms) => Census([.. rooms.Select(r => (r, Enumerable.Empty<Cell>()))]);
}
