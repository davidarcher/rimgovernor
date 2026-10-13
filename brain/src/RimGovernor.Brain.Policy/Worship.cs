namespace RimGovernor.Brain.Policy;

// Go: worship.go. The worship room. An ideoligion that requires buildings (Ideoligion.RequiredBuildings: the
// building precepts' ThingDefs and the held rituals' required buildings, all read from the game) is owed one
// worship room holding one of each. It is staged exactly like the child rooms (ChildRooms, LayoutChildRooms): the
// layout review grows a core room sized to the footprints, MaintainHousing shells it and places the buildings at the
// shared template's slots. Names come from the ideoligion and footprints from the native catalog, never constants
// here. The ideology read carries no room-quality requirement, so none is staged.

public static partial class PolicyFunctions
{
    /// <summary>The worship room the ideoligion owes; Ok is false when it requires no building.</summary>
    public static (ChildRoomNeed Need, bool Ok) WorshipRoomNeed(Ideoligion ideo)
    {
        var required = ideo.RequiredBuildings();
        if (required.Count == 0)
        {
            return (default!, false);
        }
        var need = new ChildRoomNeed
        {
            Role = RoomRole.WorshipRoom,
            Module = PlannedRole.Worship,
            Furniture = EquatableArray.From(required.Select(name => new ChildFurniture { Defs = EquatableArray.From([name]), Count = 1 }).ToList()),
        };
        return (need, true);
    }
}
