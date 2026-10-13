namespace RimGovernor.Brain.Policy;

public static partial class PolicyFunctions
{
    /// <summary>
    /// Builds GuardNativeRoles' input from what the colony observes: for each planned room standing enclosed
    /// on its region, the catalog stations among the buildings with a cell in the census room, each humanlike
    /// bed carrying the state and owners the sleeping census gives it.
    /// <para>
    /// A room is left out, never guessed, when it holds a bed the sleeping census does not name or whose
    /// medical or prisoner state is unknown, or a station whose role contribution turns on an ideo altar's
    /// memes (the census does not read them). The census carries no pawn age or parents, so a bed owner is
    /// read as an adult whose love cluster is the closure of the partner relations; a child owner can make a
    /// bedroom read as a mismatch.
    /// </para>
    /// </summary>
    public static List<RoomContents> ObservedRoomContents(
        StationCatalog catalog,
        LayoutPlan plan,
        RoomObservation rooms,
        IReadOnlyList<CurrentBuilding> buildings,
        SleepingObservation sleeping)
    {
        var beds = new Dictionary<string, SleepingBed>(StringComparer.Ordinal);
        foreach (var b in sleeping.Beds)
        {
            beds[b.Id] = b;
        }
        var clusters = LoveClusters(sleeping);
        var output = new List<RoomContents>();
        foreach (var planned in plan.Rooms)
        {
            var (census, standing) = CensusRoomIn(planned, rooms);
            if (!standing)
            {
                continue;
            }
            var inRoom = new HashSet<Cell>(census!.Cells);
            var pieces = new List<RoomPiece>();
            var known = true;
            foreach (var b in buildings)
            {
                var def = b.Building!.Definition;
                if (!catalog.Stations.TryGetValue(def, out var st) || !b.Cells.Any(c => inRoom.Contains(c)))
                {
                    continue;
                }
                if (st.Contributions.Any(c => c.When == RoleCondition.IdeoAltar))
                {
                    known = false;
                    break;
                }
                var piece = new RoomPiece { Def = def };
                if (HumanlikeBed(st))
                {
                    var ok = beds.TryGetValue(b.Id, out var bed);
                    if (!ok || !BedPieceState(bed!, out var state))
                    {
                        known = false;
                        break;
                    }
                    var owners = new List<BedOwner>();
                    foreach (var o in bed!.Owners)
                    {
                        var cluster = clusters.TryGetValue(o, out var cl) ? cl : [o.Value];
                        owners.Add(new BedOwner { Pawn = o.Value, Cluster = EquatableArray.From(cluster) });
                    }
                    piece = piece with { State = state, Owners = EquatableArray.From(owners) };
                }
                pieces.Add(piece);
            }
            if (known)
            {
                output.Add(new RoomContents { Room = planned.Id, Pieces = EquatableArray.From(pieces) });
            }
        }
        return output;
    }

    /// <summary>The RoomPiece state of a humanlike bed; false while its medical or prisoner flag is unknown.</summary>
    internal static bool BedPieceState(SleepingBed b, out RoleCondition state)
    {
        var mk = b.Medical.TryGetValue(out var medical);
        var pk = b.Prisoners.TryGetValue(out var prisoner);
        if (!mk || !pk)
        {
            state = RoleCondition.Always;
            return false;
        }
        state = (medical, prisoner) switch
        {
            (true, true) => RoleCondition.BedPrisonerMedical,
            (true, false) => RoleCondition.BedMedical,
            (false, true) => RoleCondition.BedPrisoner,
            _ => RoleCondition.BedFree,
        };
        return true;
    }

    /// <summary>
    /// Maps each pawn with a partner to its love cluster (Pawn.GetLoveCluster): the pawn and everyone
    /// reachable through partner relations, sorted. A pawn with no partner is absent.
    /// </summary>
    internal static Dictionary<PawnId, string[]> LoveClusters(SleepingObservation sleeping)
    {
        var adj = new Dictionary<PawnId, List<PawnId>>();
        void Link(PawnId a, PawnId b)
        {
            if (!adj.TryGetValue(a, out var list))
            {
                adj[a] = list = [];
            }
            list.Add(b);
        }

        foreach (var group in new[] { sleeping.People, sleeping.Slaves, sleeping.Guests })
        {
            foreach (var p in group)
            {
                foreach (var q in p.Partners)
                {
                    Link(p.Id, q);
                    Link(q, p.Id);
                }
            }
        }
        var output = new Dictionary<PawnId, string[]>();
        // Starts are visited in sorted order so the result never depends on dictionary order (the clusters are
        // closures, so the visiting order cannot change them anyway).
        foreach (var start in adj.Keys.Order())
        {
            if (output.ContainsKey(start))
            {
                continue;
            }
            var seen = new HashSet<PawnId> { start };
            var stack = new Stack<PawnId>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                var p = stack.Pop();
                foreach (var q in adj.GetValueOrDefault(p) ?? [])
                {
                    if (seen.Add(q))
                    {
                        stack.Push(q);
                    }
                }
            }
            var cluster = seen.Select(p => p.Value).Order(StringComparer.Ordinal).ToArray();
            foreach (var p in seen)
            {
                output[p] = cluster;
            }
        }
        return output;
    }
}
