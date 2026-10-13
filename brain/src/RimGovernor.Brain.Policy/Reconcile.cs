using RimGovernor.Brain.Domain;

namespace RimGovernor.Brain.Policy;

// Plan-vs-ground reconciliation: the operations a PlannedRoom still owes, from a per-cell diff of the wanted
// ring, floor and furniture against what stands. A room's state is whatever the diff leaves; the old
// clearance phases (GroundPhase) label operations, they are not stages.
//
// The dependency table is fixed, per cell where cells interact:
//
//   roof off -> wall out -> door in
//   pack or furniture out on a cell -> wall in, floor out, floor in, install, build there
//   floor out -> floor in -> install, build, on the same cells
//
// A floor in waits on its own cell's clearing only, never on the ring.
//
// Furniture standing as wanted is no operation and orders against nothing, and the floor under it is left.
// In-use pieces are packed after every other removal. An otherwise complete ring never has more than one door
// gap open, and a door the plan lacks is swapped for a wall in place. Whether an operation may queue ahead of
// its prerequisite is the placement preview's call at runtime, so the table gates only what the ground itself
// forbids.

/// <summary>One kind of work a reconciliation owes; the ready set batches by kind.</summary>
public enum OpKind
{
    [WireName("")]
    None = 0,

    [WireName("roof_off")]
    RoofOff,

    /// <summary>A ring door swapped for a wall in place.</summary>
    [WireName("door_out")]
    DoorOut,

    [WireName("wall_out")]
    WallOut,

    [WireName("wall_in")]
    WallIn,

    /// <summary>A standing wall swapped in place for a better stuff.</summary>
    [WireName("wall_up")]
    WallUp,

    [WireName("door_in")]
    DoorIn,

    [WireName("pack")]
    Pack,

    /// <summary>Deconstruct what cannot pack.</summary>
    [WireName("furniture_out")]
    FurnitureOut,

    [WireName("pack_in_use")]
    PackInUse,

    [WireName("floor_out")]
    FloorOut,

    [WireName("floor_in")]
    FloorIn,

    [WireName("install")]
    Install,

    [WireName("build")]
    Build,

    // The foreign-thing kinds: claim a ruin as ring wall, cut an impassable plant, move a haulable item off
    // the ground.
    [WireName("claim")]
    Claim,

    [WireName("cut")]
    Cut,

    [WireName("haul_out")]
    HaulOut,

    /// <summary>Builds a planned vent over a standing ring wall.</summary>
    [WireName("vent_in")]
    VentIn,

    /// <summary>Designates the Build Roof area over a roofed-set cell still open to the sky (#2962).</summary>
    [WireName("roof_in")]
    RoofIn,
}

/// <summary>
/// One piece of the room's furniture template: a def and the cells its footprint holds (Minimum to Maximum,
/// inclusive).
/// </summary>
public sealed record WantedPiece
{
    public string DefName { get; init; } = "";

    public Cell Minimum { get; init; }

    public Cell Maximum { get; init; }

    /// <summary>
    /// Slot, Size and Rot place a piece the room does not hold yet: its template slot, the def's native size at
    /// rotation North and its rotation. A piece that stands needs none.
    /// </summary>
    public string Slot { get; init; } = "";

    public Cell Size { get; init; }

    public Rotation Rot { get; init; }

    private Rectangle Footprint => new(Minimum.X, Minimum.Z, Maximum.X - Minimum.X + 1, Maximum.Z - Minimum.Z + 1);

    /// <summary>The cell a native order names for the piece's footprint.</summary>
    public Cell Anchor() => PolicyFunctions.AnchorForRect(Footprint, Size, Rot);

    internal List<Cell> Cells() => PolicyFunctions.RectCells(Footprint);
}

/// <summary>
/// One batch of work of one kind: its cells and, by kind, the buildings it takes down (Targets), the floors it
/// removes or lays (Floors, the def being the actual or the wanted one) or the pieces it installs or builds.
/// </summary>
public sealed record Operation
{
    public OpKind Kind { get; init; }

    public GroundPhase Label { get; init; }

    public IReadOnlyList<Cell> Cells { get; init; } = [];

    public IReadOnlyList<ClearanceTarget> Targets { get; init; } = [];

    public IReadOnlyList<ClearanceFloor> Floors { get; init; } = [];

    public IReadOnlyList<WantedPiece> Pieces { get; init; } = [];
}

/// <summary>One PlannedRoom against the ground.</summary>
public sealed record ReconcileInput
{
    public LayoutPlan Plan { get; init; } = new();

    public PlannedRoom Room { get; init; } = new();

    /// <summary>The colony census's walls and doors (GroundOf).</summary>
    public GroundCensus Ground { get; init; } = new();

    /// <summary>The census's player buildings on the room's ground, as planned-ground clearance reads them.</summary>
    public IReadOnlyList<ClearanceTarget> Rows { get; init; } = [];

    /// <summary>The census's constructed floors.</summary>
    public IReadOnlyList<ClearanceFloor> Floors { get; init; } = [];

    public RoomObservation Rooms { get; init; } = new();

    /// <summary>The wanted template.</summary>
    public IReadOnlyList<WantedPiece> Furniture { get; init; } = [];

    /// <summary>The wanted floor def per interior cell ("" leaves the cell's floor alone).</summary>
    public Func<Cell, string>? WantedFloor { get; init; }

    /// <summary>
    /// Says an existing constructed floor stands in for the wanted one (FloorKept); null keeps only the exact
    /// def. A floor that stands in is no operation: no tear-up for a different adequate floor.
    /// </summary>
    public Func<string, string, bool>? FloorKept { get; init; }

    /// <summary>
    /// Says a standing wall of stuff have is replaced in place by the wanted stuff: the wanted stuff ranks
    /// strictly above it in the ladder and is in stock. Null never swaps a wall. A wall whose stuff the census
    /// does not name is left.
    /// </summary>
    public Func<string, bool>? WallUpgrade { get; init; }

    /// <summary>
    /// The mirror's cells on the room's ground: their foreign things are cleared, claimed or held by the
    /// obstruction policy. Null sees none.
    /// </summary>
    public IReadOnlyList<SiteCell>? Cells { get; init; }

    /// <summary>Counts the packed pieces in storage by def.</summary>
    public IReadOnlyDictionary<string, int>? Stock { get; init; }

    /// <summary>
    /// Whether the vent can be built: a planned vent whose def is unresearched or unknown is an absent option,
    /// owed nothing (#2962).
    /// </summary>
    public bool VentAvailable { get; init; }

    /// <summary>
    /// The cells the mirror reads open to the sky; the plan's roofed set decides which of the room's are owed a
    /// roof (#2962).
    /// </summary>
    public IReadOnlySet<Cell>? Unroofed { get; init; }
}

/// <summary>
/// What a room owes (Owed) and the part of it whose prerequisites hold now (Ready): one operation per kind, in
/// table order.
/// </summary>
public sealed record Reconciliation
{
    public IReadOnlyList<Operation> Owed { get; init; } = [];

    public IReadOnlyList<Operation> Ready { get; init; } = [];

    /// <summary>The foreign things left standing, with the reason.</summary>
    public IReadOnlyList<ReconcileHold> Holds { get; init; } = [];
}

internal sealed class ReconcileItem
{
    public OpKind Kind { get; set; }

    public Cell Cell { get; set; }

    public ClearanceTarget Target { get; set; } = new();

    public ClearanceFloor Floor { get; set; } = new();

    public WantedPiece Piece { get; set; } = new();

    /// <summary>An install fed by a piece this pass packs.</summary>
    public bool Fed { get; set; }

    /// <summary>A thing that is not the colony's: it feeds no install.</summary>
    public bool Foreign { get; set; }

    public bool Ready { get; set; }
}

public static partial class PolicyFunctions
{
    private static readonly OpKind[] OpKindsInOrder =
    [
        OpKind.RoofOff, OpKind.DoorOut, OpKind.WallOut, OpKind.Claim, OpKind.Cut, OpKind.HaulOut, OpKind.WallIn, OpKind.WallUp,
        OpKind.DoorIn, OpKind.Pack, OpKind.FurnitureOut, OpKind.PackInUse, OpKind.FloorOut, OpKind.FloorIn, OpKind.Install,
        OpKind.Build, OpKind.VentIn, OpKind.RoofIn,
    ];

    /// <summary>Diffs the room against the ground.</summary>
    public static Reconciliation Reconcile(ReconcileInput input)
    {
        var r = input.Room;
        var ring = r.WallRing();
        var interior = r.Region;
        if (r.Unfenced())
        {
            // No ring is owed: only the template and the foreign things on the interior.
            ring = new Region();
        }
        var doorWanted = new HashSet<Cell>(input.Plan.ShellDoors(r));
        var (wallDef, doorDef) = r.RingDefs();
        var (walls, doors) = input.Plan.Ring(r, input.Ground);
        // The vents the plan stands in this ring, by cell (#2962).
        var vents = new Dictionary<Cell, PlannedSite>();
        if (!r.Outdoor)
        {
            foreach (var s in input.Plan.LiveSites(NodeVent))
            {
                if (ring.Contains(s.Origin) && !doorWanted.Contains(s.Origin))
                {
                    vents[s.Origin] = s;
                }
            }
        }
        var items = new List<ReconcileItem>();
        ReconcileItem Add(ReconcileItem it)
        {
            items.Add(it);
            return it;
        }

        // Furniture: match the template, then remove the rest.
        var matched = new bool[input.Furniture.Count];
        var removals = new List<ReconcileItem>();
        var covered = new HashSet<Cell>();
        var rows = input.Rows.OrderBy(row => row.EntityId, StringComparer.Ordinal).ToList();
        var removedRows = new List<ClearanceTarget>();
        var ground = r.GroundRegion();
        var innerWalls = new List<ClearanceTarget>();
        void Cover(ClearanceTarget row)
        {
            foreach (var c in RectCells(new Rectangle(row.Minimum.X, row.Minimum.Z, row.Maximum.X - row.Minimum.X + 1, row.Maximum.Z - row.Minimum.Z + 1)))
            {
                covered.Add(c);
            }
        }

        foreach (var row in rows)
        {
            if (!row.Player || !Overlaps(row, ground))
            {
                continue;
            }
            if (row.Class == "ancient_wall_door")
            {
                if (Overlaps(row, interior))
                {
                    innerWalls.Add(row);
                }
                continue;
            }
            if (row.DefName.StartsWith("Frame_", StringComparison.Ordinal) || StandInBed(row))
            {
                Cover(row);
                continue;
            }
            if (vents.ContainsKey(row.Minimum) && row.DefName == ClimateDefs().VentDef)
            {
                Cover(row); // the planned vent stands in the ring: no furniture to remove
                continue;
            }
            var hit = -1;
            for (var i = 0; i < input.Furniture.Count; i++)
            {
                var p = input.Furniture[i];
                if (!matched[i] && p.DefName == row.DefName && p.Minimum == row.Minimum && p.Maximum == row.Maximum)
                {
                    hit = i;
                    break;
                }
            }
            if (hit >= 0)
            {
                matched[hit] = true;
                foreach (var c in input.Furniture[hit].Cells())
                {
                    covered.Add(c);
                }
                continue;
            }
            var kind = OpKind.FurnitureOut;
            if (row.Packable && !row.Designated && row.InUse)
            {
                kind = OpKind.PackInUse;
            }
            else if (row.Packable && !row.Designated)
            {
                kind = OpKind.Pack;
            }
            removals.Add(Add(new ReconcileItem { Kind = kind, Target = row }));
            removedRows.Add(row);
        }
        var foreign = ForeignThings(input, ring, wallDef, doorWanted);
        foreach (var it in foreign.Items)
        {
            removals.Add(Add(it));
        }
        var removalCells = new HashSet<Cell>(foreign.Blocked);
        foreach (var row in removedRows)
        {
            foreach (var c in RectCells(new Rectangle(row.Minimum.X, row.Minimum.Z, row.Maximum.X - row.Minimum.X + 1, row.Maximum.Z - row.Minimum.Z + 1)))
            {
                removalCells.Add(c);
            }
        }

        // Ring: walls and doors by cell.
        var ringStart = items.Count;
        var allWallOut = new List<Cell>();
        var ringList = new List<Cell>(ring.Cells());
        ringList.AddRange(input.Plan.AirlockCells(r));
        foreach (var c in ringList)
        {
            if (doorWanted.Contains(c) && doors.Contains(c))
            {
                continue;
            }
            if (doorWanted.Contains(c) && walls.Contains(c))
            {
                allWallOut.Add(c);
                Add(new ReconcileItem { Kind = OpKind.DoorIn, Cell = c });
            }
            else if (doorWanted.Contains(c))
            {
                Add(new ReconcileItem { Kind = OpKind.DoorIn, Cell = c });
            }
            else if (doors.Contains(c))
            {
                Add(new ReconcileItem { Kind = OpKind.DoorOut, Cell = c, Target = RingTarget(rows, c, doorDef) });
            }
            else if (!walls.Contains(c) && !foreign.Claimed.Contains(c))
            {
                Add(new ReconcileItem { Kind = OpKind.WallIn, Cell = c });
            }
        }
        // Wall stuff: one standing wall of a lower ranked stuff is swapped in place, only on a ring with no
        // other work, so enclosure holds at that one cell and the swaps never open two cells (adjacent or
        // not) at once. The placement preview reports a stuff swap safe, so the diff itself finds it.
        if (input.WallUpgrade is not null && !r.Outdoor && items.Count == ringStart)
        {
            foreach (var c in ring.Cells())
            {
                var have = input.Ground.Stuff.GetValueOrDefault(c) ?? "";
                if (!covered.Contains(c) && !doorWanted.Contains(c) && input.Ground.Walls.Contains(c) && have.Length != 0 && input.WallUpgrade(have))
                {
                    Add(new ReconcileItem { Kind = OpKind.WallUp, Cell = c, Ready = true });
                    break;
                }
            }
        }
        // An otherwise complete ring never has more than one door gap open.
        var wallOutCells = allWallOut;
        var closed = true;
        var gaps = 0;
        foreach (var c in ringList)
        {
            if (!walls.Contains(c) && !doors.Contains(c))
            {
                if (doorWanted.Contains(c))
                {
                    gaps++;
                }
                else
                {
                    closed = false;
                }
            }
        }
        if (closed)
        {
            wallOutCells = wallOutCells.Take(Math.Min(wallOutCells.Count, Math.Max(0, 1 - gaps))).ToList();
        }
        // Walls standing inside the interior are no part of the ring and come down, but only once the new ring
        // stands: a reshaped room (a column more, an L) is never opened to the weather or walled off from its
        // pawns while its new wall is still owed. Until then they are owed, not ready, and bring no roof off.
        var ringStands = !items.Skip(ringStart).Any(it => it.Kind == OpKind.WallIn || (it.Kind == OpKind.DoorIn && !walls.Contains(it.Cell)));
        var wallTargets = new List<ClearanceTarget>(innerWalls);
        if (!ringStands)
        {
            wallTargets.Clear();
        }
        foreach (var c in wallOutCells)
        {
            wallTargets.Add(RingTarget(rows, c, wallDef));
        }
        // An outdoor ring has no roof to take off.
        List<Cell> roof = [];
        if (wallTargets.Count > 0 && !r.Outdoor)
        {
            roof = EnclosedRoof(wallTargets, [ground], input.Rooms);
        }
        foreach (var c in allWallOut)
        {
            Add(new ReconcileItem { Kind = OpKind.WallOut, Cell = c, Target = RingTarget(rows, c, wallDef), Ready = roof.Count == 0 && wallOutCells.Contains(c) });
        }
        foreach (var w in innerWalls)
        {
            Add(new ReconcileItem { Kind = OpKind.WallOut, Cell = w.Minimum, Target = w, Ready = roof.Count == 0 && ringStands });
        }
        foreach (var c in roof)
        {
            Add(new ReconcileItem { Kind = OpKind.RoofOff, Cell = c, Ready = true });
        }
        // A planned vent goes over the standing wall, never in an empty cell nor over rock; an unresearched
        // vent is an absent option (#2962).
        if (input.VentAvailable)
        {
            foreach (var c in vents.Keys.OrderBy(k => k.X).ThenBy(k => k.Z))
            {
                if (walls.Contains(c) && !input.Ground.Vents.Contains(c) && !input.Ground.Rock.Contains(c))
                {
                    Add(new ReconcileItem { Kind = OpKind.VentIn, Cell = c });
                }
            }
        }
        // The roofed set roofs the room once its ring stands: its interior and the vestibules of its airlocks,
        // whatever the mirror reads open to the sky.
        if (input.Unroofed is { Count: > 0 } && !r.Unfenced() && !r.Outdoor && !r.Role.IsOutdoor() && !input.Plan.Roofed.Empty()
            && !items.Any(it => it.Kind is OpKind.WallIn or OpKind.DoorIn or OpKind.DoorOut or OpKind.WallOut or OpKind.RoofOff))
        {
            var cells = new List<Cell>(r.Region.Cells());
            foreach (var a in input.Plan.ChildrenOfRole(r.Id, PlannedRole.Airlock))
            {
                cells.Add(a.Vestibule());
            }
            foreach (var c in cells)
            {
                if (input.Plan.Roofed.Contains(c) && input.Unroofed.Contains(c))
                {
                    Add(new ReconcileItem { Kind = OpKind.RoofIn, Cell = c, Ready = true });
                }
            }
        }

        // Floors.
        var actual = new Dictionary<Cell, string>();
        foreach (var f in input.Floors)
        {
            actual[f.Cell] = f.DefName;
        }
        var floorOut = new List<ReconcileItem>();
        var floorIn = new List<ReconcileItem>();
        if (input.WantedFloor is not null && !r.Outdoor)
        {
            foreach (var c in interior.Cells())
            {
                var want = input.WantedFloor(c);
                var has = actual.TryGetValue(c, out var have);
                if (want.Length == 0 || covered.Contains(c) || (has && (have == want || (input.FloorKept is not null && input.FloorKept(have!, want)))))
                {
                    continue;
                }
                if (has)
                {
                    floorOut.Add(Add(new ReconcileItem { Kind = OpKind.FloorOut, Cell = c, Floor = new ClearanceFloor { Cell = c, DefName = have! } }));
                }
                floorIn.Add(Add(new ReconcileItem { Kind = OpKind.FloorIn, Cell = c, Floor = new ClearanceFloor { Cell = c, DefName = want } }));
            }
        }

        // Furniture to put in: packed stock first, a piece this pass packs next, else build on site.
        var stock = new Dictionary<string, int>(StringComparer.Ordinal);
        if (input.Stock is not null)
        {
            foreach (var (def, n) in input.Stock)
            {
                stock[def] = n;
            }
        }
        var freed = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var rm in removals)
        {
            if (rm.Kind != OpKind.FurnitureOut && !rm.Foreign)
            {
                freed[rm.Target.DefName] = freed.GetValueOrDefault(rm.Target.DefName) + 1;
            }
        }
        for (var i = 0; i < input.Furniture.Count; i++)
        {
            var p = input.Furniture[i];
            if (matched[i])
            {
                continue;
            }
            if (stock.GetValueOrDefault(p.DefName) > 0)
            {
                stock[p.DefName]--;
                Add(new ReconcileItem { Kind = OpKind.Install, Piece = p });
            }
            else if (freed.GetValueOrDefault(p.DefName) > 0)
            {
                freed[p.DefName]--;
                Add(new ReconcileItem { Kind = OpKind.Install, Piece = p, Fed = true });
            }
            else
            {
                Add(new ReconcileItem { Kind = OpKind.Build, Piece = p });
            }
        }

        // Readiness.
        bool Owed(params OpKind[] kinds) => items.Any(it => kinds.Contains(it.Kind));
        var shellOut = Owed(OpKind.DoorOut, OpKind.WallOut, OpKind.RoofOff);
        var otherRemoval = Owed(OpKind.Pack, OpKind.FurnitureOut);
        var packOwed = Owed(OpKind.Pack, OpKind.PackInUse);
        var floorOutCells = new HashSet<Cell>(floorOut.Select(it => it.Cell));
        var floorInCells = new HashSet<Cell>(floorIn.Select(it => it.Cell));
        foreach (var it in items)
        {
            switch (it.Kind)
            {
                case OpKind.DoorOut:
                    it.Ready = true;
                    break;
                case OpKind.WallIn:
                    it.Ready = !removalCells.Contains(it.Cell);
                    break;
                case OpKind.DoorIn:
                    it.Ready = !walls.Contains(it.Cell) && !removalCells.Contains(it.Cell);
                    break;
                case OpKind.VentIn:
                    it.Ready = !removalCells.Contains(it.Cell);
                    break;
                case OpKind.Pack or OpKind.FurnitureOut:
                    it.Ready = true;
                    break;
                case OpKind.PackInUse:
                    it.Ready = !otherRemoval;
                    break;
                case OpKind.FloorOut:
                    it.Ready = !shellOut && !removalCells.Contains(it.Cell);
                    break;
                case OpKind.FloorIn:
                    it.Ready = !removalCells.Contains(it.Cell) && !floorOutCells.Contains(it.Cell);
                    break;
                case OpKind.Install or OpKind.Build:
                    var ready = true;
                    foreach (var c in it.Piece.Cells())
                    {
                        ready = ready && !removalCells.Contains(c) && !floorOutCells.Contains(c) && !floorInCells.Contains(c);
                    }
                    if (it.Fed && packOwed)
                    {
                        ready = false;
                    }
                    it.Ready = ready;
                    break;
            }
        }
        return GroupItems(items) with { Holds = foreign.Holds };
    }

    /// <summary>
    /// The census row of the wall or door on ring cell c, or a stand-in naming the cell when the census has none.
    /// </summary>
    private static ClearanceTarget RingTarget(IReadOnlyList<ClearanceTarget> rows, Cell c, string def)
    {
        foreach (var row in rows)
        {
            if (row.Class == "ancient_wall_door" && row.Minimum == c && row.Maximum == c)
            {
                return row;
            }
        }
        return new ClearanceTarget { DefName = def, Minimum = c, Maximum = c, Class = "ancient_wall_door", Player = true, EnclosesRoom = true };
    }

    private static Reconciliation GroupItems(List<ReconcileItem> items)
    {
        var owed = new List<Operation>();
        var ready = new List<Operation>();
        foreach (var kind in OpKindsInOrder)
        {
            var all = new OperationBuilder(kind);
            var now = new OperationBuilder(kind);
            foreach (var it in items)
            {
                if (it.Kind != kind)
                {
                    continue;
                }
                all.Add(it);
                if (it.Ready)
                {
                    now.Add(it);
                }
            }
            if (all.Cells.Count + all.Pieces.Count > 0 || all.Targets.Count > 0)
            {
                owed.Add(all.Build());
            }
            if (now.Cells.Count + now.Pieces.Count > 0 || now.Targets.Count > 0)
            {
                ready.Add(now.Build());
            }
        }
        return new Reconciliation { Owed = owed, Ready = ready };
    }

    private sealed class OperationBuilder(OpKind kind)
    {
        public List<Cell> Cells { get; } = [];

        public List<ClearanceTarget> Targets { get; } = [];

        public List<ClearanceFloor> Floors { get; } = [];

        public List<WantedPiece> Pieces { get; } = [];

        public void Add(ReconcileItem it)
        {
            switch (it.Kind)
            {
                case OpKind.Install or OpKind.Build:
                    Pieces.Add(it.Piece);
                    Cells.AddRange(it.Piece.Cells());
                    break;
                case OpKind.Claim or OpKind.Cut or OpKind.HaulOut:
                    Targets.Add(it.Target);
                    Cells.Add(it.Cell);
                    break;
                case OpKind.Pack or OpKind.FurnitureOut or OpKind.PackInUse:
                    Targets.Add(it.Target);
                    Cells.Add(it.Target.Minimum);
                    break;
                case OpKind.DoorOut or OpKind.WallOut:
                    Targets.Add(it.Target);
                    Cells.Add(it.Cell);
                    break;
                case OpKind.FloorOut or OpKind.FloorIn:
                    Floors.Add(it.Floor);
                    Cells.Add(it.Cell);
                    break;
                default:
                    Cells.Add(it.Cell);
                    break;
            }
        }

        public Operation Build() => new() { Kind = kind, Label = KindLabel(kind), Cells = Cells, Targets = Targets, Floors = Floors, Pieces = Pieces };
    }

    private static GroundPhase KindLabel(OpKind k) => k switch
    {
        OpKind.Pack or OpKind.PackInUse => GroundPhase.Pack,
        OpKind.DoorOut or OpKind.DoorIn => GroundPhase.Doors,
        OpKind.WallOut or OpKind.WallIn or OpKind.RoofOff or OpKind.Claim => GroundPhase.Walls,
        OpKind.FloorOut or OpKind.FloorIn => GroundPhase.Floors,
        _ => GroundPhase.Furniture,
    };
}
