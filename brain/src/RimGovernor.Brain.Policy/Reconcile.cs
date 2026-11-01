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

    /// <summary>
    /// A chosen Relocate: the standing piece reinstalled at its planned slot in one native order (the game's Reinstall blueprint,
    /// GenConstruct.PlaceBlueprintForReinstall), which uninstalls, carries and installs it. Targets[i] moves to Pieces[i].
    /// </summary>
    [WireName("reinstall")]
    Reinstall,

    [WireName("floor_out")]
    FloorOut,

    [WireName("floor_in")]
    FloorIn,

    [WireName("install")]
    Install,

    [WireName("build")]
    Build,

    // The foreign-thing kinds: claim a ruin as ring wall or cut an impassable plant.
    [WireName("claim")]
    Claim,

    [WireName("cut")]
    Cut,

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
    /// Size and Rot place a piece the room does not hold yet: the def's native size at rotation North and its
    /// rotation. A piece that stands needs neither.
    /// </summary>
    public Cell Size { get; init; }

    public Rotation Rot { get; init; }

    /// <summary>
    /// The material the owner chose for a piece it builds (a replacement bed's, never worse than the bed it
    /// replaces); empty for the build side's ordinary choice. A piece with a stuff is built to it, never
    /// installed from stock, whose stuff is not the owner's choice.
    /// </summary>
    public string Stuff { get; init; } = "";

    private Rectangle Footprint => new(Minimum.X, Minimum.Z, Maximum.X - Minimum.X + 1, Maximum.Z - Minimum.Z + 1);

    /// <summary>The cell a native order names for the piece's footprint.</summary>
    public Cell Anchor() => InteriorRules.AnchorForRect(Footprint, Size, Rot);

    internal List<Cell> Cells() => PlannedRules.RectCells(Footprint);
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

    public required RoomObservation Rooms { get; init; }

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
    /// Says a standing ring wall of stuff have is owed swapped in place (#3657): the planned sites ask for every
    /// standing wall, which they price per ring stuff, and the room reconcile for those not in the stuff of the
    /// room's selected wall upgrade. Null never swaps a wall: a wall stands in the stuff it was built in. A wall
    /// whose stuff the census does not name is left.
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

    /// <summary>
    /// The transition facts an unlisted standing piece is compared by (task 8.1, ServicingChoiceRules.Transition); null while
    /// unread, when a piece the plan builds over is held and any other stays.
    /// </summary>
    public TransitionFacts? Transitions { get; init; }

    /// <summary>The definitions the room's owner forbids standing in it (a throne room's beds): such a piece cannot be retained.</summary>
    public Func<string, bool>? Forbidden { get; init; }

    /// <summary>
    /// The other planned rooms' unfilled pieces (OpenSlotsOf): slots an unlisted minifiable piece here could be relocated to when
    /// this room has none of its def open. A piece chosen to relocate to one is reinstalled there in one native order (OpKind.Reinstall)
    /// by this room's reconcile; the slot's own room sees it filled once the census shows it standing there.
    /// </summary>
    public IReadOnlyList<WantedPiece> OpenSlots { get; init; } = [];
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

    /// <summary>The transition decisions of the unlisted pieces, bound or compared: what a committed step binds into the plan.</summary>
    public IReadOnlyList<TransitionDecision> Transitions { get; init; } = [];

    /// <summary>The Relocate destination offered to each unlisted piece, by id (RelocationSlots); a piece with no open slot has none.</summary>
    public IReadOnlyDictionary<string, TransitionDestination> Destinations { get; init; } = new Dictionary<string, TransitionDestination>(StringComparer.Ordinal);
}

internal sealed class ReconcileItem
{
    public OpKind Kind { get; set; }

    public Cell Cell { get; set; }

    public ClearanceTarget Target { get; set; } = new();

    /// <summary>The floor a FloorOut or FloorIn item moves; an empty floor on every other kind.</summary>
    public ClearanceFloor Floor { get; set; } = new() { Cell = default, DefName = "", Designated = false };

    public WantedPiece Piece { get; set; } = new();

    /// <summary>An install fed by a piece this pass packs.</summary>
    public bool Fed { get; set; }

    /// <summary>A Reinstall onto one of this room's own slots (it waits on the room's ring); false for another room's slot.</summary>
    public bool Here { get; set; }

    /// <summary>A thing that is not the colony's: it feeds no install.</summary>
    public bool Foreign { get; set; }

    public bool Ready { get; set; }
}

public static partial class ReconcileRules
{
    private static readonly OpKind[] OpKindsInOrder =
    [
        OpKind.RoofOff, OpKind.DoorOut, OpKind.WallOut, OpKind.Claim, OpKind.Cut, OpKind.WallIn, OpKind.WallUp,
        OpKind.DoorIn, OpKind.Pack, OpKind.FurnitureOut, OpKind.PackInUse, OpKind.Reinstall, OpKind.FloorOut, OpKind.FloorIn, OpKind.Install,
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
            foreach (var s in input.Plan.LiveSites(LayoutTileVentRules.NodeVent))
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
            foreach (var c in PlannedRules.RectCells(new Rectangle(row.Minimum.X, row.Minimum.Z, row.Maximum.X - row.Minimum.X + 1, row.Maximum.Z - row.Minimum.Z + 1)))
            {
                covered.Add(c);
            }
        }

        // The cells the plan builds on: every wanted piece, the ring and the airlocks.
        var planned = new HashSet<Cell>(ring.Cells());
        planned.UnionWith(input.Plan.AirlockCells(r));
        foreach (var p in input.Furniture)
        {
            planned.UnionWith(p.Cells());
        }
        var transitionHolds = new List<ReconcileHold>();
        var decisions = new List<TransitionDecision>();
        var heldCells = new HashSet<Cell>();
        var unlisted = new List<ClearanceTarget>();
        foreach (var row in rows)
        {
            if (!row.Player || !ClearanceGroundRules.Overlaps(row, ground))
            {
                continue;
            }
            if (row.Class == "ancient_wall_door")
            {
                if (ClearanceGroundRules.Overlaps(row, interior))
                {
                    innerWalls.Add(row);
                }
                continue;
            }
            if (row.DefName.StartsWith("Frame_", StringComparison.Ordinal) || ClearanceGroundRules.StandInBed(row))
            {
                Cover(row);
                continue;
            }
            if (vents.ContainsKey(row.Minimum) && row.DefName == ClimateRules.ClimateDefs().VentDef)
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
            // A standing deconstruct designation is already ordered, whoever placed it: the row comes down as designated.
            if (row.Designated)
            {
                removals.Add(Add(new ReconcileItem { Kind = OpKind.FurnitureOut, Target = row }));
                removedRows.Add(row);
                continue;
            }
            unlisted.Add(row);
        }
        // The unfilled slots an unlisted piece of the same def could move into: this room's first, then the other rooms'.
        var open = new List<WantedPiece>();
        for (var i = 0; i < input.Furniture.Count; i++)
        {
            if (!matched[i] && input.Furniture[i].Stuff.Length == 0)
            {
                open.Add(input.Furniture[i]);
            }
        }
        var destinations = RelocationSlots(unlisted, open, input.OpenSlots, input.Transitions);
        foreach (var row in unlisted)
        {
            // An unlisted piece is only proposed: the transition choice decides whether it stays, moves, is stored or removed. It
            // cannot stay where the plan builds (a wanted piece, the ring, an airlock) or where the room's owner forbids it; the
            // furnishing's omission alone never takes it down (task 8.3), and a removal waits on its replacement's observed service.
            var service = input.Transitions?.Standing?.Invoke(row);
            var decision = ServicingChoiceRules.Choose(new(row.EntityId, row.DefName, InteriorFurniture(r, row) ? TransitionCause.StaleInterior : TransitionCause.PlannedGround)
            {
                RetainExcluded = RowCells(row).Any(planned.Contains) ? FootprintPlanned : input.Forbidden?.Invoke(row.DefName) == true ? ForbiddenInRoom : null,
                Service = service,
                Destination = destinations.GetValueOrDefault(row.EntityId),
            }, input.Transitions);
            decisions.Add(decision);
            if (decision.Admitted && decision.Chosen!.Kind == ServicingChoiceKind.Relocate)
            {
                // A Relocate is one native reinstall onto its slot: the slot is neither built nor installed from stock meanwhile.
                var slot = destinations.GetValueOrDefault(row.EntityId)?.Piece
                    ?? throw new InvalidOperationException("relocate without a slot piece: " + row.EntityId);
                var here = false;
                for (var i = 0; i < input.Furniture.Count; i++)
                {
                    if (ReferenceEquals(input.Furniture[i], slot))
                    {
                        matched[i] = here = true;
                    }
                }
                Add(new ReconcileItem { Kind = OpKind.Reinstall, Target = row, Piece = slot, Here = here });
                removedRows.Add(row);
            }
            else if (decision.Admitted)
            {
                var kind = !decision.Uninstalls ? OpKind.FurnitureOut : row.InUse ? OpKind.PackInUse : OpKind.Pack;
                removals.Add(Add(new ReconcileItem { Kind = kind, Target = row }));
                removedRows.Add(row);
            }
            else if (decision.Hold is { } hold)
            {
                // Nothing could be priced, or the chosen removal waits on its replacement: the piece stays, named, and nothing is
                // built over it.
                transitionHolds.Add(new ReconcileHold { Cell = row.Minimum, Def = row.DefName, Reason = hold });
                heldCells.UnionWith(RowCells(row));
            }
            else
            {
                Cover(row);
            }
        }
        var foreign = ForeignThings(input, ring, wallDef, doorWanted);
        foreach (var it in foreign.Items)
        {
            removals.Add(Add(it));
        }
        var removalCells = new HashSet<Cell>(foreign.Blocked);
        removalCells.UnionWith(heldCells);
        foreach (var row in removedRows)
        {
            foreach (var c in PlannedRules.RectCells(new Rectangle(row.Minimum.X, row.Minimum.Z, row.Maximum.X - row.Minimum.X + 1, row.Maximum.Z - row.Minimum.Z + 1)))
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
                // A standing door of another def than the ring's (a plain door where a barn's animal flap is planned) is owed
                // replaced in place: the ring's door is placed over it (a door's replaceTags let one door's blueprint replace
                // another). A door the census names no def for stands.
                if (!r.Outdoor && input.Ground.DoorDefs.TryGetValue(c, out var standing) && standing != doorDef)
                {
                    Add(new ReconcileItem { Kind = OpKind.DoorIn, Cell = c });
                }
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
        // Wall stuff: on a ring with no other work, every standing wall WallUpgrade names is owed swapped in
        // place (a planned vent's cell is left to the vent), and only the first is ready, so enclosure holds at
        // that one cell and the swaps never open two cells (adjacent or not) at once. The placement preview
        // reports a stuff swap safe, so the diff itself finds it.
        if (input.WallUpgrade is not null && !r.Outdoor && items.Count == ringStart)
        {
            var first = true;
            foreach (var c in ring.Cells())
            {
                var have = input.Ground.Stuff.GetValueOrDefault(c) ?? "";
                if (!covered.Contains(c) && !doorWanted.Contains(c) && !vents.ContainsKey(c) && input.Ground.Walls.Contains(c) && have.Length != 0 && input.WallUpgrade(have))
                {
                    Add(new ReconcileItem { Kind = OpKind.WallUp, Cell = c, Ready = first });
                    first = false;
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
        var ringStands = !items.Skip(ringStart).Any(it => it.Kind == OpKind.WallIn || (it.Kind == OpKind.DoorIn && !walls.Contains(it.Cell) && !doors.Contains(it.Cell)));
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
            roof = ClearanceGroundRules.EnclosedRoof(wallTargets, [ground], input.Rooms);
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
                    floorOut.Add(Add(new ReconcileItem { Kind = OpKind.FloorOut, Cell = c, Floor = new ClearanceFloor { Cell = c, DefName = have!, Designated = false } }));
                }
                floorIn.Add(Add(new ReconcileItem { Kind = OpKind.FloorIn, Cell = c, Floor = new ClearanceFloor { Cell = c, DefName = want, Designated = false } }));
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
            // A slot the plan already relocates another room's piece into (a bound Relocate, TransitionBindingRules.Holds) is that
            // piece's reinstall destination: neither built nor installed from stock meanwhile, held naming the piece.
            if (input.Transitions?.Planned.FirstOrDefault(t => t.Kind == ServicingChoiceKind.Relocate && t.Slot?.Minimum == p.Minimum && t.Definition == p.DefName) is { } relocation)
            {
                transitionHolds.Add(new ReconcileHold { Cell = p.Minimum, Def = p.DefName, Reason = SlotBoundToRelocation + ":" + relocation.Target });
                continue;
            }
            if (p.Stuff.Length > 0)
            {
                Add(new ReconcileItem { Kind = OpKind.Build, Piece = p });
            }
            else if (stock.GetValueOrDefault(p.DefName) > 0)
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
        // Ring first, every role: furniture waits while the room still owes a wall or a door, so a piece never
        // stands in an unenclosed room (#3743). A piece already standing is matched above and never removed.
        var ringOwed = Owed(OpKind.WallIn, OpKind.DoorIn);
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
                case OpKind.Reinstall:
                    // The slot clear of every removal and floor work but the piece's own cells, which it vacates itself.
                    var own = RowCells(it.Target).ToHashSet();
                    it.Ready = (!it.Here || !ringOwed) && it.Piece.Cells().All(c => own.Contains(c)
                        || (!removalCells.Contains(c) && !floorOutCells.Contains(c) && !floorInCells.Contains(c)));
                    break;
                case OpKind.Install or OpKind.Build:
                    var ready = !ringOwed;
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
        return GroupItems(items) with { Holds = [.. foreign.Holds, .. transitionHolds], Transitions = decisions, Destinations = destinations };
    }

    /// <summary>The retain exclusion of an unlisted piece standing where the plan builds.</summary>
    public const string FootprintPlanned = "footprint_planned";

    /// <summary>The retain exclusion of an unlisted piece the room's owner forbids.</summary>
    public const string ForbiddenInRoom = "forbidden_in_room";

    /// <summary>The hold of an open slot a bound Relocate from another room is moving its piece into: <c>slot_bound_to_relocation:&lt;target&gt;</c>.</summary>
    public const string SlotBoundToRelocation = "slot_bound_to_relocation";

    /// <summary>
    /// The Relocate destination proposer (task 8.1): each unlisted minifiable piece, in id order, takes the nearest unfilled slot of
    /// its def that no earlier piece took (by octile distance from its footprint's minimum to the slot's, then the slot's minimum
    /// Z and X): this room's own slots (<paramref name="here"/>, filled through Pack and a fed Install) before the other planned
    /// rooms' (<paramref name="elsewhere"/>, filled from packed stock). The haul is the worker's walk from the piece to the slot as
    /// the game's pathfinder read it (TransitionFacts.Hauls, the haul probe); unread naming <c>haul_unprobed</c> while that leg is
    /// not read (<see cref="UnprobedHauls"/> names it), and <c>no_worker</c> while no worker is read. The avoided build is the
    /// slot's build price (TransitionFacts.BuildPrice). A piece the plan already relocates keeps its bound slot first while it is open.
    /// A piece with no open slot of its def gets none (no_destination).
    /// </summary>
    public static Dictionary<string, TransitionDestination> RelocationSlots(IReadOnlyList<ClearanceTarget> pieces, IReadOnlyList<WantedPiece> here, IReadOnlyList<WantedPiece> elsewhere, TransitionFacts? transitions)
    {
        var output = new Dictionary<string, TransitionDestination>(StringComparer.Ordinal);
        var taken = new HashSet<WantedPiece>(ReferenceEqualityComparer.Instance);
        foreach (var row in pieces.OrderBy(p => p.EntityId, StringComparer.Ordinal))
        {
            if (transitions?.Read?.Transitions.FirstOrDefault(m => m.Id == row.EntityId) is not { Minifiable: true } m)
            {
                continue;
            }
            // A bound Relocate keeps its slot while that slot is still open (TransitionBindingRules.Holds).
            var bound = transitions.Planned.FirstOrDefault(t => t.Target == row.EntityId && t.Kind == ServicingChoiceKind.Relocate)?.Slot?.Minimum;
            WantedPiece? Nearest(IReadOnlyList<WantedPiece> slots) => slots.Where(s => s.DefName == row.DefName && s.Stuff.Length == 0 && !taken.Contains(s))
                .OrderBy(s => s.Minimum == bound ? 0 : 1).ThenBy(s => Octile(row.Minimum, s.Minimum)).ThenBy(s => s.Minimum.Z).ThenBy(s => s.Minimum.X).FirstOrDefault();
            if ((Nearest(here) ?? Nearest(elsewhere)) is not { } slot)
            {
                continue;
            }
            taken.Add(slot);
            RimGovernor.Brain.GameFacts.Observations.TransitionLeg haul = m.Worker is null ? new RimGovernor.Brain.GameFacts.Observations.TransitionLeg.Unread(ServicingRules.NoWorker)
                : transitions.Hauls?.Invoke(row.EntityId, slot.Minimum) ?? new RimGovernor.Brain.GameFacts.Observations.TransitionLeg.Unread(ServicingRules.HaulUnprobed);
            output[row.EntityId] = new(haul, transitions.BuildPrice?.Invoke(slot) ?? new TransitionBuild.Unread("build price:" + slot.DefName)) { Slot = slot.Minimum, Piece = slot, From = row.Minimum, Worker = m.Worker?.Pawn };
        }
        return output;
    }

    /// <summary>
    /// The relocation hauls <see cref="Reconcile"/> at <paramref name="input"/> would price had the game walked them (task 8.1):
    /// each destination whose haul is unread for want of a probe (<see cref="ServicingRules.HaulUnprobed"/>) and has a worker,
    /// in id order, as the haul probe's legs. A reader asks the game for them (observations_read_haul_probe) and reconciles again
    /// with <see cref="TransitionFacts.Hauls"/> answering, as the servicing read collects its legs and runs again.
    /// </summary>
    public static IReadOnlyList<RimGovernor.Brain.GameOrders.Observations.HaulProbeLeg> UnprobedHauls(ReconcileInput input) =>
        [.. Reconcile(input).Destinations
            .Where(d => d.Value.Haul is RimGovernor.Brain.GameFacts.Observations.TransitionLeg.Unread { Fact: ServicingRules.HaulUnprobed } && d.Value.Worker is not null)
            .OrderBy(d => d.Key, StringComparer.Ordinal)
            .Select(d => new RimGovernor.Brain.GameOrders.Observations.HaulProbeLeg(d.Key, d.Value.Worker!, d.Value.From, d.Value.Slot))];

    // Cells walked between two cells with diagonal steps at TicksPerMoveDiagonal's 1.41421 cardinal steps: the nearest-slot
    // order, not a price (the haul is the probe's). The 1.41421 is Pawn.TicksPerMove's diagonal factor (kept-constants.md,
    // "Mirrored game literals: transitions").
    private static double Octile(Cell a, Cell b)
    {
        var (dx, dz) = (Math.Abs(a.X - b.X), Math.Abs(a.Z - b.Z));
        return Math.Max(dx, dz) + ((1.41421 - 1) * Math.Min(dx, dz));
    }

    /// <summary>
    /// The other planned rooms' unfilled pieces (ReconcileInput.OpenSlots): every piece the plan places in a room other than
    /// <paramref name="room"/> where no building of its def stands inside its footprint (<paramref name="standing"/>, by def and position).
    /// </summary>
    public static List<WantedPiece> OpenSlotsOf(LayoutPlan plan, int room, IReadOnlyList<(string Def, Cell Cell)> standing)
    {
        var output = new List<WantedPiece>();
        foreach (var other in plan.Rooms.Where(o => o.Id != room).OrderBy(o => o.Id))
        {
            foreach (var piece in plan.Pieces.GetValueOrDefault(other.Id) ?? [])
            {
                var wanted = piece.Wanted();
                if (!standing.Any(b => b.Def == wanted.DefName && b.Cell.X >= wanted.Minimum.X && b.Cell.X <= wanted.Maximum.X && b.Cell.Z >= wanted.Minimum.Z && b.Cell.Z <= wanted.Maximum.Z))
                {
                    output.Add(wanted);
                }
            }
        }
        return output;
    }

    private static List<Cell> RowCells(ClearanceTarget row) =>
        PlannedRules.RectCells(new Rectangle(row.Minimum.X, row.Minimum.Z, row.Maximum.X - row.Minimum.X + 1, row.Maximum.Z - row.Minimum.Z + 1));

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
                case OpKind.Reinstall:
                    Targets.Add(it.Target);
                    Pieces.Add(it.Piece);
                    Cells.AddRange(it.Piece.Cells());
                    break;
                case OpKind.Claim or OpKind.Cut:
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
                case OpKind.None or OpKind.RoofOff or OpKind.WallIn or OpKind.WallUp or OpKind.DoorIn or OpKind.VentIn or OpKind.RoofIn:
                    Cells.Add(it.Cell);
                    break;
            }
        }

        public Operation Build() => new() { Kind = kind, Label = KindLabel(kind), Cells = Cells, Targets = Targets, Floors = Floors, Pieces = Pieces };
    }

    internal static GroundPhase KindLabel(OpKind k) => k switch
    {
        OpKind.Pack or OpKind.PackInUse => GroundPhase.Pack,
        OpKind.DoorOut or OpKind.DoorIn => GroundPhase.Doors,
        OpKind.WallOut or OpKind.WallIn or OpKind.RoofOff or OpKind.Claim => GroundPhase.Walls,
        OpKind.FloorOut or OpKind.FloorIn => GroundPhase.Floors,
        OpKind.None or OpKind.WallUp or OpKind.FurnitureOut or OpKind.Reinstall or OpKind.Install or OpKind.Build or OpKind.Cut or OpKind.VentIn or OpKind.RoofIn => GroundPhase.Furniture,
        var other => throw new ArgumentOutOfRangeException(nameof(k), other, "undeclared op kind"),
    };
}
