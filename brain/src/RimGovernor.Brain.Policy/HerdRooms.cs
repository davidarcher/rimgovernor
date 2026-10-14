namespace RimGovernor.Brain.Policy;

// The herd's rooms. A pen is a walled-off outdoor PlannedRoom of the plan, a room node of the station graph
// (HerdRoomNodes) the tiler sites with the others (HerdPen). A pen carries the herd's key (PlannedRoom.Herd). The
// planner (NextHerdStep) reconciles each pen's fence, gate and marker through the build-side reconciler
// (ReconcileRoom), then the hospital's animal beds (HerdWard): a sick animal is treated in the hospital.

/// <summary>The native shapes of the animal definitions the herd rooms are built from.</summary>
public sealed record HerdFurniture
{
    /// <summary>The hospital ward's animal bed.</summary>
    public InteriorPieceDef Bed { get; init; } = new();

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
    /// Room differs from the plan or from Template (a pen's marker, a ward's animal beds): the build side
    /// reconciles it (ReconcileRoom). The room's state is whatever the diff leaves; there is no shell or place step.
    /// </summary>
    [WireName("reconcile")]
    Reconcile,

    /// <summary>Flag Bed, a standing hospital ward bed, medical.</summary>
    [WireName("medical")]
    Medical,
}

/// <summary>One bounded step towards the pens and the hospital's ward.</summary>
public sealed record HerdStep
{
    public HerdStepKind Kind { get; init; }
    public PlannedRole Role { get; init; }
    public PlannedRoom Room { get; init; } = new();

    /// <summary>A Reconcile's wanted furniture: a pen's marker, or a ward's beds where they stand and the missing ones in their template slots.</summary>
    public IReadOnlyList<WantedPiece> Template { get; init; } = [];

    /// <summary>The census id of the bed a Medical step flags.</summary>
    public string Bed { get; init; } = "";

    /// <summary>Reports whether the planner can act on the step now.</summary>
    public bool Owed() => Kind is HerdStepKind.Reconcile or HerdStepKind.Medical;
}

public sealed partial record LayoutPlan
{
    /// <summary>The rooms of role of the unit keyed herd, in plan order.</summary>
    internal IReadOnlyList<PlannedRoom> HerdRooms(PlannedRole role, string herd) => [.. RoomsOf(role).Where(r => r.Herd == herd)];

    /// <summary>
    /// The room a pen outline holds, as a plan saved with them as reservations is read (WithLegacySites): a
    /// door in the middle of the south wall. A replan tiles the room again, and the tiler chooses its door. Id is
    /// the caller's.
    /// </summary>
    internal static PlannedRoom HerdRoom(Rectangle area, PlannedRole role, string herd)
    {
        var inner = new Rectangle { X = area.X + 1, Z = area.Z + 1, Width = area.Width - 2, Height = area.Height - 2 };
        return new PlannedRoom
        {
            Role = role,
            Region = PolicyFunctions.RectRegion(inner),
            Outdoor = role.IsOutdoor(),
            Herd = herd,
            Door = new Cell(inner.X + (inner.Width / 2), inner.Z - 1),
            DoorRot = Rotation.South,
        };
    }

}

public static partial class PolicyFunctions
{
    /// <summary>The plan role of a pen: an outdoor room, a fence and a gate round grazing ground.</summary>
    public static PlannedRole PlannedPen => PlannedRole.Pen;

    /// <summary>
    /// Picks the next pen or ward step for ward animal beds (WardBeds): a pen is reconciled while its fence, gate or
    /// marker differ from the plan; the hospital, once it stands, holds the ward beds in the free floor beside its own
    /// template, each flagged medical (sleeping's Medical fact says which are) first. The hospital's ring is the
    /// hospital planner's, so the ward step waits for the room to stand. None for no pens and no ward demand, or once
    /// every room matches and every bed stands and is flagged.
    /// </summary>
    public static HerdStep NextHerdStep(
        LayoutPlan plan,
        RoomObservation rooms,
        GroundCensus ground,
        IReadOnlyList<CurrentBuilding> built,
        IReadOnlyList<SleepingBed> sleeping,
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
        foreach (var room in plan.RoomsOf(PlannedPen))
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
}
