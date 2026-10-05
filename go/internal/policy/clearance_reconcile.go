package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The clear side of the reconciler (#2108, epic #2101): every planned room
// whose ground does not match the plan, standing or not, is reconciled, and the
// operations that take things down are batched by kind across rooms. The build
// side (wall, door, floor in, install, build) belongs to the owning concerns.

// clearFloor stands for "no floor wanted": until the flooring review supplies
// the wanted floor (#2107) every constructed floor on planned ground is owed
// its removal.
const clearFloor = "(clear)"

// clearKinds are the removal kinds in the order a pass takes them: other
// buildings first (one at a time, so removals cannot jointly invalidate the
// observed roof support), then packing, the door swaps, the roof and the
// walls, the floors last.
var clearKinds = []OpKind{OpFurnitureOut, OpPack, OpPackInUse, OpDoorOut, OpRoofOff, OpWallOut, OpFloorOut}

// groundRooms are the plan's open-ground rooms whose ring or doors differ from
// the plan.
func (p LayoutPlan) groundRooms(g GroundCensus) []PlannedRoom {
	var out []PlannedRoom
	for _, r := range p.AllRooms() {
		if !r.Dug && !p.GroundMatches(r, g) {
			out = append(out, r)
		}
	}
	return out
}

type clearedRoom struct {
	room PlannedRoom
	rec  Reconciliation
}

func reconcileGround(plan LayoutPlan, g GroundCensus, rows []ClearanceTarget, floors []ClearanceFloor, rooms RoomObservation) []clearedRoom {
	var out []clearedRoom
	for _, r := range plan.groundRooms(g) {
		out = append(out, clearedRoom{r, Reconcile(ReconcileInput{
			Plan: plan, Room: r, Ground: g, Rows: rows, Floors: floors, Rooms: rooms,
			WantedFloor: func(domain.Cell) string { return clearFloor },
		})})
	}
	return out
}

// PlannedGroundStep is the next clearance method: the first removal kind with
// an operation ready in any planned room, all of its ready cells of every room
// in one batch (a single other building or door swap at a time), else the
// first retired ground with work. Rows are the census's player rows
// (SplitGroundRows). ok is false when the ground is clear.
func PlannedGroundStep(plan LayoutPlan, g GroundCensus, rows []ClearanceTarget, floors []ClearanceFloor, rooms RoomObservation, rg RetiredGround) (GroundStep, bool) {
	crs := reconcileGround(plan, g, rows, floors, rooms)
	for _, kind := range clearKinds {
		step := GroundStep{Phase: kindLabel(kind)}
		seen, seenRoof := map[string]bool{}, map[domain.Cell]bool{}
		for _, cr := range crs {
			for _, op := range cr.rec.Ready {
				if op.Kind != kind {
					continue
				}
				cleared := false
				for _, t := range op.Targets {
					if id := targetID(t); id != "" && !seen[id] {
						seen[id] = true
						step.Targets = append(step.Targets, t)
						cleared = true
					}
				}
				for _, f := range op.Floors {
					if id := GroundFloorID(f.Cell); !seen[id] {
						seen[id] = true
						step.Floors = append(step.Floors, f)
					}
				}
				if kind == OpRoofOff {
					for _, c := range op.Cells {
						if !seenRoof[c] {
							seenRoof[c] = true
							step.Roof = append(step.Roof, c)
						}
					}
				}
				if cleared && kind == OpWallOut {
					step.Cleared = append(step.Cleared, roomGround(cr.room.Interior))
				}
			}
		}
		if len(step.Targets)+len(step.Floors)+len(step.Roof) == 0 {
			continue
		}
		if kind == OpFurnitureOut || kind == OpDoorOut {
			step.Targets = step.Targets[:1]
		}
		return step, true
	}
	return retiredGroundStep(rows, floors, rg.Ground, rooms, rg)
}

// PlannedGroundWork is the clearance deficit planned ground owes: every
// removal the planned rooms owe (Reconcile) and every target building and floor
// retired ground holds, stable.
func PlannedGroundWork(plan LayoutPlan, g GroundCensus, rows []ClearanceTarget, floors []ClearanceFloor, rooms RoomObservation, rg RetiredGround) []string {
	out := retiredGroundWork(rows, floors, rg.Ground, rg)
	for _, cr := range reconcileGround(plan, g, rows, floors, rooms) {
		for _, op := range cr.rec.Owed {
			if !slices.Contains(clearKinds, op.Kind) || op.Kind == OpRoofOff {
				continue
			}
			for _, t := range op.Targets {
				if id := targetID(t); id != "" {
					out = append(out, id)
				}
			}
			for _, f := range op.Floors {
				out = append(out, GroundFloorID(f.Cell))
			}
		}
	}
	slices.Sort(out)
	return slices.Compact(out)
}

// targetID names a building target; empty for a ring wall the census does not
// list, which no action can address.
func targetID(t ClearanceTarget) string { return t.EntityID }
