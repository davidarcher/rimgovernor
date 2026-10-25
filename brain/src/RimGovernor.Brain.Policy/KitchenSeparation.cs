namespace RimGovernor.Brain.Policy;

public static class KitchenSeparationRules
{
    /// <summary>The real butcher bench, built from stuff.</summary>
    public const string ButcherTableDefinition = "TableButcher";

    // The vanilla food-processing benches the separation rule reasons about. Butchery generates blood filth
    // (and corpse rot) wherever it runs, so a room holding one is inherently dirty; a room holding a cooking
    // bench is a workspace whose cleanliness the game reads for food poisoning.
    internal static readonly IReadOnlySet<Resource> CookingDefinitions = new HashSet<Resource> { new("Campfire"), new("FueledStove"), new("ElectricStove") };

    internal static readonly IReadOnlySet<Resource> ButcherDefinitions = new HashSet<Resource> { new(Rules.ButcherSpotDefinition), new(ButcherTableDefinition) };

    private static bool RoomHolds(RoomState room, IReadOnlySet<Resource> set) =>
        room.Contents.Any(q => set.Contains(new Resource(q.DefName)) && q.Units > 0);

    /// <summary>
    /// Reports whether room is one the controller keeps clean: an enclosed kitchen, hospital or laboratory, or
    /// any enclosed room holding a cooking bench (a "Room" role kitchen with a campfire counts). Rooms that are
    /// inherently dirty -- barns, and any room holding a butcher bench -- are never workspaces, whatever else
    /// they contain: their filth is the cost of the work done there, not a coverage failure.
    /// </summary>
    public static bool CleanWorkspace(RoomState room)
    {
        if (!room.Enclosed() || InherentlyDirty(room))
        {
            return false;
        }
        if (room.Role is RoomRole.Kitchen or RoomRole.Hospital or RoomRole.Laboratory)
        {
            return true;
        }
        return RoomHolds(room, CookingDefinitions);
    }

    /// <summary>Reports a room whose use produces filth as a matter of course (barn, butchery). Cleaning it is ordinary colonist work, never a controller intervention.</summary>
    public static bool InherentlyDirty(RoomState room)
    {
        if (room.Role == RoomRole.Barn)
        {
            return true;
        }
        return RoomHolds(room, ButcherDefinitions);
    }

}
