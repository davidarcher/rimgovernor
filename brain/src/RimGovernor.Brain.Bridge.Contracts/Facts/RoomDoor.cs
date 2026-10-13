using PolicyRoomDoor = RimGovernor.Brain.Policy.RoomDoor;
using RoomDoorWire = RimGovernor.Protocol.Observations.RoomDoor;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go room_door.go.
public static partial class BridgeFunctions
{
    /// <summary>Go's <c>RoomDoorFacts</c>: keeps absent native fields unknown for both routine and combat.</summary>
    public static PolicyRoomDoor RoomDoorFacts(RoomDoorWire door) =>
        new()
        {
            Id = door.Id,
            Cell = ToDomainCell(door.Cell),
            Outside = ToDomainCell(door.Outside),
            Outdoors = OptionalFact(door.HasOutdoors, door.Outdoors),
            PlayerOwned = OptionalFact(door.HasPlayerOwned, door.PlayerOwned),
            Open = OptionalFact(door.HasOpen, door.Open),
            HoldOpen = OptionalFact(door.HasHoldOpen, door.HoldOpen),
            BlockedOpen = OptionalFact(door.HasBlockedOpen, door.BlockedOpen),
            Forbidden = OptionalFact(door.HasForbidden, door.Forbidden),
        };
}
