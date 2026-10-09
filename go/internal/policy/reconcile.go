package policy

import (
	"slices"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Plan-vs-ground reconciliation: the operations a
// PlannedRoom still owes, from a per-cell diff of the wanted ring, floor and
// furniture against what stands. A room's state is whatever the diff leaves;
// the old clearance phases (GroundPhase) label operations, they are not stages.
//
// The dependency table is fixed, per cell where cells interact:
//
//	roof off -> wall out -> door in
//	pack or furniture out on a cell -> wall in, floor out, floor in, install, build there
//	floor out -> floor in -> install, build, on the same cells
//
// A floor in waits on its own cell's clearing only, never on the ring.
//
// Furniture standing as wanted is no operation and orders against nothing, and
// the floor under it is left. In-use pieces are packed after every other
// removal. An otherwise complete ring never has more than one door gap open,
// and a door the plan lacks is swapped for a wall in place. Whether an
// operation may queue ahead of its prerequisite is the placement preview's call
// at runtime, so the table gates only what the ground itself forbids.

// OpKind is one kind of work a reconciliation owes; the ready set batches by kind.
type OpKind string

const (
	OpRoofOff      OpKind = "roof_off"
	OpDoorOut      OpKind = "door_out" // a ring door swapped for a wall in place
	OpWallOut      OpKind = "wall_out"
	OpWallIn       OpKind = "wall_in"
	OpWallUp       OpKind = "wall_up" // a standing wall swapped in place for a better stuff
	OpDoorIn       OpKind = "door_in"
	OpPack         OpKind = "pack"
	OpFurnitureOut OpKind = "furniture_out" // deconstruct what cannot pack
	OpPackInUse    OpKind = "pack_in_use"
	OpFloorOut     OpKind = "floor_out"
	OpFloorIn      OpKind = "floor_in"
	OpInstall      OpKind = "install"
	OpBuild        OpKind = "build"
	// The foreign-thing kinds: claim a ruin as ring wall, cut an
	// impassable plant, move a haulable item off the ground.
	OpClaim        OpKind = "claim"
	OpCut          OpKind = "cut"
	OpHaulOut      OpKind = "haul_out"
	opKindsInOrder        = "roof_off door_out wall_out claim cut haul_out wall_in wall_up door_in pack furniture_out pack_in_use floor_out floor_in install build"
)

// WantedPiece is one piece of the room's furniture template: a def and the
// cells its footprint holds (Minimum to Maximum, inclusive).
type WantedPiece struct {
	DefName          string
	Minimum, Maximum domain.Cell
	// Slot, Size and Rot place a piece the room does not hold yet: its template
	// slot, the def's native size at rotation North and its rotation. A piece
	// that stands needs none.
	Slot string
	Size domain.Cell
	Rot  domain.Rotation
}

// Anchor is the cell a native order names for the piece's footprint.
func (p WantedPiece) Anchor() domain.Cell {
	return AnchorForRect(Rectangle{X: p.Minimum.X, Z: p.Minimum.Z, Width: p.Maximum.X - p.Minimum.X + 1, Height: p.Maximum.Z - p.Minimum.Z + 1}, p.Size, p.Rot)
}

func (p WantedPiece) cells() []domain.Cell {
	return rectCells(Rectangle{X: p.Minimum.X, Z: p.Minimum.Z, Width: p.Maximum.X - p.Minimum.X + 1, Height: p.Maximum.Z - p.Minimum.Z + 1})
}

// Operation is one batch of work of one kind: its cells and, by kind, the
// buildings it takes down (Targets), the floors it removes or lays (Floors, the
// def being the actual or the wanted one) or the pieces it installs or builds.
type Operation struct {
	Kind    OpKind
	Label   GroundPhase
	Cells   []domain.Cell
	Targets []ClearanceTarget
	Floors  []ClearanceFloor
	Pieces  []WantedPiece
}

// ReconcileInput is one PlannedRoom against the ground.
type ReconcileInput struct {
	Plan LayoutPlan
	Room PlannedRoom
	// Ground is the colony census's walls and doors (GroundOf).
	Ground GroundCensus
	// Rows are the census's player buildings on the room's ground and Floors
	// its constructed floors, as planned-ground clearance reads them.
	Rows   []ClearanceTarget
	Floors []ClearanceFloor
	Rooms  RoomObservation
	// Furniture is the wanted template; WantedFloor the wanted floor def per
	// interior cell ("" leaves the cell's floor alone).
	Furniture   []WantedPiece
	WantedFloor func(domain.Cell) string
	// FloorKept says an existing constructed floor stands in for the wanted
	// one (FloorKept); nil keeps only the exact def. A floor that stands
	// in is no operation: no tear-up for a different adequate floor.
	FloorKept func(have, want string) bool
	// WallUpgrade says a standing wall of stuff have is replaced in place by the
	// wanted stuff: the wanted stuff ranks strictly above it in the
	// ladder and is in stock. Nil never swaps a wall. A wall whose stuff the
	// census does not name is left.
	WallUpgrade func(have string) bool
	// Cells are the mirror's cells on the room's ground: their foreign things
	// are cleared, claimed or held by the obstruction policy. Nil sees
	// none.
	Cells []SiteCell
	// Stock counts the packed pieces in storage by def.
	Stock map[string]int
}

// Reconciliation is what a room owes (Owed) and the part of it whose
// prerequisites hold now (Ready): one operation per kind, in table order.
type Reconciliation struct {
	Owed, Ready []Operation
	// Holds are the foreign things left standing, with the reason.
	Holds []ReconcileHold
}

type reconcileItem struct {
	kind   OpKind
	cell   domain.Cell
	target ClearanceTarget
	floor  ClearanceFloor
	piece  WantedPiece
	fed    bool // an install fed by a piece this pass packs
	// foreign is a thing that is not the colony's: it feeds no install.
	foreign bool
	ready   bool
}

// Reconcile diffs the room against the ground.
func Reconcile(in ReconcileInput) Reconciliation {
	r := in.Room
	ring, interior := roomWalls(r), r.Interior
	doorWanted := map[domain.Cell]bool{}
	for _, d := range in.Plan.ShellDoors(r) {
		doorWanted[d] = true
	}
	wallDef, doorDef := r.RingDefs()
	walls, doors := in.Plan.ring(r, in.Ground)
	var items []*reconcileItem
	add := func(it reconcileItem) *reconcileItem { items = append(items, &it); return items[len(items)-1] }

	// Furniture: match the template, then remove the rest.
	matched := make([]bool, len(in.Furniture))
	var removals []*reconcileItem
	covered := map[domain.Cell]bool{}
	rows := append([]ClearanceTarget(nil), in.Rows...)
	sort.Slice(rows, func(i, j int) bool { return rows[i].EntityID < rows[j].EntityID })
	var removedRows []ClearanceTarget
	ground := roomGround(interior)
	var innerWalls []ClearanceTarget
	cover := func(row ClearanceTarget) {
		for _, c := range rectCells(Rectangle{X: row.Minimum.X, Z: row.Minimum.Z, Width: row.Maximum.X - row.Minimum.X + 1, Height: row.Maximum.Z - row.Minimum.Z + 1}) {
			covered[c] = true
		}
	}
	for _, row := range rows {
		if !row.Player || !overlaps(row, ground) {
			continue
		}
		if row.Class == "ancient_wall_door" {
			if overlaps(row, interior) {
				innerWalls = append(innerWalls, row)
			}
			continue
		}
		if strings.HasPrefix(row.DefName, "Frame_") || standInBed(row) {
			cover(row)
			continue
		}
		hit := -1
		for i, p := range in.Furniture {
			if !matched[i] && p.DefName == row.DefName && p.Minimum == row.Minimum && p.Maximum == row.Maximum {
				hit = i
				break
			}
		}
		if hit >= 0 {
			matched[hit] = true
			for _, c := range in.Furniture[hit].cells() {
				covered[c] = true
			}
			continue
		}
		kind := OpFurnitureOut
		switch {
		case row.Packable && !row.Designated && row.InUse:
			kind = OpPackInUse
		case row.Packable && !row.Designated:
			kind = OpPack
		}
		removals = append(removals, add(reconcileItem{kind: kind, target: row}))
		removedRows = append(removedRows, row)
	}
	foreign := foreignThings(in, ring, wallDef, doorWanted)
	for i := range foreign.items {
		removals = append(removals, add(foreign.items[i]))
	}
	removalCells := map[domain.Cell]bool{}
	for c := range foreign.blocked {
		removalCells[c] = true
	}
	for _, row := range removedRows {
		for _, c := range rectCells(Rectangle{X: row.Minimum.X, Z: row.Minimum.Z, Width: row.Maximum.X - row.Minimum.X + 1, Height: row.Maximum.Z - row.Minimum.Z + 1}) {
			removalCells[c] = true
		}
	}

	// Ring: walls and doors by cell.
	ringStart := len(items)
	var allWallOut []domain.Cell
	for _, c := range rectCells(ring) {
		if !onRing(c, ring) {
			continue
		}
		switch {
		case doorWanted[c] && doors[c]:
		case doorWanted[c] && walls[c]:
			allWallOut = append(allWallOut, c)
			add(reconcileItem{kind: OpDoorIn, cell: c})
		case doorWanted[c]:
			add(reconcileItem{kind: OpDoorIn, cell: c})
		case doors[c]:
			add(reconcileItem{kind: OpDoorOut, cell: c, target: ringTarget(rows, c, doorDef)})
		case !walls[c] && !foreign.claimed[c]:
			add(reconcileItem{kind: OpWallIn, cell: c})
		}
	}
	// Wall stuff: one standing wall of a lower ranked stuff is swapped in place,
	// only on a ring with no other work, so enclosure holds at that one cell and
	// the swaps never open two cells (adjacent or not) at once. The placement
	// preview reports a stuff swap safe, so the diff itself finds it.
	if in.WallUpgrade != nil && !r.Outdoor && len(items) == ringStart {
		for _, c := range rectCells(ring) {
			if have := in.Ground.stuff[c]; onRing(c, ring) && !covered[c] && !doorWanted[c] && in.Ground.walls[c] && have != "" && in.WallUpgrade(have) {
				add(reconcileItem{kind: OpWallUp, cell: c, ready: true})
				break
			}
		}
	}
	// An otherwise complete ring never has more than one door gap open.
	wallOutCells, closed, gaps := allWallOut, true, 0
	for _, c := range rectCells(ring) {
		if onRing(c, ring) && !walls[c] && !doors[c] {
			if doorWanted[c] {
				gaps++
			} else {
				closed = false
			}
		}
	}
	if closed {
		wallOutCells = wallOutCells[:min(len(wallOutCells), max(0, 1-gaps))]
	}
	// Walls standing inside the interior are no part of the ring and come down.
	wallTargets := slices.Clone(innerWalls)
	for _, c := range wallOutCells {
		wallTargets = append(wallTargets, ringTarget(rows, c, wallDef))
	}
	// An outdoor ring has no roof to take off.
	var roof []domain.Cell
	if len(wallTargets) > 0 && !r.Outdoor {
		roof = enclosedRoof(wallTargets, []Rectangle{roomGround(interior)}, in.Rooms)
	}
	for _, c := range allWallOut {
		add(reconcileItem{kind: OpWallOut, cell: c, target: ringTarget(rows, c, wallDef), ready: len(roof) == 0 && slices.Contains(wallOutCells, c)})
	}
	for _, w := range innerWalls {
		add(reconcileItem{kind: OpWallOut, cell: w.Minimum, target: w, ready: len(roof) == 0})
	}
	for _, c := range roof {
		add(reconcileItem{kind: OpRoofOff, cell: c, ready: true})
	}

	// Floors.
	actual := map[domain.Cell]string{}
	for _, f := range in.Floors {
		actual[f.Cell] = f.DefName
	}
	var floorOut, floorIn []*reconcileItem
	if in.WantedFloor != nil && !r.Outdoor {
		for _, c := range rectCells(interior) {
			want := in.WantedFloor(c)
			have, has := actual[c]
			if want == "" || covered[c] || has && (have == want || in.FloorKept != nil && in.FloorKept(have, want)) {
				continue
			}
			if has {
				floorOut = append(floorOut, add(reconcileItem{kind: OpFloorOut, cell: c, floor: ClearanceFloor{Cell: c, DefName: have}}))
			}
			floorIn = append(floorIn, add(reconcileItem{kind: OpFloorIn, cell: c, floor: ClearanceFloor{Cell: c, DefName: want}}))
		}
	}

	// Furniture to put in: packed stock first, a piece this pass packs next,
	// else build on site.
	stock := map[string]int{}
	for def, n := range in.Stock {
		stock[def] = n
	}
	freed := map[string]int{}
	for _, rm := range removals {
		if rm.kind != OpFurnitureOut && !rm.foreign {
			freed[rm.target.DefName]++
		}
	}
	for i, p := range in.Furniture {
		if matched[i] {
			continue
		}
		switch {
		case stock[p.DefName] > 0:
			stock[p.DefName]--
			add(reconcileItem{kind: OpInstall, piece: p})
		case freed[p.DefName] > 0:
			freed[p.DefName]--
			add(reconcileItem{kind: OpInstall, piece: p, fed: true})
		default:
			add(reconcileItem{kind: OpBuild, piece: p})
		}
	}

	// Readiness.
	owed := func(kinds ...OpKind) bool {
		for _, it := range items {
			for _, k := range kinds {
				if it.kind == k {
					return true
				}
			}
		}
		return false
	}
	shellOut := owed(OpDoorOut, OpWallOut, OpRoofOff)
	otherRemoval := owed(OpPack, OpFurnitureOut)
	packOwed := owed(OpPack, OpPackInUse)
	floorOutCells, floorInCells := map[domain.Cell]bool{}, map[domain.Cell]bool{}
	for _, it := range floorOut {
		floorOutCells[it.cell] = true
	}
	for _, it := range floorIn {
		floorInCells[it.cell] = true
	}
	for _, it := range items {
		switch it.kind {
		case OpDoorOut:
			it.ready = true
		case OpWallIn:
			it.ready = !removalCells[it.cell]
		case OpDoorIn:
			it.ready = !walls[it.cell] && !removalCells[it.cell]
		case OpPack, OpFurnitureOut:
			it.ready = true
		case OpPackInUse:
			it.ready = !otherRemoval
		case OpFloorOut:
			it.ready = !shellOut && !removalCells[it.cell]
		case OpFloorIn:
			it.ready = !removalCells[it.cell] && !floorOutCells[it.cell]
		case OpInstall, OpBuild:
			ready := true
			for _, c := range it.piece.cells() {
				ready = ready && !removalCells[c] && !floorOutCells[c] && !floorInCells[c]
			}
			if it.fed && packOwed {
				ready = false
			}
			it.ready = ready
		}
	}
	out := groupItems(items)
	out.Holds = foreign.holds
	return out
}

// ringTarget is the census row of the wall or door on ring cell c, or a stand-in
// naming the cell when the census has none.
func ringTarget(rows []ClearanceTarget, c domain.Cell, def string) ClearanceTarget {
	for _, row := range rows {
		if row.Class == "ancient_wall_door" && row.Minimum == c && row.Maximum == c {
			return row
		}
	}
	return ClearanceTarget{DefName: def, Minimum: c, Maximum: c, Class: "ancient_wall_door", Player: true, EnclosesRoom: true}
}

func groupItems(items []*reconcileItem) Reconciliation {
	var out Reconciliation
	for _, kind := range strings.Fields(opKindsInOrder) {
		var all, ready Operation
		all.Kind, ready.Kind = OpKind(kind), OpKind(kind)
		all.Label, ready.Label = kindLabel(OpKind(kind)), kindLabel(OpKind(kind))
		for _, it := range items {
			if string(it.kind) != kind {
				continue
			}
			addToOp(&all, it)
			if it.ready {
				addToOp(&ready, it)
			}
		}
		if len(all.Cells)+len(all.Pieces) > 0 || len(all.Targets) > 0 {
			out.Owed = append(out.Owed, all)
		}
		if len(ready.Cells)+len(ready.Pieces) > 0 || len(ready.Targets) > 0 {
			out.Ready = append(out.Ready, ready)
		}
	}
	return out
}

func addToOp(op *Operation, it *reconcileItem) {
	switch it.kind {
	case OpInstall, OpBuild:
		op.Pieces = append(op.Pieces, it.piece)
		op.Cells = append(op.Cells, it.piece.cells()...)
	case OpClaim, OpCut, OpHaulOut:
		op.Targets = append(op.Targets, it.target)
		op.Cells = append(op.Cells, it.cell)
	case OpPack, OpFurnitureOut, OpPackInUse:
		op.Targets = append(op.Targets, it.target)
		op.Cells = append(op.Cells, it.target.Minimum)
	case OpDoorOut, OpWallOut:
		op.Targets = append(op.Targets, it.target)
		op.Cells = append(op.Cells, it.cell)
	case OpFloorOut, OpFloorIn:
		op.Floors = append(op.Floors, it.floor)
		op.Cells = append(op.Cells, it.cell)
	default:
		op.Cells = append(op.Cells, it.cell)
	}
}

func kindLabel(k OpKind) GroundPhase {
	switch k {
	case OpPack, OpPackInUse:
		return GroundPack
	case OpDoorOut, OpDoorIn:
		return GroundDoors
	case OpWallOut, OpWallIn, OpRoofOff, OpClaim:
		return GroundWalls
	case OpFloorOut, OpFloorIn:
		return GroundFloors
	}
	return GroundFurniture
}
