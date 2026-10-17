using PolicyRoomDoor = RimGovernor.Brain.Policy.RoomDoor;
using RoomDoorWire = RimGovernor.Contracts.Wire.Observations.RoomDoor;

namespace RimGovernor.Brain.Bridge.Contracts;

public static class RoomDoorRules
{
    /// <summary>Keeps absent native fields unknown for both routine and combat.</summary>
    public static PolicyRoomDoor RoomDoorFacts(RoomDoorWire door) =>
        new()
        {
            Cell = FactHelpers.ToDomainCell(door.Cell),
            Outside = FactHelpers.ToDomainCell(door.Outside),
            Outdoors = FactHelpers.OptionalFact(door.Outdoors is not null, door.Outdoors.GetValueOrDefault()),
            PlayerOwned = FactHelpers.OptionalFact(door.PlayerOwned is not null, door.PlayerOwned.GetValueOrDefault()),
            Open = FactHelpers.OptionalFact(door.Open is not null, door.Open.GetValueOrDefault()),
            HoldOpen = FactHelpers.OptionalFact(door.HoldOpen is not null, door.HoldOpen.GetValueOrDefault()),
            BlockedOpen = FactHelpers.OptionalFact(door.BlockedOpen is not null, door.BlockedOpen.GetValueOrDefault()),
            Forbidden = FactHelpers.OptionalFact(door.Forbidden is not null, door.Forbidden.GetValueOrDefault()),
        };
}
