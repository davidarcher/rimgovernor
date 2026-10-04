package policy

import (
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The child rooms' layout (#1680, epic #1667): nursery, playroom and
// classroom are core rooms sized from the footprints they hold
// (ChildRoomShape), sited at the nearest core slot like the throne room.
// Furniture stands in bands of free floor: a band as high as the piece, a
// free row between bands, the row inside the entrance free, and the
// entrance's column free the full depth so every band stays reachable.

func init() {
	for _, role := range []RoomRole{RoomRoleNursery, RoomRolePlayroom, RoomRoleClassroom, RoomRoleWorshipRoom, RoomRoleDeathrestChamber, RoomRoleContainmentCell, RoomRoleIsolationRoom} {
		RegisterInteriorTemplate(role, InteriorTemplate{Name: "child-room", Plan: planChildRoom})
	}
}

// childSlots are the canonical corners a piece of size stands at in a
// width by depth room whose entrance column is aisle, back band first.
func childSlots(width, depth, aisle int32, size domain.Cell) []domain.Cell {
	var out []domain.Cell
	if size.X < 1 || size.Z < 1 {
		return nil
	}
	for z := depth - size.Z; z >= 1; z -= size.Z + 1 {
		for x := int32(0); x+size.X <= width; {
			if x <= aisle && aisle < x+size.X {
				x = aisle + 1
				continue
			}
			out = append(out, domain.Cell{X: x, Z: z})
			x += size.X
		}
	}
	return out
}

// childCapacity is how many pieces of size a width by depth room holds
// wherever its entrance stands.
func childCapacity(width, depth int32, size domain.Cell) int {
	capacity := -1
	for aisle := int32(0); aisle < width; aisle++ {
		if n := len(childSlots(width, depth, aisle, size)); capacity < 0 || n < capacity {
			capacity = n
		}
	}
	return max(capacity, 0)
}

// holds reports whether a width by depth interior holds every piece.
func (s ChildRoomShape) holds(width, depth int32) bool {
	for _, p := range s.Pieces {
		if childCapacity(width, depth, p.Size) < p.Count {
			return false
		}
	}
	return true
}

// ChildRoomSizes are the interiors (width along the spine, depth away from
// it) that hold the shape, best first: the fewest cells, then the aspect
// nearest two thirds. Depth stays within the core's cross-section.
func ChildRoomSizes(s ChildRoomShape) [][2]int32 {
	var out [][2]int32
	for d := throneMinSide; d <= coreMaxDepth; d++ {
		for w := throneMinSide; ; w++ {
			if s.holds(w, d) {
				out = append(out, [2]int32{w, d})
				break
			}
			// Width past the whole demand in one row cannot help.
			if int(w) > childRowDemand(s)+int(throneMinSide) {
				break
			}
		}
	}
	sort.Slice(out, func(i, j int) bool {
		a, b := out[i], out[j]
		if ca, cb := a[0]*a[1], b[0]*b[1]; ca != cb {
			return ca < cb
		}
		return abs32(2*a[0]-3*a[1]) < abs32(2*b[0]-3*b[1])
	})
	return out
}

// childRowDemand is the width one row would need for every piece.
func childRowDemand(s ChildRoomShape) int {
	w := 0
	for _, p := range s.Pieces {
		w += int(p.Size.X) * p.Count
	}
	return w
}

// ChildRoomFor is the plan's first room of the shape's role that holds it.
func (p LayoutPlan) ChildRoomFor(s ChildRoomShape) (LayoutRoom, bool) {
	for _, r := range p.AllRooms() {
		if w, d := frameDims(r); r.Role == s.Module && s.holds(w, d) {
			return r, true
		}
	}
	return LayoutRoom{}, false
}

// frameDims are the room's interior as its furniture frame sees it: width
// along the door's wall, depth away from it. A room beside a north-south
// hallway is stored transposed, so its door faces east or west and the
// frame's width is the interior's height.
func frameDims(r LayoutRoom) (width, depth int32) {
	if side, ok := doorSide(r.Interior, r.Door); ok && (side == domain.East || side == domain.West) {
		return r.Interior.Height, r.Interior.Width
	}
	return r.Interior.Width, r.Interior.Height
}

// growChildRoom adds a room holding the shape to plan unless it has one, at
// the nearest core slot. A room is only ever retired by the reconcile steps
// in layout_retire.go; none moves or resizes. It reports whether a room was
// added.
func growChildRoom(plan LayoutPlan, s ChildRoomShape) (LayoutPlan, bool, error) {
	if len(s.Pieces) == 0 || len(plan.Hallways()) == 0 {
		return plan, false, nil
	}
	if _, ok := plan.ChildRoomFor(s); ok {
		return plan, false, nil
	}
	return growModuleRoom(plan, s.Module, ChildRoomSizes(s))
}

// planChildRoom lays every slot for the piece being placed; the step takes
// the first free one. A template given no piece plans nothing.
func planChildRoom(f InteriorFrame, piece InteriorPieceDef) ([]InteriorPiece, bool) {
	var out []InteriorPiece
	for i, c := range childSlots(f.Width, f.Depth, f.Entrance, piece.Size) {
		out = append(out, NewInteriorPiece(fmt.Sprintf("child.%d", i), piece.Def, piece.Size, domain.South, c))
	}
	return out, piece.Def != "" && len(out) > 0
}
