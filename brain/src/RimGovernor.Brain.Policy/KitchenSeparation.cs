namespace RimGovernor.Brain.Policy;

public static partial class PolicyConstants
{
    /// <summary>The real butcher bench, built from stuff.</summary>
    public const string ButcherTableDefinition = "TableButcher";

    // The vanilla food-processing benches the separation rule reasons about. Butchery generates blood filth
    // (and corpse rot) wherever it runs, so a room holding one is inherently dirty; a room holding a cooking
    // bench is a workspace whose cleanliness the game reads for food poisoning.
    internal static readonly IReadOnlySet<Resource> CookingDefinitions = new HashSet<Resource> { new("Campfire"), new("FueledStove"), new("ElectricStove") };

    internal static readonly IReadOnlySet<Resource> ButcherDefinitions = new HashSet<Resource> { new(ButcherSpotDefinition), new(ButcherTableDefinition) };

    internal static readonly IReadOnlySet<Resource> SleepingDefinitions =
        new HashSet<Resource> { new("SleepingSpot"), new("DoubleSleepingSpot"), new("Bed"), new("DoubleBed"), new("RoyalBed") };
}

/// <summary>One room holding both a cooking bench and a butcher bench: butchery filth lands in the workspace the game reads for food poisoning.</summary>
public sealed record SeparationRoom(string Id, EquatableArray<Cell> Cells);

public static partial class PolicyFunctions
{
    private static bool RoomHolds(Room room, IReadOnlySet<Resource> set)
    {
        if (!room.Contents.TryGetValue(out var contents))
        {
            return false;
        }
        return contents.Any(amount => set.Contains(amount.Resource) && amount.Count > 0);
    }

    /// <summary>
    /// Reports whether room is one the controller keeps clean: an enclosed kitchen, hospital or laboratory, or
    /// any enclosed room holding a cooking bench (a "Room" role kitchen with a campfire counts). Rooms that are
    /// inherently dirty -- barns, and any room holding a butcher bench -- are never workspaces, whatever else
    /// they contain: their filth is the cost of the work done there, not a coverage failure.
    /// </summary>
    public static bool CleanWorkspace(Room room)
    {
        if (!room.Enclosed.TryGetValue(out var enclosed) || !enclosed || InherentlyDirty(room))
        {
            return false;
        }
        if (room.Role.TryGetValue(out var role) && role is RoomRole.Kitchen or RoomRole.Hospital or RoomRole.Laboratory)
        {
            return true;
        }
        return RoomHolds(room, CookingDefinitions);
    }

    /// <summary>Reports a room whose use produces filth as a matter of course (barn, butchery). Cleaning it is ordinary colonist work, never a controller intervention.</summary>
    public static bool InherentlyDirty(Room room)
    {
        if (room.Role.TryGetValue(out var role) && role == RoomRole.Barn)
        {
            return true;
        }
        return RoomHolds(room, ButcherDefinitions);
    }

    /// <summary>Lists rooms where cooking and butchery share a room, with their cells so placement can keep new facilities out. Unknown when the room census is unknown.</summary>
    public static Fact<IReadOnlyList<SeparationRoom>> KitchenSeparation(Fact<RoomObservation> rooms)
    {
        if (!rooms.TryGetValue(out var census))
        {
            return Unknown<IReadOnlyList<SeparationRoom>>();
        }
        var result = census.Rooms
            .Where(room => RoomHolds(room, CookingDefinitions) && RoomHolds(room, ButcherDefinitions))
            .Select(room => new SeparationRoom(room.Id, new EquatableArray<Cell>([.. room.Cells])))
            .OrderBy(r => r.Id, StringComparer.Ordinal)
            .ToList();
        return Known<IReadOnlyList<SeparationRoom>>(result);
    }

    /// <summary>
    /// Returns every cell of every room holding the benches in set (cooking rooms for a butcher placement,
    /// butcher rooms for a cooking placement; a butcher also avoids any room the game already scores a
    /// Kitchen), so a placement search never proposes a site that would co-locate the two. Unknown room facts
    /// protect nothing: the placement's own native preview still owns legality. Sleeping rooms (the Bedroom
    /// role, or any room holding a bed or sleeping spot, so the starter bedroom counts before native scores it)
    /// are protected for a butcher placement only: blood filth stays out of bedrooms.
    /// </summary>
    public static IReadOnlyList<Cell> SeparationProtectedCells(Fact<RoomObservation> rooms, bool butcherPlacement)
    {
        if (!rooms.TryGetValue(out var census))
        {
            return [];
        }
        var set = butcherPlacement ? CookingDefinitions : ButcherDefinitions;
        var cells = new List<Cell>();
        foreach (var room in census.Rooms)
        {
            var role = room.Role.ValueOrDefault;
            if ((butcherPlacement && (role == RoomRole.Bedroom || RoomHolds(room, SleepingDefinitions))) || RoomHolds(room, set) || (butcherPlacement && role == RoomRole.Kitchen))
            {
                cells.AddRange(room.Cells);
            }
        }
        return cells.SortedBy(CellLess);
    }
}
