namespace RimGovernor.Brain.Policy;

// Bedroom migration. A bedroom a successor replaces (layout_succession.go) is retiring: its pawns move one at
// a time into the other bedrooms. The move is not a bedroom deficit: the unhoused count, BedroomsOwed and the
// bed research request ignore it.

public static partial class PolicyFunctions
{
    /// <summary>The bedrooms whose pawns migrate: those a successor replaces.</summary>
    internal static List<PlannedRoom> RetiringBedrooms(LayoutPlan plan)
    {
        var gone = plan.Superseded();
        var output = new List<PlannedRoom>();
        foreach (var r in plan.Rooms)
        {
            if ((r.Role == PlannedRole.Bedroom || r.Role == PlannedRole.Suite) && gone.Contains(r.Id))
            {
                output.Add(r);
            }
        }
        return output;
    }

    /// <summary>The interiors of the retiring bedrooms.</summary>
    internal static HashSet<Rectangle> RetiringRooms(LayoutPlan plan)
    {
        var output = new HashSet<Rectangle>();
        foreach (var r in RetiringBedrooms(plan))
        {
            output.Add(r.Region.Bounds());
        }
        return output;
    }

    /// <summary>The beds standing in the retiring bedrooms.</summary>
    internal static HashSet<string> RetiringBeds(LayoutPlan plan, RoomObservation rooms) =>
        new(RetiringBedRoles(plan, rooms).Keys, StringComparer.Ordinal);

    /// <summary>The retiring beds with the planned role of their room: a pawn leaving a suite moves to a suite, one leaving a bedroom to a bedroom.</summary>
    internal static Dictionary<string, PlannedRole> RetiringBedRoles(LayoutPlan plan, RoomObservation rooms)
    {
        var output = new Dictionary<string, PlannedRole>(StringComparer.Ordinal);
        foreach (var r in RetiringBedrooms(plan))
        {
            // Census: the room's beds are the retiring beds.
            var (room, ok) = CensusRoomIn(r, rooms);
            if (ok)
            {
                foreach (var b in room!.Beds)
                {
                    output[b] = r.Role;
                }
            }
        }
        return output;
    }

    /// <summary>
    /// The bedrooms a successor replaces that a pawn still owns a bed in: they stay in the plan until the pawn
    /// has moved. With the census or the sleeping read unknown every replaced bedroom counts, so none leaves unseen.
    /// </summary>
    public static HashSet<int> SleptBedrooms(LayoutPlan plan, Fact<RoomObservation> rooms, Fact<SleepingObservation> sleeping)
    {
        var rk = rooms.TryGetValue(out var census);
        var sk = sleeping.TryGetValue(out var obs);
        var owned = new HashSet<string>(StringComparer.Ordinal);
        if (sk)
        {
            foreach (var b in obs!.Beds)
            {
                if (b.Owners.Count > 0)
                {
                    owned.Add(b.Id);
                }
            }
        }
        var gone = plan.Superseded();
        var output = new HashSet<int>();
        foreach (var r in plan.Rooms)
        {
            if ((r.Role != PlannedRole.Bedroom && r.Role != PlannedRole.Suite) || !gone.Contains(r.Id))
            {
                continue;
            }
            var slept = !rk || !sk;
            if (rk && sk)
            {
                var (room, ok) = CensusRoomIn(r, census!);
                if (ok)
                {
                    foreach (var b in room!.Beds)
                    {
                        slept = slept || owned.Contains(b);
                    }
                }
            }
            if (slept)
            {
                output.Add(r.Id);
            }
        }
        return output;
    }

    /// <summary>Whether b is an unowned colonist bed: humanlike, not medical, not for prisoners or slaves.</summary>
    internal static bool VacantColonistBed(SleepingBed b)
    {
        var hk = b.Humanlike.TryGetValue(out var human);
        var mk = b.Medical.TryGetValue(out var medical);
        var pk = b.Prisoners.TryGetValue(out var prisoner);
        return b.Owners.Count == 0 && hk && human && mk && !medical && pk && !prisoner && !b.Slaves;
    }

    /// <summary>
    /// The next migration step: move a pawn owning a bed in a retiring room to a vacant bed in a room that
    /// stays; with none vacant, reconcile the first standing empty such room, else the first unbuilt one
    /// (BedroomStepKind.Reconcile). None when no single pawn sleeps in a retiring room (a couple keeps its
    /// double bed). One pawn per step.
    /// </summary>
    public static BedroomStep NextMigrateStep(LayoutPlan plan, RoomObservation rooms, SleepingObservation sleeping)
    {
        var old = RetiringBedRoles(plan, rooms);
        if (old.Count == 0)
        {
            return new BedroomStep();
        }
        var people = sleeping.People.OrderBy(p => p.Id).ToList();
        var couples = SleepingCouples(sleeping.People);
        var movers = new List<SleepingPerson>();
        foreach (var p in people)
        {
            if (couples.ContainsKey(p.Id))
            {
                continue;
            }
            if (p.OwnedBed.TryGetValue(out var bed) && old.ContainsKey(bed))
            {
                movers.Add(p);
            }
        }
        if (movers.Count == 0)
        {
            return new BedroomStep();
        }
        var beds = new Dictionary<string, SleepingBed>(StringComparer.Ordinal);
        foreach (var b in sleeping.Beds)
        {
            beds[b.Id] = b;
        }
        // A pawn leaving a suite moves to a suite and one leaving a bedroom to a bedroom, so the targets are kept by the planned role.
        var empty = new Dictionary<PlannedRole, List<PlannedRoom>>();
        var unbuilt = new Dictionary<PlannedRole, List<PlannedRoom>>();
        var vacant = new Dictionary<PlannedRole, List<string>>();
        var retiring = RetiringRooms(plan);
        foreach (var r in plan.Rooms)
        {
            if ((r.Role != PlannedRole.Bedroom && r.Role != PlannedRole.Suite) || retiring.Contains(r.Region.Bounds()))
            {
                continue;
            }
            // Census: the room's beds say vacant, empty or unbuilt.
            var (room, ok) = CensusRoomIn(r, rooms);
            if (!ok)
            {
                Append(unbuilt, r.Role, r);
                continue;
            }
            if (room!.Beds.Count == 0)
            {
                Append(empty, r.Role, r);
            }
            foreach (var id in room.Beds)
            {
                if (beds.TryGetValue(id, out var b) && VacantColonistBed(b))
                {
                    Append(vacant, r.Role, id);
                }
            }
        }
        foreach (var ids in vacant.Values)
        {
            ids.Sort(StringComparer.Ordinal);
        }
        foreach (var p in movers)
        {
            p.OwnedBed.TryGetValue(out var previous);
            previous ??= "";
            if (vacant.TryGetValue(old[previous], out var ids))
            {
                foreach (var id in ids)
                {
                    if (beds[id].AccessibleTo.Contains(p.Id))
                    {
                        return new BedroomStep { Kind = BedroomStepKind.Move, Pawn = p.Id, Bed = id, PreviousBed = previous };
                    }
                }
            }
        }
        foreach (var p in movers)
        {
            p.OwnedBed.TryGetValue(out var previous);
            var role = old[previous ?? ""];
            var candidates = new List<PlannedRoom>();
            if (empty.TryGetValue(role, out var e))
            {
                candidates.AddRange(e);
            }
            if (unbuilt.TryGetValue(role, out var u))
            {
                candidates.AddRange(u);
            }
            if (candidates.Count > 0)
            {
                return new BedroomStep { Kind = BedroomStepKind.Reconcile, Room = candidates[0] };
            }
        }
        return new BedroomStep();

        static void Append<T>(Dictionary<PlannedRole, List<T>> map, PlannedRole role, T value)
        {
            if (!map.TryGetValue(role, out var list))
            {
                list = [];
                map[role] = list;
            }
            list.Add(value);
        }
    }
}
