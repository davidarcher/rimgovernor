package policy

import (
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The build side of the reconciler (#2109, epic #2101): one library an owning
// concern calls for its own room. The owner decides which room and when; the
// reconciler decides how: it diffs the room's ring, floor and furniture against
// the ground and reports the operations ready now, installing from packed stock
// first (Stock, #2104) and building on site only when none is stored. The
// plan-wide clear side (PlannedGroundStep) owns the ring's removals, so those
// kinds are not the owner's.

// ReconcileRoom is the room's next operations: every ready operation of
// Reconcile except the ring's removals (door and wall out, roof off), batched by
// kind in table order. Empty when the room matches or waits on a prerequisite.
func ReconcileRoom(in ReconcileInput) []Operation {
	var out []Operation
	for _, op := range Reconcile(in).Ready {
		switch op.Kind {
		case OpDoorOut, OpWallOut, OpRoofOff:
			continue
		}
		out = append(out, op)
	}
	return out
}

// OwnRows are the census rows an owner reconciles its room against: the ring,
// frames and stand-ins, the template's standing pieces and the buildings
// forbidden finds (packed or taken down by the reconcile). Every other building
// the room holds is left alone, so an owner never repacks the furniture the
// room's quality levers added.
func OwnRows(rows []ClearanceTarget, template []WantedPiece, forbidden func(def string) bool) []ClearanceTarget {
	var out []ClearanceTarget
	for _, row := range rows {
		keep := !row.Player || row.Class == "ancient_wall_door" || strings.HasPrefix(row.DefName, "Frame_") || standInBed(row) || forbidden != nil && forbidden(row.DefName)
		for _, p := range template {
			keep = keep || p.DefName == row.DefName && p.Minimum == row.Minimum && p.Maximum == row.Maximum
		}
		if keep {
			out = append(out, row)
		}
	}
	return out
}

// GroundWithRock is g with the natural rock on the rings of the plan's rooms
// counted as wall, except where the plan puts a door: rock walls a dug room as
// it stands (#836) and is no wall to raise.
func (p LayoutPlan) GroundWithRock(g GroundCensus, rock []domain.Cell) GroundCensus {
	doors := map[domain.Cell]bool{}
	for _, r := range p.AllRooms() {
		for _, d := range p.ShellDoors(r) {
			doors[d] = true
		}
	}
	out := GroundCensus{walls: map[domain.Cell]bool{}, doors: g.doors}
	for c := range g.walls {
		out.walls[c] = true
	}
	for _, c := range rock {
		if !doors[c] {
			out.walls[c] = true
		}
	}
	return out
}

// RoomGround is the rectangle a planned room's ring and interior cover, the
// ground an owner reads the census on.
func (r PlannedRoom) RoomGround() Rectangle { return roomGround(r.Interior) }
