using RimGovernor.Brain.Policy;

namespace RimGovernor.Brain.Domain.Tests;

internal static class RoomTestFacts
{
    internal static RoomObservation NoRooms { get; } = new() { Rooms = [], Cells = new Dictionary<string, IReadOnlyList<Cell>>(StringComparer.Ordinal) };
}
