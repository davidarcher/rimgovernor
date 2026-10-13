namespace RimGovernor.Brain.Policy;

// The barn as a real room. Each ReserveBarn reservation is a walled room: its interior is the reservation without
// the ring, its door faces the colony core; a pen beside it joins it through an animal flap in the wall they share
// (HerdPen). The room is derived from the reservation, never stored (HerdRooms). The animal-bed planner
// (NextHerdStep) reconciles each room: its ring and door, then animal sleeping spots in the barn, up to one per
// kept animal, nearest the door first, through the build-side reconciler (ReconcileRoom); then each pen's fence,
// gate and marker. A herd the rooms cannot hold gets another reservation (PlanHerdSites); a placed room never
// moves or shrinks. The barn is the bedroom of every animal, bonded ones included; a sick animal is treated in the
// hospital's animal beds (HerdWard).

/// <summary>The native shapes of the two animal bed definitions.</summary>
public sealed record HerdFurniture
{
    /// <summary>The barn's sleeping spot.</summary>
    public InteriorPieceDef Spot { get; init; } = new();

    /// <summary>The hospital ward's animal bed.</summary>
    public InteriorPieceDef Bed { get; init; } = new();

    /// <summary>The barn's climate piece; the zero value while its research is unfinished or its size unread, which plans no heater step (the beds never wait on it).</summary>
    public InteriorPieceDef Heater { get; init; } = new();

    /// <summary>The pen's PenMarker; the zero value while it is not buildable, which plans no new marker (a standing one is kept).</summary>
    public InteriorPieceDef Marker { get; init; } = new();
}

/// <summary>The next animal-bed step.</summary>
public enum HerdStepKind
{
    /// <summary>Nothing is due, or a fact is unknown.</summary>
    [WireName("")]
    None = 0,

    /// <summary>
    /// Room differs from the plan or from Template (the animal beds, and the barn's heater): the build side
    /// reconciles it (ReconcileRoom). The room's state is whatever the diff leaves; there is no shell or place step.
    /// </summary>
    [WireName("reconcile")]
    Reconcile,

    /// <summary>Flag Bed, a standing hospital ward bed, medical.</summary>
    [WireName("medical")]
    Medical,
}

/// <summary>One bounded step towards the barn and the hospital's ward.</summary>
public sealed record HerdStep
{
    public HerdStepKind Kind { get; init; }
    public PlannedRole Role { get; init; }
    public PlannedRoom Room { get; init; } = new();

    /// <summary>A Reconcile's wanted furniture: standing beds where they stand, the missing ones in their template slots, the barn's heater.</summary>
    public IReadOnlyList<WantedPiece> Template { get; init; } = [];

    /// <summary>The census id of the bed a Medical step flags.</summary>
    public string Bed { get; init; } = "";

    /// <summary>Reports whether the planner can act on the step now.</summary>
    public bool Owed() => Kind is HerdStepKind.Reconcile or HerdStepKind.Medical;
}

/// <summary>A candidate door: its wall cell, the side it faces, the step to the cell outside it and how well it faces the room's target.</summary>
internal readonly record struct HerdDoor(Cell Cell, Rotation Rot, int Sx, int Sz, double Score);

public sealed partial record LayoutPlan
{
    /// <summary>The plan's rooms of role (PlannedBarn, PlannedPen), one per reservation in plan order.</summary>
    public IReadOnlyList<PlannedRoom> HerdRooms(PlannedRole role)
    {
        var @out = new List<PlannedRoom>();
        foreach (var r in Reservations)
        {
            var (want, ok) = PolicyFunctions.HerdRole(r.Kind);
            if (ok && want == role)
            {
                @out.Add(HerdRoom(r.Area.Bounds(), role));
            }
        }
        return @out;
    }

    /// <summary>AllRooms and the barns the reservations hold: the rooms whose doors and links a ring is matched against.</summary>
    internal IReadOnlyList<PlannedRoom> RoomsWithHerd() => [.. Rooms, .. HerdRooms(PolicyFunctions.PlannedBarn)];

    /// <summary>The room a barn reservation holds: the door in the middle of the wall facing the colony core (the barn's centre when the plan has no core).</summary>
    internal PlannedRoom HerdRoom(Rectangle area, PlannedRole role)
    {
        var inner = new Rectangle { X = area.X + 1, Z = area.Z + 1, Width = area.Width - 2, Height = area.Height - 2 };
        var room = new PlannedRoom
        {
            Role = role,
            Region = PolicyFunctions.RectRegion(inner),
            Outdoor = role.IsOutdoor(),
        };
        var cx = inner.X + inner.Width / 2.0;
        var cz = inner.Z + inner.Height / 2.0;
        var (tx, tz, ok) = HerdTarget();
        if (!ok)
        {
            tx = cx;
            tz = cz - 1;
        }
        var dx = tx - cx;
        var dz = tz - cz;
        var east = new HerdDoor(new Cell(inner.X + inner.Width, inner.Z + inner.Height / 2), Rotation.East, 1, 0, dx);
        var west = new HerdDoor(new Cell(inner.X - 1, inner.Z + inner.Height / 2), Rotation.West, -1, 0, -dx);
        var north = new HerdDoor(new Cell(inner.X + inner.Width / 2, inner.Z + inner.Height), Rotation.North, 0, 1, dz);
        var south = new HerdDoor(new Cell(inner.X + inner.Width / 2, inner.Z - 1), Rotation.South, 0, -1, -dz);
        HerdDoor first;
        if (Math.Abs(dx) > Math.Abs(dz) && dx > 0)
        {
            first = east;
        }
        else if (Math.Abs(dx) > Math.Abs(dz))
        {
            first = west;
        }
        else if (dz > 0)
        {
            first = north;
        }
        else
        {
            first = south;
        }
        // The door faces its target unless another reservation's walls stand right outside it; then the best side that opens on free ground.
        room = room with { Door = first.Cell, DoorRot = first.Rot };
        if (!DoorBlocked(first, area))
        {
            return room;
        }
        var best = default(HerdDoor);
        var found = false;
        foreach (var d in new[] { east, west, north, south })
        {
            if (d.Rot == first.Rot || DoorBlocked(d, area) || (found && d.Score <= best.Score))
            {
                continue;
            }
            best = d;
            found = true;
        }
        if (found)
        {
            room = room with { Door = best.Cell, DoorRot = best.Rot };
        }
        return room;
    }

    /// <summary>Reports the cell outside d inside another reservation's area (its walls), where the door would open on a wall nobody can walk through.</summary>
    private bool DoorBlocked(HerdDoor d, Rectangle own)
    {
        var outside = new Cell(d.Cell.X + d.Sx, d.Cell.Z + d.Sz);
        foreach (var r in Reserved())
        {
            var a = r.Area;
            if (a.Equal(PolicyFunctions.RectRegion(own)))
            {
                continue;
            }
            if (a.Contains(outside))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The point a herd room's door faces: the colony core.</summary>
    private (double X, double Z, bool Ok) HerdTarget()
    {
        var core = Core();
        if (core is { } c)
        {
            return (c.X, c.Z, true);
        }
        return (0, 0, false);
    }

    /// <summary>The beds the plan's rooms of role hold, counting 1x1 beds: the sizes of the real definitions are read natively when a room is furnished.</summary>
    internal int HerdCapacity(PlannedRole role)
    {
        var n = 0;
        foreach (var room in HerdRooms(role))
        {
            n += PolicyFunctions.HerdRoomBeds(room, new PieceShapes(), new InteriorPieceDef { Def = "bed", Size = new Cell(1, 1) });
        }
        return n;
    }
}

public static partial class PolicyFunctions
{
    /// <summary>The barn's plan role.</summary>
    public static PlannedRole PlannedBarn => PlannedRole.Barn;

    /// <summary>The plan role of a pen reservation: an outdoor room, a fence and a gate round grazing ground, its barn's wall on the shared side.</summary>
    public static PlannedRole PlannedPen => PlannedRole.Pen;

    /// <summary>The fewest animals a barn is sized for.</summary>
    internal const int HerdBarnMinBeds = 8;

    /// <summary>Bounds the interior side a herd room searches.</summary>
    internal const int HerdSizeSide = 24;

    /// <summary>
    /// The barn's climate slot: the heater the temperature planner counts as conditioning the room and the power
    /// planner connects like any other unpowered consumer. It is no bed.
    /// </summary>
    internal const string HerdHeaterSlot = "climate.heater";

    /// <summary>The 1x1 beds an interior w by h holds whichever wall its door is in: the columns beside the barn aisle times its bed rows.</summary>
    internal static int HerdGridBeds(int w, int h) => Math.Min((w - 1) * (h / 2), (h - 1) * (w / 2));

    /// <summary>The near-square interior holding at least beds 1x1 beds, the fewest cells first.</summary>
    internal static (int W, int H) HerdSide(int beds)
    {
        var best = 0;
        var w = 0;
        var h = 0;
        for (var a = 3; a <= HerdSizeSide; a++)
        {
            for (var b = a; b <= HerdSizeSide; b++)
            {
                if (HerdGridBeds(a, b) < beds || HerdGridBeds(b, a) < beds)
                {
                    continue;
                }
                var cells = a * b;
                if (best == 0 || cells < best || (cells == best && b - a < h - w))
                {
                    best = cells;
                    w = a;
                    h = b;
                }
            }
        }
        if (best == 0)
        {
            return (HerdSizeSide, HerdSizeSide);
        }
        return (w, h);
    }

    /// <summary>The plan role of a herd reservation kind.</summary>
    internal static (PlannedRole Role, bool Ok) HerdRole(ReservationKind kind)
    {
        if (kind == ReserveBarn)
        {
            return (PlannedBarn, true);
        }
        if (kind == ReservePen)
        {
            return (PlannedPen, true);
        }
        return (PlannedRole.None, false);
    }

    /// <summary>The beds def's template holds in room.</summary>
    internal static int HerdRoomBeds(PlannedRoom room, PieceShapes shapes, InteriorPieceDef def)
    {
        var (inner, ok) = InteriorRoomFromLayout(room, shapes);
        if (!ok)
        {
            return 0;
        }
        var (plan, planned) = PlanRoomInterior(inner!, def);
        if (!planned)
        {
            return 0;
        }
        return HerdBedPieces(plan.Pieces, room.Door).Count;
    }

    /// <summary>The pieces of a barn plan that are beds: all but the climate slot, nearest the door first (a barn short of animals fills from the door).</summary>
    internal static IReadOnlyList<InteriorPiece> HerdBedPieces(IReadOnlyList<InteriorPiece> pieces, Cell door)
    {
        var at = new Rectangle
        {
            X = door.X,
            Z = door.Z,
            Width = 1,
            Height = 1,
        };
        return pieces.Where(p => p.Slot != HerdHeaterSlot).OrderBy(p => CentreDistance(p.Rect, at)).ToList();
    }

    /// <summary>
    /// Picks the next barn or ward step for a herd of animals kept animals and ward animal beds (WardBeds): the
    /// barn holds one sleeping spot per animal, filled room by room in plan order; the hospital, once it stands,
    /// holds the ward beds in the free floor beside its own template. A barn is reconciled while its ring or doors
    /// differ from the plan (a lost wall is rebuilt the same way as a first shell) or a bed of its quota or its
    /// heater is missing; each ward bed is flagged medical (sleeping's Medical fact says which are) first. The
    /// hospital's ring is the hospital planner's, so the ward step waits for the room to stand. None for no
    /// animals and no ward demand, or once every room matches and every bed stands and is flagged.
    /// </summary>
    public static HerdStep NextHerdStep(
        LayoutPlan plan,
        RoomObservation rooms,
        GroundCensus ground,
        IReadOnlyList<CurrentBuilding> built,
        IReadOnlyList<SleepingBed> sleeping,
        int animals,
        int ward,
        HerdFurniture f
    )
    {
        var taken = new HashSet<Cell>();
        foreach (var b in built)
        {
            foreach (var c in b.Cells)
            {
                taken.Add(c);
            }
        }
        var want = animals;
        foreach (var room in plan.HerdRooms(PlannedBarn))
        {
            if (want <= 0)
            {
                break;
            }
            var (inner, ok) = InteriorRoomFromLayout(room, rooms.Shapes);
            if (!ok)
            {
                continue;
            }
            var (layout, planned) = PlanRoomInterior(inner!, f.Spot);
            if (!planned)
            {
                continue;
            }
            var beds = HerdBedPieces(layout.Pieces, room.Door);
            var quota = Math.Min(want, beds.Count);
            want -= quota;
            var (template, absent) = HerdTemplate(PlannedBarn, f.Spot, f.Heater, layout.Pieces, quota, room, built, taken);
            if (absent || !plan.GroundMatches(room, ground))
            {
                return new HerdStep
                {
                    Kind = HerdStepKind.Reconcile,
                    Role = PlannedBarn,
                    Room = room,
                    Template = template,
                };
            }
        }
        foreach (var room in plan.HerdRooms(PlannedPen))
        {
            var (piece, marked) = PenMarkerPiece(room, f.Marker, built, taken);
            IReadOnlyList<WantedPiece> template = marked ? [piece] : [];
            if ((marked && !PenMarkerStanding(room, built)) || !plan.GroundMatches(room, ground))
            {
                return new HerdStep
                {
                    Kind = HerdStepKind.Reconcile,
                    Role = PlannedPen,
                    Room = room,
                    Template = template,
                };
            }
        }
        if (ward <= 0 || string.IsNullOrEmpty(f.Bed.Def))
        {
            return new HerdStep();
        }
        foreach (var room in plan.Hospitals())
        {
            var (_, standing) = CensusRoomIn(room, rooms);
            if (!standing)
            {
                continue;
            }
            var (bed, due) = UnflaggedWardBed(room, built, sleeping, f.Bed.Def);
            if (due)
            {
                return new HerdStep
                {
                    Kind = HerdStepKind.Medical,
                    Role = PlannedHospital,
                    Room = room,
                    Bed = bed,
                };
            }
            var (template, absent) = WardTemplate(room, rooms.Shapes, f.Bed, ward, built, taken);
            if (absent)
            {
                return new HerdStep
                {
                    Kind = HerdStepKind.Reconcile,
                    Role = PlannedHospital,
                    Room = room,
                    Template = template,
                };
            }
            ward -= Math.Min(ward, template.Count);
            if (ward <= 0)
            {
                break;
            }
        }
        return new HerdStep();
    }

    /// <summary>
    /// A barn's wanted furniture: the beds of def standing in the room (up to quota) where they stand, the
    /// missing ones in the first free planned bed slots, and the barn's heater (standing, else its planned slot
    /// when free). Absent reports a planned piece the room lacks. A heater that is not buildable yet (no def) is no
    /// part of the template.
    /// </summary>
    internal static (IReadOnlyList<WantedPiece> Template, bool Absent) HerdTemplate(
        PlannedRole role,
        InteriorPieceDef def,
        InteriorPieceDef heater,
        IReadOnlyList<InteriorPiece> pieces,
        int quota,
        PlannedRoom room,
        IReadOnlyList<CurrentBuilding> built,
        IReadOnlySet<Cell> taken
    )
    {
        var template = new List<WantedPiece>();
        var absent = false;
        bool Inside(CurrentBuilding b, string name) =>
            b.Building!.Definition == name && b.Cells.Count > 0 && RectInside(room.Region.Bounds(), CellsRectangle(b.Cells));
        WantedPiece Standing(CurrentBuilding b)
        {
            var r = CellsRectangle(b.Cells);
            return new WantedPiece
            {
                DefName = b.Building!.Definition,
                Minimum = new Cell(r.X, r.Z),
                Maximum = new Cell(r.X + r.Width - 1, r.Z + r.Height - 1),
            };
        }
        WantedPiece Planned(InteriorPiece p) =>
            new()
            {
                DefName = p.Def,
                Minimum = new Cell(p.Rect.X, p.Rect.Z),
                Maximum = new Cell(p.Rect.X + p.Rect.Width - 1, p.Rect.Z + p.Rect.Height - 1),
                Slot = p.Slot,
                Size = p.Size,
                Rot = p.Rot,
            };
        bool Free(InteriorPiece p) => !RectCells(p.Rect).Any(taken.Contains);
        var have = 0;
        foreach (var b in built)
        {
            if (have < quota && Inside(b, def.Def))
            {
                template.Add(Standing(b));
                have++;
            }
        }
        foreach (var p in HerdBedPieces(pieces, room.Door))
        {
            if (have < quota && Free(p))
            {
                template.Add(Planned(p));
                absent = true;
                have++;
            }
        }
        if (role != PlannedBarn || string.IsNullOrEmpty(heater.Def))
        {
            return (template, absent);
        }
        foreach (var b in built)
        {
            if (Inside(b, heater.Def))
            {
                template.Add(Standing(b));
                return (template, absent);
            }
        }
        foreach (var p in pieces)
        {
            if (p.Slot == HerdHeaterSlot && p.Def == heater.Def && Free(p))
            {
                template.Add(Planned(p));
                return (template, true);
            }
        }
        return (template, absent);
    }
}
