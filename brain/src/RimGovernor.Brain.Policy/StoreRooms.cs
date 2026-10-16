namespace RimGovernor.Brain.Policy;

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
}
