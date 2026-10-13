namespace RimGovernor.Brain.Policy;

// Royal bedroom requirements: a titled owner's solo bedroom must hold the title's BedroomThings, each met by
// Count things of any one of its definitions. The bed entry is the bed replacement's (TitleBed); every other
// entry is placed here, one piece at a time, at its bedroom template slot when the template plans that
// definition and the slot is free, or else on free floor. Minimum impressiveness is the room target's
// "title" reason and flooring is MaintainFlooring's living tier; the minimum area is not a lever (a room is
// not enlarged).

public static partial class PolicyFunctions
{
    /// <summary>The North footprints of the non-bed royal bedroom things the closer places.</summary>
    internal static readonly IReadOnlyDictionary<Resource, Cell> TitleFurnitureSizes = new Dictionary<Resource, Cell>
    {
        [new Resource("EndTable")] = new Cell(1, 1),
        [new Resource("Dresser")] = new Cell(2, 1),
    };

    /// <summary>The first unmet royal bedroom thing, false when none.</summary>
    public static (RoomUpgrade Upgrade, bool Ok) NextTitleFurniture(
        SleepingObservation obs, IReadOnlyList<FurnitureRoom> rooms, Func<string, bool> available)
    {
        var titles = new Dictionary<PawnId, RoyalTitle?>();
        foreach (var p in obs.People)
        {
            titles[p.Id] = p.Title;
        }
        var furniture = new Dictionary<string, FurnitureRoom>(StringComparer.Ordinal);
        foreach (var r in rooms)
        {
            furniture[r.Id] = r;
        }
        foreach (var s in SoloBedrooms(obs))
        {
            var title = titles.GetValueOrDefault(s.Owner);
            var ok = furniture.TryGetValue(s.Room, out var room);
            if (title is null || !ok)
            {
                continue;
            }
            foreach (var thing in title.BedroomThings)
            {
                if (IsReplacementBed(thing.AnyOf))
                {
                    continue;
                }
                var have = 0;
                foreach (var p in room!.Pieces)
                {
                    foreach (var d in thing.AnyOf)
                    {
                        if (p.Def == d.Value)
                        {
                            have++;
                        }
                    }
                }
                if (have >= thing.Count)
                {
                    continue;
                }
                foreach (var d in thing.AnyOf)
                {
                    var (u, found) = TitlePiece(s.Room, room, d, available);
                    if (found)
                    {
                        return (u, true);
                    }
                }
            }
        }
        return (new RoomUpgrade(), false);
    }

    internal static (RoomUpgrade Upgrade, bool Ok) TitlePiece(string id, FurnitureRoom room, Resource def, Func<string, bool> available)
    {
        if (!TitleFurnitureSizes.TryGetValue(def, out var size) || !available(def.Value))
        {
            return (new RoomUpgrade(), false);
        }
        var slot = "title-" + def.Value;
        var (plan, ok) = PlanRoomInterior(room.Planning(), new InteriorPieceDef());
        if (ok)
        {
            foreach (var p in plan.Pieces)
            {
                if (p.Def == def.Value && !RoomPieceOverlaps(room.Pieces, p.Rect))
                {
                    return (new RoomUpgrade { Room = id, Slot = slot, Def = p.Def, Anchor = p.Anchor(), Rot = p.Rot }, true);
                }
            }
        }
        var (cell, rot, found) = FreeSpot(room, size);
        if (found)
        {
            return (new RoomUpgrade { Room = id, Slot = slot, Def = def.Value, Anchor = cell, Rot = rot }, true);
        }
        return (new RoomUpgrade(), false);
    }
}
