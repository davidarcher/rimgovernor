using PolicyRoomDoor = RimGovernor.Brain.Policy.RoomDoor;
using RoomDoorWire = RimGovernor.Contracts.Wire.Observations.RoomDoor;

namespace RimGovernor.Brain.Bridge.Contracts;

// Go room_door.go.
public static partial class BridgeFunctions
{
    /// <summary>Go's <c>RoomDoorFacts</c>: keeps absent native fields unknown for both routine and combat.</summary>
    public static PolicyRoomDoor RoomDoorFacts(RoomDoorWire door) =>
        new()
        {
            Id = door.Id ?? "",
            Cell = ToDomainCell(door.Cell),
            Outside = ToDomainCell(door.Outside),
            Outdoors = OptionalFact(door.Outdoors is not null, door.Outdoors.GetValueOrDefault()),
            PlayerOwned = OptionalFact(door.PlayerOwned is not null, door.PlayerOwned.GetValueOrDefault()),
            Open = OptionalFact(door.Open is not null, door.Open.GetValueOrDefault()),
            HoldOpen = OptionalFact(door.HoldOpen is not null, door.HoldOpen.GetValueOrDefault()),
            BlockedOpen = OptionalFact(door.BlockedOpen is not null, door.BlockedOpen.GetValueOrDefault()),
            Forbidden = OptionalFact(door.Forbidden is not null, door.Forbidden.GetValueOrDefault()),
        };
}
