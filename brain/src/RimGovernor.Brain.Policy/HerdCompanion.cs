namespace RimGovernor.Brain.Policy;

// Companion beds. A bonded animal's master is its first bond partner by id on the roster (HerdMasterChoice).
// Each master with a solo bedroom gets one animal sleeping spot in it per animal they master, on free floor
// that keeps the room walkable; the bedroom closer places one per review, through the same room upgrade path as
// the title furniture. A master who shares a room, or has none, gets no spot: the animal sleeps where animals
// sleep.

public static partial class PolicyFunctions
{
    /// <summary>Go <c>CompanionMaster</c>: the colonist who masters a bonded animal: the first of its bond partners by id among people. False for an unbonded animal.</summary>
    public static (PawnId Master, bool Ok) CompanionMaster(UpkeepAnimal a, IReadOnlyList<SleepingPerson> people)
    {
        var onRoster = new HashSet<PawnId>(people.Select(p => p.Id));
        PawnId first = default;
        foreach (var id in a.BondedPawns)
        {
            var pid = new PawnId(id);
            if (onRoster.Contains(pid) && (first.IsEmpty || pid < first))
            {
                first = pid;
            }
        }
        return (first, !first.IsEmpty);
    }

    /// <summary>
    /// Go <c>NextCompanionBed</c>: returns the first companion bed due, false when none. rooms are the furniture
    /// rooms (FurnitureRooms); spot is the animal sleeping spot's shape and available reports that it can be
    /// built now.
    /// </summary>
    public static (RoomUpgrade Upgrade, bool Ok) NextCompanionBed(
        SleepingObservation obs,
        IReadOnlyList<UpkeepAnimal> animals,
        IReadOnlyList<FurnitureRoom> rooms,
        RoomFurniture beds,
        InteriorPieceDef spot,
        bool available
    )
    {
        if (!available || spot.Def.Length == 0)
        {
            return (new RoomUpgrade(), false);
        }
        var owed = new Dictionary<PawnId, int>();
        foreach (var a in animals)
        {
            var release = a.Release.ValueOrDefault;
            var slaughter = a.Slaughter.ValueOrDefault;
            var (master, ok) = CompanionMaster(a, obs.People);
            if (ok && !release && !slaughter)
            {
                owed[master] = owed.GetValueOrDefault(master) + 1;
            }
        }
        var furniture = new Dictionary<string, FurnitureRoom>(StringComparer.Ordinal);
        foreach (var r in rooms)
        {
            furniture[r.Id] = r;
        }
        foreach (var s in SoloBedrooms(obs).OrderBy(s => s.Room, StringComparer.Ordinal))
        {
            if (!furniture.TryGetValue(s.Room, out var room) || owed.GetValueOrDefault(s.Owner) == 0)
            {
                continue;
            }
            var have = room.Pieces.Count(p => p.Def == beds.AnimalSpot || p.Def == beds.AnimalBed);
            if (have >= owed[s.Owner])
            {
                continue;
            }
            var (cell, rot, found) = FreeSpot(room, spot.Size);
            if (found)
            {
                return (new RoomUpgrade { Room = s.Room, Slot = "companion-bed-" + (have + 1), Def = spot.Def, Anchor = cell, Rot = rot }, true);
            }
        }
        return (new RoomUpgrade(), false);
    }
}
