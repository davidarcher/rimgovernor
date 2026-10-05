package policy

import (
	"slices"
	"sort"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Plan-vs-ground reconciliation (#2106, epic #2101): the operations a
// PlannedRoom still owes, from a per-cell diff of the wanted ring, floor and
// furniture against what stands. A room's state is whatever the diff leaves;
// the old clearance phases (GroundPhase) label operations, they are not stages.
//
// The dependency table is fixed, per cell where cells interact:
//
//	roof off -> wall out -> door in
//	shell (wall or door, in or out) -> floor in
//	pack or furniture out on a cell -> floor out, floor in, install, build there
//	floor out -> floor in -> install, build, on the same cells
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
	OpDoorIn       OpKind = "door_in"
	OpPack         OpKind = "pack"
	OpFurnitureOut OpKind = "furniture_out" // deconstruct what cannot pack
	OpPackInUse    OpKind = "pack_in_use"
	OpFloorOut     OpKind = "floor_out"
	OpFloorIn      OpKind = "floor_in"
	OpInstall      OpKind = "install"
	OpBuild        OpKind = "build"
	opKindsInOrder        = "roof_off door_out wall_out wall_in door_in pack furniture_out pack_in_use floor_out floor_in install build"
)

// WantedPiece is one piece of the room's furniture template: a def and the
// cells its footprint holds (Minimum to Maximum, inclusive).
type WantedPiece struct {
	DefName          string
	Minimum, Maximum domain.Cell
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
	// Ground is the colony census's walls and doors (GroundOf, #2105).
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
	// Stock counts the packed pieces in storage by def (#2104).
	Stock map[string]int
}

// Reconciliation is what a room owes (Owed) and the part of it whose
// prerequisites hold now (Ready): one operation per kind, in table order.
type Reconciliation struct{ Owed, Ready []Operation }

type reconcileItem struct {
	kind   OpKind
	cell   domain.Cell
	target ClearanceTarget
	floor  ClearanceFloor
	piece  WantedPiece
	fed    bool // an install fed by a piece this pass packs
	ready  bool
}

// Reconcile diffs the room against the ground.
func Reconcile(in ReconcileInput) Reconciliation {
	r := in.Room
	ring, interior := roomWalls(r), r.Interior
	doorWanted := map[domain.Cell]bool{}
	for _, d := range in.Plan.ShellDoors(r) {
		doorWanted[d] = true
	}
	var items []*reconcileItem
	add := func(it reconcileItem) *reconcileItem { items = append(items, &it); return items[len(items)-1] }

	// Furniture: match the template, then remove the rest.
	matched := make([]bool, len(in.Furniture))
	var removals []*reconcileItem
	covered := map[domain.Cell]bool{}
	rows := append([]ClearanceTarget(nil), in.Rows...)
	sort.Slice(rows, func(i, j int) bool { return rows[i].EntityID < rows[j].EntityID })
	var removedRows []ClearanceTarget
	for _, row := range rows {
		if !row.Player || !overlaps(row, interior) || row.Class == "ancient_wall_door" || strings.HasPrefix(row.DefName, "Frame_") || standInBed(row) {
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
	removalCells := map[domain.Cell]bool{}
	for _, row := range removedRows {
		for _, c := range rectCells(Rectangle{X: row.Minimum.X, Z: row.Minimum.Z, Width: row.Maximum.X - row.Minimum.X + 1, Height: row.Maximum.Z - row.Minimum.Z + 1}) {
			removalCells[c] = true
		}
	}

	// Ring: walls and doors by cell.
	var allWallOut []domain.Cell
	for _, c := range rectCells(ring) {
		if !onRing(c, ring) {
			continue
		}
		g := in.Ground
		switch {
		case doorWanted[c] && g.doors[c]:
		case doorWanted[c] && g.walls[c]:
			allWallOut = append(allWallOut, c)
			add(reconcileItem{kind: OpDoorIn, cell: c})
		case doorWanted[c]:
			add(reconcileItem{kind: OpDoorIn, cell: c})
		case g.doors[c]:
			add(reconcileItem{kind: OpDoorOut, cell: c, target: ringTarget(rows, c, "Door")})
		case !g.walls[c]:
			add(reconcileItem{kind: OpWallIn, cell: c})
		}
	}
	// An otherwise complete ring never has more than one door gap open.
	wallOutCells, closed, gaps := allWallOut, true, 0
	for _, c := range rectCells(ring) {
		if g := in.Ground; onRing(c, ring) && !g.walls[c] && !g.doors[c] {
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
	var wallTargets []ClearanceTarget
	for _, c := range wallOutCells {
		wallTargets = append(wallTargets, ringTarget(rows, c, "Wall"))
	}
	var roof []domain.Cell
	if len(wallTargets) > 0 {
		roof = enclosedRoof(wallTargets, []Rectangle{roomGround(interior)}, in.Rooms)
	}
	for _, c := range allWallOut {
		add(reconcileItem{kind: OpWallOut, cell: c, target: ringTarget(rows, c, "Wall"), ready: len(roof) == 0 && slices.Contains(wallOutCells, c)})
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
	if in.WantedFloor != nil {
		for _, c := range rectCells(interior) {
			want := in.WantedFloor(c)
			have, has := actual[c]
			if want == "" || covered[c] || has && have == want {
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
		if rm.kind != OpFurnitureOut {
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
	shellAny := shellOut || owed(OpWallIn, OpDoorIn)
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
			it.ready = true
		case OpDoorIn:
			it.ready = !in.Ground.walls[it.cell]
		case OpPack, OpFurnitureOut:
			it.ready = true
		case OpPackInUse:
			it.ready = !otherRemoval
		case OpFloorOut:
			it.ready = !shellOut && !removalCells[it.cell]
		case OpFloorIn:
			it.ready = !shellAny && !removalCells[it.cell] && !floorOutCells[it.cell]
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
	return groupItems(items)
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
	case OpWallOut, OpWallIn, OpRoofOff:
		return GroundWalls
	case OpFloorOut, OpFloorIn:
		return GroundFloors
	}
	return GroundFurniture
}
