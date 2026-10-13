namespace RimGovernor.Brain.Policy;

// Couple beds. When two colonists become a couple (sleepingCouples) and share no double bed, the couple's room
// gets one in its bedroom template's bed slot. The change is walked across reviews, each step read back from
// the census:
//   - pack: the single beds the two own (the couple's room's first) are uninstalled; vanilla hauling stores the
//     packed items, and the uninstall frees the partner's old room;
//   - install: the couple's room, found from the cell of its packed bed, gets a DoubleBed in the template's
//     bed slot: a stored one reinstalled, else a new one built.
//
// The ordinary assignment then gives both partners the double bed (ReviewSleeping offers a couple's double bed
// first).

public enum CoupleBedKind
{
    [WireName("")]
    None = 0,
    [WireName("pack")]
    CouplePack,
    [WireName("install")]
    CoupleInstall,
}

/// <summary>
/// The next couple bed step. Pack lists the beds to uninstall (the couple's room's first); Room is the room's
/// census id. An install is the build side's: Planned is the couple's planned room and Template its DoubleBed
/// in the bedroom template's bed slot, which ReconcileRoom installs from stock or builds.
/// </summary>
public sealed record CoupleBed
{
    public CoupleBedKind Kind { get; init; }

    public PawnId Pawn { get; init; }

    public PawnId Partner { get; init; }

    public string Room { get; init; } = "";

    public EquatableArray<FurniturePiece> Pack { get; init; }

    public PlannedRoom? Planned { get; init; }

    public EquatableArray<WantedPiece> Template { get; init; }
}

public static partial class PolicyFunctions
{
    /// <summary>The packed (minified) item a bed becomes.</summary>
    public const string PackedFurnitureDefinition = "MinifiedThing";

    /// <summary>The bedroom template's bed slot for a DoubleBed in room, and the planned room of plan that room stands in; null when plan has none.</summary>
    private static (PlannedRoom Planned, IReadOnlyList<WantedPiece> Template)? CoupleBedSlot(LayoutPlan plan, FurnitureRoom room)
    {
        foreach (var planned in plan.Rooms)
        {
            if (planned.Region.Bounds() != room.Room.Interior)
            {
                continue;
            }
            var (template, ok) = BedroomTemplate(planned, room.Room.Shapes, SleepingCoupleBedDefinition);
            return ok ? (planned, template) : null;
        }
        return null;
    }

    /// <summary>
    /// The first couple (by lower pawn id) bed step due, null when none. rooms are the furniture rooms, each of
    /// which must stand in a planned room of plan (its install is reconciled); packed are the cells of the beds
    /// this Episode's completed pack steps uninstalled, the couple's room's bed first; buildable reports that a
    /// DoubleBed can be built, without which nothing is packed.
    /// </summary>
    public static CoupleBed? NextCoupleBed(
        SleepingObservation obs,
        LayoutPlan plan,
        IReadOnlyList<FurnitureRoom> rooms,
        IReadOnlyList<Cell> packed,
        bool buildable)
    {
        var couples = SleepingCouples(obs.People);
        var owned = new Dictionary<PawnId, string>();
        foreach (var p in obs.People)
        {
            owned[p.Id] = p.OwnedBed.TryGetValue(out var bed) ? bed : "";
        }
        var beds = new Dictionary<string, SleepingBed>(StringComparer.Ordinal);
        foreach (var b in obs.Beds)
        {
            beds[b.Id] = b;
        }
        (FurniturePiece Piece, FurnitureRoom Room)? PieceOf(string bed)
        {
            foreach (var r in rooms)
            {
                foreach (var p in r.Pieces)
                {
                    if (p.Thing == bed)
                    {
                        return (p, r);
                    }
                }
            }
            return null;
        }
        var pawns = couples.Where(kv => kv.Key < kv.Value).Select(kv => kv.Key).Order().ToList();
        foreach (var p in pawns)
        {
            var q = couples[p];
            // A double bed only the couple holds (or nobody) is the ordinary assignment's to give.
            var shared = false;
            foreach (var b in obs.Beds)
            {
                var only = b.Owners.All(o => o == p || o == q);
                var humanlike = b.Humanlike.TryGetValue(out var h) && h;
                shared = shared || (SleepingDoubleBeds.Contains(b.Definition) && humanlike && only);
            }
            if (shared)
            {
                continue;
            }
            var step = new CoupleBed { Pawn = p, Partner = q };
            FurnitureRoom? home = null;
            var pack = new List<FurniturePiece>();
            foreach (var pawn in new[] { p, q })
            {
                if (!beds.TryGetValue(owned.GetValueOrDefault(pawn, ""), out var b) || SleepingDoubleBeds.Contains(b.Definition))
                {
                    continue;
                }
                var only = b.Owners.All(o => o == p || o == q);
                var found = PieceOf(b.Id);
                if (!only || found is null)
                {
                    continue;
                }
                var (piece, room) = found.Value;
                if (step.Room.Length == 0)
                {
                    step = step with { Room = room.Id };
                    home = room;
                }
                if (pack.Count == 0 || pack[0].Thing != piece.Thing)
                {
                    pack.Add(piece);
                }
            }
            if (pack.Count > 0)
            {
                if (CoupleBedSlot(plan, home!) is null || !buildable)
                {
                    continue;
                }
                return step with { Kind = CoupleBedKind.CouplePack, Pack = EquatableArray.From(pack) };
            }
            if (packed.Count == 0)
            {
                continue;
            }
            foreach (var r in rooms)
            {
                if (CellInRect(r.Room.Interior, packed[0]))
                {
                    if (CoupleBedSlot(plan, r) is not var (planned, template))
                    {
                        break;
                    }
                    return new CoupleBed
                    {
                        Kind = CoupleBedKind.CoupleInstall,
                        Pawn = p,
                        Partner = q,
                        Room = r.Id,
                        Planned = planned,
                        Template = EquatableArray.From(template),
                    };
                }
            }
        }
        return null;
    }

    /// <summary>The first free anchor in room where a bed of def fits (the bed replacement's placement), for reinstalling a stored bed.</summary>
    public static (Cell Cell, Rotation Rotation)? BedSpot(FurnitureRoom room, Resource def) => BedSpotCore(room, def);

    /// <summary>
    /// FurnitureRooms for the couple bed lever. Packing the couple's beds leaves their room bedless, and native
    /// then reads it as RoomRoleNone, which has no interior template; such an empty room is read as a bedroom
    /// so the install step still finds it by its packed cell.
    /// </summary>
    public static IReadOnlyList<FurnitureRoom> CoupleBedRooms(RoomObservation rooms, CurrentConstruction census, IReadOnlyList<SiteCell> cells)
    {
        var read = rooms.Rooms.Select(room =>
            room.Role.TryGetValue(out var role) && role == RoomRole.None && room.Beds.Count == 0
                ? room with { Role = Known(RoomRole.Bedroom) }
                : room).ToList();
        return FurnitureRooms(rooms with { Rooms = EquatableArray.From(read) }, census, cells);
    }

    private static bool CellInRect(Rectangle r, Cell c) => c.X >= r.X && c.Z >= r.Z && c.X < r.X + r.Width && c.Z < r.Z + r.Height;
}
