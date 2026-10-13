using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

// The build side of the reconciler: one library an owning concern calls for its own room. The owner decides
// which room and when; the reconciler decides how: it diffs the room's ring, floor and furniture against the
// ground and reports the operations ready now, installing from packed stock first (Stock) and building on site
// only when none is stored. The plan-wide clear side (PlannedGroundStep) owns the ring's removals, so those
// kinds are not the owner's.
public sealed partial record LayoutPlan
{
    /// <summary>
    /// g with the natural rock on the rings of the plan's rooms counted as wall, except where the plan puts a
    /// door: rock walls a dug room as it stands and is no wall to raise.
    /// </summary>
    public GroundCensus GroundWithRock(GroundCensus g, IReadOnlyList<Cell> rock)
    {
        var doors = new HashSet<Cell>();
        foreach (var r in RoomsWithHerd())
        {
            foreach (var d in this.ShellDoors(r))
            {
                doors.Add(d);
            }
        }
        var output = new GroundCensus
        {
            Walls = new HashSet<Cell>(g.Walls),
            Doors = g.Doors,
            Fences = g.Fences,
            Gates = g.Gates,
            Flaps = g.Flaps,
            Stuff = g.Stuff,
            Vents = g.Vents,
            Rock = [],
        };
        foreach (var c in rock)
        {
            if (!doors.Contains(c))
            {
                output.Walls.Add(c);
                output.Rock.Add(c);
            }
        }
        return output;
    }
}

public static partial class PolicyFunctions
{
    /// <summary>
    /// The room's next operations: every ready operation of Reconcile except the ring's removals (door and
    /// wall out, roof off), batched by kind in table order. Empty when the room matches or waits on a
    /// prerequisite.
    /// </summary>
    public static IReadOnlyList<Operation> ReconcileRoom(ReconcileInput input) => ReconcileRoomHolds(input).Operations;

    /// <summary>ReconcileRoom with the foreign things the room leaves standing.</summary>
    public static (IReadOnlyList<Operation> Operations, IReadOnlyList<ReconcileHold> Holds) ReconcileRoomHolds(ReconcileInput input)
    {
        var output = new List<Operation>();
        var rec = Reconcile(input);
        foreach (var op in rec.Ready)
        {
            if (op.Kind is OpKind.DoorOut or OpKind.WallOut or OpKind.RoofOff)
            {
                continue;
            }
            output.Add(op);
        }
        return (output, rec.Holds);
    }

    /// <summary>
    /// The census rows an owner reconciles its room against: the ring, frames and stand-ins, the template's
    /// standing pieces and the buildings forbidden finds (packed or taken down by the reconcile). Every other
    /// building the room holds is left alone, so an owner never repacks the furniture the room's quality
    /// levers added.
    /// </summary>
    public static IReadOnlyList<ClearanceTarget> OwnRows(IReadOnlyList<ClearanceTarget> rows, IReadOnlyList<WantedPiece> template, Func<string, bool>? forbidden)
    {
        var output = new List<ClearanceTarget>();
        foreach (var row in rows)
        {
            var keep = !row.Player || row.Class == "ancient_wall_door" || row.DefName.StartsWith("Frame_", StringComparison.Ordinal) || StandInBed(row) || (forbidden is not null && forbidden(row.DefName));
            foreach (var p in template)
            {
                keep = keep || (p.DefName == row.DefName && p.Minimum == row.Minimum && p.Maximum == row.Maximum);
            }
            if (keep)
            {
                output.Add(row);
            }
        }
        return output;
    }

    /// <summary>The rectangle a planned room's ring and interior cover, the ground an owner reads the census on.</summary>
    public static Rectangle RoomGround(this PlannedRoom r) =>
        r.Unfenced() ? r.Region.Bounds() : RoomGround(r.Region.Bounds());

    /// <summary>The template piece for p: its slot's cells, size and rotation.</summary>
    public static WantedPiece Wanted(this InteriorPiece p) => new()
    {
        DefName = p.Def,
        Minimum = new Cell(p.Rect.X, p.Rect.Z),
        Maximum = new Cell(p.Rect.X + p.Rect.Width - 1, p.Rect.Z + p.Rect.Height - 1),
        Slot = p.Slot,
        Size = p.Size,
        Rot = p.Rot,
    };

    /// <summary>
    /// The cells a planned room's ring and interior cover: the region and its wall ring, so an L's notch is no
    /// ground; an unfenced room's block.
    /// </summary>
    internal static Region GroundRegion(this PlannedRoom r)
    {
        if (r.Unfenced())
        {
            return r.Region;
        }
        var cells = new List<Cell>(r.Region.Cells());
        cells.AddRange(r.Region.Walls().Cells());
        return RegionOf(cells);
    }
}
