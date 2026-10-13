namespace RimGovernor.Brain.Policy;

// Go: wanted_floor.go.

/// <summary>
/// The flooring review's decision for one PlannedRoom, for the reconciler: the wanted floor per interior cell and
/// whether an existing floor stands in for it. A null RoomFloors wants no floor anywhere.
/// </summary>
public delegate (Func<Cell, string> Wanted, Func<string, string, bool> Kept) RoomFloors(PlannedRoom room);

public static partial class PolicyFunctions
{
    /// <summary>
    /// The flooring review's decision read per cell of a PlannedRoom, the reconciler's WantedFloor: the floor def each
    /// interior cell wants, "" for none. The floor choice stays flooring's (ChooseFloor, the one the review's selector
    /// lays): the throne room takes the floor its title's tags name, any other room the tier its role requires, scored
    /// on the same economics. A room whose role needs no floor, or for which no floor is available, affordable or
    /// known yet, wants none; the whole room is priced as one batch, since the MaxCellsPerPlan batching only paces the
    /// laying of non-planned ground.
    /// </summary>
    public static Func<Cell, string> WantedFloors(PlannedRoom room, IReadOnlyList<string> tags, FlooringFacts facts, FlooringPolicy p)
    {
        static string None(Cell _) => "";
        var (d, ok) = RoomFloorDeficit(room, tags);
        var cells = d.Cells;
        if (room.Role == PlannedRole.ContainmentCell)
        {
            // The containment cell takes the catalog's containment floor when the stock pays for tiles not already
            // laid or ordered, else no floor.
            if (!ContainmentRoomFloorLaid(facts.ContainmentFloor, facts, room))
            {
                return None;
            }
            return _ => facts.ContainmentFloor.Def;
        }
        if (!ok || cells.Count == 0 || !p.IsValid)
        {
            return None;
        }
        var (name, _, _) = ChooseFloor(d, facts, cells.Count, p);
        if (name.Length == 0)
        {
            return None;
        }
        var inside = new HashSet<Cell>(cells);
        return c => inside.Contains(c) ? name : "";
    }

    // The floor requirement a PlannedRoom's whole interior carries: the tier its role asks for, the throne room's title tags.
    private static (FloorDeficit Deficit, bool Ok) RoomFloorDeficit(PlannedRoom room, IReadOnlyList<string> tags)
    {
        var role = ModuleRoomRoles[room.Role];
        var roleTier = RoleFloorTier(role);
        var tier = roleTier ?? default;
        var ok = roleTier is not null;
        if (tags.Count > 0 && role == RoomRole.ThroneRoom)
        {
            (tier, ok) = (FloorTier.Throne, true);
        }
        var d = new FloorDeficit { Tier = tier, Role = role, Cells = RectCells(room.Region.Bounds()) };
        if (tier == FloorTier.Throne)
        {
            d = d with { Tags = EquatableArray.From(tags) };
        }
        return (d, ok);
    }

    /// <summary>
    /// Whether the constructed floor "have" stands in for the wanted floor "want" of a PlannedRoom: any floor that
    /// meets the room's tier does, so a different adequate floor is never torn up. The throne room's title tags are
    /// the exception: the floor must carry one. A floor the mirror does not describe is kept, never torn up on a
    /// missing fact.
    /// </summary>
    public static Func<string, string, bool> FloorKept(PlannedRoom room, IReadOnlyList<string> tags, FlooringFacts facts, FlooringPolicy p)
    {
        var (d, ok) = RoomFloorDeficit(room, tags);
        return (have, want) =>
        {
            if (have == want)
            {
                return true;
            }
            if (room.Role == PlannedRole.ContainmentCell)
            {
                // Only the containment floor stands in for it (replacing a plain floor is what the strength was predicted on).
                return false;
            }
            if (!ok || !facts.Definitions.TryGetValue(have, out var def) || !p.IsValid)
            {
                return true;
            }
            if (d.Tier == FloorTier.Throne)
            {
                return HasAnyTag(def.Tags, d.Tags);
            }
            var weights = d.Tier == FloorTier.Living ? p.Living : p.Clean;
            var (_, meets) = FloorScore(d.Tier, def, weights);
            return meets;
        };
    }

    /// <summary>The flooring review's WantedFloors and FloorKept for every room; tags names a room's title tags (the throne room's), null for none.</summary>
    public static RoomFloors FlooringRoomFloors(Func<PlannedRoom, IReadOnlyList<string>>? tags, FlooringFacts facts, FlooringPolicy p) =>
        r =>
        {
            IReadOnlyList<string> t = tags is null ? [] : tags(r);
            return (WantedFloors(r, t, facts, p), FloorKept(r, t, facts, p));
        };
}
