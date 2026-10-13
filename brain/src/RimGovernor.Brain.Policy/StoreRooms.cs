namespace RimGovernor.Brain.Policy;

// A further warehouse: when every warehouse zone is at or over StockpileFurtherRoomFill, the Storage department
// asks layout for one more storage room (RoomDemand.Storage). It never plans the room; once the room stands its
// warehouse zone is created and the demand clears until that zone fills too.
public sealed partial record StoreView
{
    /// <summary>The plan's storage rooms in plan order, the first being the core room.</summary>
    internal List<PlannedRoom> PlannedStorageRooms()
    {
        if (Layout is null)
        {
            return [];
        }
        return [.. Layout.Rooms.Where(p => p.Role == PlannedRole.Storage)];
    }

    /// <summary>How many storage rooms the plan holds, a room and its successors counting as one.</summary>
    internal int StorageRoomChains() => Layout is null ? 0 : Layout.RoomChains(PlannedRole.Storage);
}
