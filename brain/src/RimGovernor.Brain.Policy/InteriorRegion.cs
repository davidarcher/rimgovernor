using System.Globalization;
using System.Text;

namespace RimGovernor.Brain.Policy;

// The region interior is the live furnishing entry (#2956): a room's role says what its floor should hold
// (RegionWants), and the region placer seats it, behind the room's doors, on any cell set; the seeded interior
// search then improves the layout (#2869). It replaces the role templates, which fit one rectangle in a
// canonical frame. A role with no entry here has no interior.
//
// Slot names are the ones the reconcilers and runtime consumers read; a want names its pieces
// (RegionWant.Names) where a consumer reads the name.

/// <summary>
/// The region interior's one judgment table, registered in DefTables so a renamed def fails the
/// dangling-reference gate. ShelfDef is the input shelf beside a workshop bench and LampDef the standing lamp
/// of a bedroom, throne room and dining room; KitchenStoves is the most stoves a kitchen wants: two stoves feed
/// a mid-sized colony, and a third only adds floor the Space stat counts against the room.
/// </summary>
public sealed record InteriorTable(string ShelfDef, string LampDef, int KitchenStoves);

/// <summary>The room's floor seen from its entrance: Width cells along the entrance wall, Depth away from it, the entrance at column Entrance.</summary>
public readonly record struct RegionFrame(int Width, int Depth, int Entrance);

/// <summary>Says what a role's floor should hold: the wants, in the order they place, and false when the room cannot be furnished for the piece being placed.</summary>
internal delegate (IReadOnlyList<RegionWant> Wants, bool Ok) RegionRole(InteriorRoom room, InteriorPieceDef piece, RegionFrame f);

public static partial class PolicyFunctions
{
    private static readonly InteriorTable InteriorTableValue = new(WorkshopShelfDef, StandingLampDef, 2);

    /// <summary>The region interior's judgment table.</summary>
    public static InteriorTable InteriorDefs() => InteriorTableValue;

    /// <summary>The room's floor: its Region when set, else the Interior rectangle.</summary>
    public static Region Floor(this InteriorRoom r) => r.Region is { } g && !g.Empty() ? g : RectRegion(r.Interior);

    /// <summary>
    /// The room's floor seen from its entrance. A floor that is not a rectangle is measured by its bounding
    /// rectangle, so it never wants less than a rectangle of its bounds would hold; the placer then seats what
    /// its cells fit.
    /// </summary>
    public static RegionFrame RegionFrameOf(this InteriorRoom r)
    {
        var probe = r with { Interior = r.Floor().Bounds(), Region = null };
        var (f, ok) = EntranceFrame(probe);
        if (ok)
        {
            return f;
        }
        var b = probe.Interior;
        return new RegionFrame(Math.Max(b.Width, b.Height), Math.Min(b.Width, b.Height), Math.Max(b.Width, b.Height) / 2);
    }

    private static readonly Lazy<IReadOnlyDictionary<RoomRole, RegionRole>> RegionRoles = new(() =>
    {
        RegionRole child = ChildRoomWants;
        return new Dictionary<RoomRole, RegionRole>
        {
            [RoomRole.Workshop] = WorkshopWants,
            [RoomRole.Laboratory] = LaboratoryWants,
            [RoomRole.Kitchen] = KitchenWants,
            [RoomRole.Tomb] = TombWants,
            [RoomRole.PrisonCell] = JailWants,
            [RoomRole.Hospital] = HospitalWants,
            [RoomRole.Bedroom] = BedroomWants,
            [RoomRole.Suite] = BedroomWants,
            [RoomRole.ThroneRoom] = ThroneWants,
            [RoomRole.Shelter] = ShelterWants,
            [RoomRole.Barn] = BarnWants,
            [RoomRole.DiningRoom] = DiningWants(false),
            [RoomRole.RecRoom] = DiningWants(true),
            [RoomRole.Nursery] = child,
            [RoomRole.Playroom] = child,
            [RoomRole.Classroom] = child,
            [RoomRole.WorshipRoom] = child,
            [RoomRole.DeathrestChamber] = child,
            [RoomRole.ContainmentCell] = child,
            [RoomRole.IsolationRoom] = child,
        };
    });

    /// <summary>The room's placement: its role's wants on its floor behind its doors.</summary>
    private static (RegionPlacement Placement, bool Ok) RegionRequest(InteriorRoom room, InteriorPieceDef piece)
    {
        if (!RegionRoles.Value.TryGetValue(room.Role, out var role) || room.Doors.Count == 0)
        {
            return (default!, false);
        }
        var (wants, ok) = role(room, piece, room.RegionFrameOf());
        if (!ok || wants.Count == 0)
        {
            return (default!, false);
        }
        wants = Unbuilt(wants, room.Built);
        if (wants.Count == 0 && room.Built.Count == 0)
        {
            return (default!, false);
        }
        var reserved = new List<Cell>(room.Reserved);
        foreach (var p in room.Built)
        {
            reserved.AddRange(RectCells(p.Rect));
            reserved.AddRange(p.Reserves());
        }
        return (new RegionPlacement { Region = room.Floor(), Doors = EquatableArray.From(room.Doors), Reserved = EquatableArray.From(reserved), Wants = EquatableArray.From(wants) }, true);
    }

    /// <summary>
    /// The wants less what stands: a built piece takes one piece of the first want of its definition that still
    /// has one, and the names of the pieces it took go with it.
    /// </summary>
    private static List<RegionWant> Unbuilt(IReadOnlyList<RegionWant> wants, IReadOnlyList<InteriorPiece> built)
    {
        var taken = new Dictionary<string, int>();
        foreach (var p in built)
        {
            taken[p.Def] = taken.GetValueOrDefault(p.Def) + 1;
        }
        var output = new List<RegionWant>();
        foreach (var want in wants)
        {
            var n = Math.Min(taken.GetValueOrDefault(want.Def), want.Count);
            taken[want.Def] = taken.GetValueOrDefault(want.Def) - n;
            var w = want with
            {
                Count = want.Count - n,
                Min = Math.Max(want.Min - n, 0),
                Names = n < want.Names.Count ? [.. want.Names.Skip(n)] : [],
            };
            if (w.Count > 0)
            {
                output.Add(w);
            }
        }
        return output;
    }

    /// <summary>Seeds the room's search from what the room is, so equal rooms furnish alike and no wall time enters (FNV-1a, 64 bit).</summary>
    private static ulong RegionSeed(InteriorRoom room)
    {
        var text = new StringBuilder();
        text.Append(room.Role).Append(' ');
        text.Append('[').AppendJoin(' ', room.Floor().Runs.Select(r => r.ToString())).Append("] ");
        text.Append('[').AppendJoin(' ', room.Doors.Select(d => string.Create(CultureInfo.InvariantCulture, $"{{{d.X} {d.Z}}}"))).Append(']');
        var h = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(text.ToString()))
        {
            h ^= b;
            h *= 1099511628211UL;
        }
        return h;
    }

    /// <summary>
    /// Furnishes the room's floor from its role's wants with the greedy placer, improved by the seeded interior
    /// search. It is false when the role is not furnished by the region placer, the room has no door, or the
    /// floor does not fit what the role requires. The plan carries world footprints only: Frame and Canonical
    /// belong to the templates.
    /// </summary>
    public static (InteriorPlan Plan, bool Ok) PlanRegionInterior(InteriorRoom room, InteriorPieceDef piece) => PlanRegion(room, piece, true);

    private static (InteriorPlan Plan, bool Ok) PlanRegion(InteriorRoom room, InteriorPieceDef piece, bool search)
    {
        var (req, ok) = RegionRequest(room, piece);
        if (!ok)
        {
            return (default!, false);
        }
        if (search)
        {
            req = req with
            {
                Search = new InteriorSearch
                {
                    Candidates = req.Doors,
                    Shapes = room.Shapes,
                    Dining = room.Dining,
                    Pinned = room.Built,
                    Seed = RegionSeed(room),
                },
            };
        }
        IReadOnlyList<InteriorPiece> pieces;
        try
        {
            pieces = PlaceInRegion(req);
        }
        catch (DomainException)
        {
            return (default!, false);
        }
        return (new InteriorPlan { Room = room, Pieces = pieces }, true);
    }

    /// <summary>The live interior entry: the region placer's plan, false for a role with no entry in the role table.</summary>
    public static (InteriorPlan Plan, bool Ok) PlanRoomInterior(InteriorRoom room, InteriorPieceDef piece) => PlanRegionInterior(room, piece);

    /// <summary>Reports that the room has a plan, without the search: the existence check of a caller that reads no piece.</summary>
    public static bool RoomHasInterior(InteriorRoom room, InteriorPieceDef piece) => PlanRegion(room, piece, false).Ok;

    /// <summary>The definition a family's bench row plans: the piece being placed when the family holds it, else the family's default bench.</summary>
    internal static (InteriorPieceDef Def, bool Ok) BenchDef(this InteriorRoom r, InteriorPieceDef piece, RoomRole family) =>
        piece.Family == family ? (piece, true) : r.Shapes.Get(r.Shapes.Furniture.BenchFor(family));

    /// <summary>A want for count pieces of a shape.</summary>
    internal static RegionWant ShapeWant(string slot, InteriorPieceDef d, int count, int min, Func<int, string> names) => new()
    {
        Slot = slot,
        Def = d.Def,
        Size = d.Size,
        Interaction = d.Interaction,
        Count = count,
        Min = min,
        Names = [.. Enumerable.Range(0, count).Select(names)],
    };

    internal static Func<int, string> Numbered(string prefix, int from) => i => $"{prefix}.{i + from}";

    /// <summary>How many benches of a footprint one wall of the room holds with a free cell between them.</summary>
    internal static int BenchRowCapacity(RegionFrame f, InteriorPieceDef d, int gap)
    {
        if (!FrontInteraction(d) || f.Depth < d.Size.Z + 2)
        {
            return 0;
        }
        return RowCapacity(f.Width, d.Size.X, gap);
    }
}
