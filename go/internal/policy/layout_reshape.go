package policy

import (
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// In-place reshape (#2865). A built room whose region changes keeps its id:
// a wall moves, a column is added or taken away. Growing and shrinking are the
// same operation, ReshapeRoom, and the diff between the two regions
// (DiffRegions) is what the build then has to do. The ground the old shape
// held that the new one does not is retired, so clearance takes the old walls
// that now stand inside the room or outside it. A room whose doors would not
// stay on its walls is not reshaped; the outgrown trigger then falls back to a
// successor (layout_succession.go). One rebuild runs at a time: no reshape
// starts while another room is being built (rebuildInFlight).

// maxReshapeColumns is the most rows or columns one reshape adds to a side.
const maxReshapeColumns int32 = 8

// RegionDiff is what changing a room from one region to another builds and
// takes down.
type RegionDiff struct {
	// Added and Removed are the floor cells gained and lost.
	Added, Removed Region
	// WallsAdded and WallsRemoved are the wall-ring cells the new shape needs
	// built and the old one no longer holds.
	WallsAdded, WallsRemoved Region
}

// Empty reports no change at all.
func (d RegionDiff) Empty() bool {
	return d.Added.Empty() && d.Removed.Empty() && d.WallsAdded.Empty() && d.WallsRemoved.Empty()
}

// DiffRegions is the change from region from to region to.
func DiffRegions(from, to Region) RegionDiff {
	return RegionDiff{
		Added:        to.minus(from),
		Removed:      from.minus(to),
		WallsAdded:   to.Walls().minus(from.Walls()),
		WallsRemoved: from.Walls().minus(to.Walls()),
	}
}

func unionRegions(a, b Region) Region {
	return RegionOfRuns(append(slices.Clone(a.Runs), b.Runs...))
}

// reshapedGround is the ground clearance takes when a room goes from one
// region to another: every cell the old shape held (floor and walls) except
// the new shape's walls and the floor both shapes share. Old walls that now
// stand inside the room, floor that is now outside, and old walls beyond the
// new ring are in it.
func reshapedGround(from, to Region) Region {
	old := unionRegions(from, from.Walls())
	keep := unionRegions(to.Walls(), from.minus(from.minus(to)))
	return old.minus(keep)
}

// Reshaped is a room whose region a replan changed, with the change.
type Reshaped struct {
	Room PlannedRoom
	Diff RegionDiff
}

// ReshapedRooms are the rooms of next that keep an id of old and hold another
// region, for the caller to record.
func ReshapedRooms(old, next LayoutPlan) []Reshaped {
	had := map[int32]PlannedRoom{}
	for _, r := range old.AllRooms() {
		had[r.ID] = r
	}
	var out []Reshaped
	for _, r := range next.AllRooms() {
		if o, ok := had[r.ID]; ok && !o.Region.Equal(r.Region) {
			out = append(out, Reshaped{Room: r, Diff: DiffRegions(o.Region, r.Region)})
		}
	}
	return out
}

// reshapeFits reports that room r may take region to on plan: a plain
// rectangle room of the plan's own (not a wing's), every door and link still
// on the new walls, and the ground it newly takes (floor and walls) free core
// ground that no other room, hallway or blocked cell holds.
func reshapeFits(plan LayoutPlan, r PlannedRoom, to Region, blocked map[domain.Cell]bool, core coreGrid) bool {
	if !slices.ContainsFunc(plan.Rooms, func(o PlannedRoom) bool { return o.ID == r.ID }) {
		return false
	}
	if r.Unfenced() || r.Dug || r.Airlock != nil || r.Porch != nil || !r.Region.IsRect() || !to.IsRect() || to.Empty() {
		return false
	}
	b := to.Bounds()
	doors := []domain.Cell{r.Door}
	for _, d := range r.Doors {
		doors = append(doors, d.Cell)
	}
	if r.Link != nil {
		doors = append(doors, *r.Link)
	}
	doors = append(doors, plan.LinkCells(r.ID)...)
	for _, d := range doors {
		if !inWall(b, d) {
			return false
		}
	}
	taken := r.Region.Walls()
	taken = unionRegions(taken, r.Region)
	var others []Rectangle
	for _, o := range plan.AllRooms() {
		if o.ID != r.ID {
			others = append(others, roomWalls(o))
		}
	}
	halls := spineRects(plan.Hallways())
	for _, c := range unionRegions(to, to.Walls()).minus(taken).Cells() {
		if !core.core[c] || blocked[c] {
			return false
		}
		for _, o := range others {
			if contains(o, c) {
				return false
			}
		}
		for _, h := range halls {
			if contains(h, c) {
				return false
			}
		}
	}
	return true
}

// ReshapeRoom gives room id on plan the region to, keeping its id, doors and
// links, and retires the ground the old shape no longer holds. It reports the
// diff, or false (and plan as it came) when the room is not a plan room or the
// shape does not fit (reshapeFits).
func ReshapeRoom(plan LayoutPlan, id int32, to Region, blocked map[domain.Cell]bool) (LayoutPlan, RegionDiff, bool) {
	core := newCoreGrid(coreWithout(plan.Zones, blocked), plan.Reservations)
	return reshapeRoom(plan, id, to, blocked, core)
}

func reshapeRoom(plan LayoutPlan, id int32, to Region, blocked map[domain.Cell]bool, core coreGrid) (LayoutPlan, RegionDiff, bool) {
	i := slices.IndexFunc(plan.Rooms, func(o PlannedRoom) bool { return o.ID == id })
	if i < 0 || !reshapeFits(plan, plan.Rooms[i], to, blocked, core) {
		return plan, RegionDiff{}, false
	}
	from := plan.Rooms[i].Region
	diff := DiffRegions(from, to)
	if diff.Empty() {
		return plan, diff, false
	}
	plan.Rooms = slices.Clone(plan.Rooms)
	plan.Rooms[i].Region = to
	if ground := reshapedGround(from, to); !ground.Empty() {
		plan.RetiredGround = append(slices.Clone(plan.RetiredGround), ground)
	}
	return plan, diff, true
}

// rebuildInFlight reports a planned room with something of ours on it that is
// not standing as planned: a room being built, or a reshaped room whose new
// walls are not up. A census that is not known reports none.
func rebuildInFlight(growth RoomGrowth) bool {
	if growth.Fixed == nil || growth.Built == nil {
		return false
	}
	for id := range growth.Fixed {
		if !growth.Built[id] {
			return true
		}
	}
	return false
}

// reshapeOutgrown enlarges the room the successor asks to replace in place,
// when it is built, nothing else is being rebuilt, and some side takes
// the columns it lacks (the fewest cells over, ties east, west, south, north).
// It reports whether it did; the caller then asks no successor.
func reshapeOutgrown(plan LayoutPlan, s Successor, growth RoomGrowth, blocked map[domain.Cell]bool) (LayoutPlan, bool) {
	if !s.Any() || growth.Built == nil || !growth.Built[s.Replaces] || rebuildInFlight(growth) || SuccessorPending(plan) {
		return plan, false
	}
	i := slices.IndexFunc(plan.Rooms, func(o PlannedRoom) bool { return o.ID == s.Replaces })
	if i < 0 {
		return plan, false
	}
	r := plan.Rooms[i]
	b := r.Region.Bounds()
	need := s.Floor + s.Shortfall
	if b.Width <= 0 || b.Height <= 0 || !r.Region.IsRect() || need <= r.Region.Area() {
		return plan, false
	}
	extra := need - r.Region.Area()
	core := newCoreGrid(coreWithout(plan.Zones, blocked), plan.Reservations)
	var best Region
	bestArea := 0
	for side := range 4 {
		span := b.Height // east, west add columns
		if side >= 2 {
			span = b.Width // south, north add rows
		}
		k := int32((extra + int(span) - 1) / int(span))
		if k < 1 || k > maxReshapeColumns {
			continue
		}
		to := b
		switch side {
		case 0:
			to.Width += k
		case 1:
			to.X -= k
			to.Width += k
		case 2:
			to.Height += k
		case 3:
			to.Z -= k
			to.Height += k
		}
		cand := RectRegion(to)
		if !reshapeFits(plan, r, cand, blocked, core) {
			continue
		}
		if area := cand.Area(); best.Empty() || area < bestArea {
			best, bestArea = cand, area
		}
	}
	if best.Empty() {
		return plan, false
	}
	next, _, ok := reshapeRoom(plan, r.ID, best, blocked, core)
	return next, ok
}
