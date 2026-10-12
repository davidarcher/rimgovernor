package policy

import (
	"errors"
	"fmt"
	"slices"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Successor rooms (#2863). A built room the colony outgrew is not resized: the
// plan adds a larger room that Replaces it, the old room stays pinned and in
// use until the successor stands, and then the old room leaves the plan, so
// clearance takes its ground as it does a dropped wing's. One rebuild is in
// flight at a time. The trigger is the station chain's outgrown predicate
// (GroupOutgrown); the successor replaces the smallest matched room and is
// sized to that room's floor plus the group's shortfall. The roles are storage
// and workshop (#2863), and the kitchen, freezer, butchery, dining,
// recreation and bedrooms (#2864). Bills and
// packed furniture stay with #2881, and merging several rooms into one is
// #2878.
//
// Every role has the one outgrown test: its station group needs more floor
// than the built rooms matched to it give, by at least the footprint plus one
// open cell of the group's smallest station. The freezer and the butchery are
// matched to the group that became them (matchedPlanned), not to the storage
// and kitchen groups. A successor of a room with a Link door (the freezer's to
// the kitchen, the dining room's to the freezer, the butchery's) is sited only
// where it has one (beside the neighbour, else on its back wall), and a
// neighbour that is itself replaced is passed over for its successor
// (packer.against).
//
// A bedroom is an ordinary successor role: the replaced bedroom stays pinned
// and in use, its sleepers move one pawn a step, each straight to a vacant bed
// (NextMigrateStep), and it leaves the plan only once no pawn owns a bed in it
// (SleptBedrooms), so no night is spent unbedded.
//
// When the old room leaves, the Link doors of the rooms that opened into it
// (the dining room and butchery into the freezer, the freezer into the
// kitchen, the wardrobe into the workshop) move to the wall each shares with
// the successor (repointLinks); a room the successor does not touch loses its
// Link, so no door opens on cleared ground. The successor is sited beside
// those rooms when a slot allows it.

// successorRoles are the planned roles a successor may replace.
var successorRoles = []PlannedRole{PlannedStorage, PlannedWorkshop, PlannedKitchen, PlannedFreezer, PlannedButchery, PlannedDining, PlannedRec, PlannedBedroom}

// Successor is the room the plan is asked to add in place of an outgrown one.
// The zero value asks for none.
type Successor struct {
	// Replaces is the id of the built room the successor supersedes.
	Replaces int32
	Role     PlannedRole
	// Linked: the replaced room has a Link door, which the successor keeps.
	Linked bool
	// Floor is the replaced room's floor and Shortfall the cells its group
	// lacks; the successor holds at least their sum.
	Floor, Shortfall int
	// Sizes are the interiors (width, depth) SiteRoom tries in order: the
	// group's embedded block, cut to the floor needed. Empty when the
	// embedding placed no block for the group.
	Sizes [][2]int32
}

// Any reports a successor was asked for.
func (s Successor) Any() bool { return s.Replaces != 0 }

// UnsitedSuccessor is a successor the plan could not site. Nothing is built.
type UnsitedSuccessor struct {
	Successor Successor
	Reason    string
}

func (u *UnsitedSuccessor) Error() string {
	return fmt.Sprintf("successor of room %d (%s) not sited: %s", u.Successor.Replaces, u.Successor.Role, u.Reason)
}

// SplitUnsited separates the unsited successors from the other errors of a
// replan.
func SplitUnsited(err error) ([]*UnsitedSuccessor, error) {
	var unsited []*UnsitedSuccessor
	var rest []error
	var walk func(error)
	walk = func(e error) {
		switch v := e.(type) {
		case nil:
		case *UnsitedSuccessor:
			unsited = append(unsited, v)
		case interface{ Unwrap() []error }:
			for _, c := range v.Unwrap() {
				walk(c)
			}
		default:
			rest = append(rest, e)
		}
	}
	walk(err)
	return unsited, errors.Join(rest...)
}

// superseded are the ids of the plan's rooms that another room of the plan
// replaces.
func (p LayoutPlan) superseded() map[int32]bool {
	out := map[int32]bool{}
	for _, r := range p.AllRooms() {
		if r.Replaces != 0 {
			out[r.Replaces] = true
		}
	}
	return out
}

// RoomChains is how many rooms of role the plan holds, a room and the
// successors that replace it counting as one.
func (p LayoutPlan) RoomChains(role PlannedRole) int {
	gone := p.superseded()
	n := 0
	for _, r := range p.roomsOf(role) {
		if !gone[r.ID] {
			n++
		}
	}
	return n
}

// SuccessorPending reports a successor whose predecessor is still in the plan:
// a rebuild in flight.
func SuccessorPending(plan LayoutPlan) bool {
	gone := plan.superseded()
	return slices.ContainsFunc(plan.AllRooms(), func(r PlannedRoom) bool { return gone[r.ID] })
}

// successorPinned are the ids of a room in a rebuild in flight and of its
// successor: neither moves until the predecessor leaves the plan.
func successorPinned(plan LayoutPlan) map[int32]bool {
	have := map[int32]bool{}
	for _, r := range plan.AllRooms() {
		have[r.ID] = true
	}
	out := map[int32]bool{}
	for _, r := range plan.AllRooms() {
		if r.Replaces != 0 && have[r.Replaces] {
			out[r.ID], out[r.Replaces] = true, true
		}
	}
	return out
}

// retireSuperseded adds to drop each room whose successor stands. The old
// room is built; its ground goes to clearance, unless slept: a bedroom a pawn
// still owns a bed in stays until the pawn has moved.
func retireSuperseded(plan LayoutPlan, built, slept map[int32]bool, drop map[int32]bool) {
	for _, r := range plan.AllRooms() {
		if r.Replaces != 0 && built[r.ID] && !slept[r.Replaces] {
			drop[r.Replaces] = true
		}
	}
}

// SuccessionChanges are the successors a replan added and the predecessors it
// retired, for the caller to record.
func SuccessionChanges(old, next LayoutPlan) (planned, retired []PlannedRoom) {
	had := map[int32]PlannedRoom{}
	for _, r := range old.AllRooms() {
		had[r.ID] = r
	}
	have := map[int32]bool{}
	for _, r := range next.AllRooms() {
		have[r.ID] = true
		if _, ok := had[r.ID]; !ok && r.Replaces != 0 {
			planned = append(planned, r)
		}
	}
	for _, r := range next.AllRooms() {
		if gone, ok := had[r.Replaces]; ok && r.Replaces != 0 && !have[r.Replaces] {
			retired = append(retired, gone)
		}
	}
	return planned, retired
}

// OutgrownSuccession is the successor the station advice asks for, or none.
// A group is outgrown (GroupOutgrown) when its matched built rooms hold less
// floor than it needs; the succession acts only when that shortfall holds the
// footprint and clearance of the smallest station the group places (catalog
// data), so a floor that cannot take one more station is not outgrown. Of the
// outgrown groups of a successor role the one most short goes first (ties to
// the lower id); its smallest matched room of such a role is replaced and the
// successor is that room's floor plus the shortfall. None while a rebuild is in
// flight, so one runs at a time. An unreadable station shape leaves its group
// as it is.
func OutgrownSuccession(advice StationAdvice, catalog StationCatalog, plan LayoutPlan) Successor {
	if SuccessorPending(plan) {
		return Successor{}
	}
	rooms := map[int32]PlannedRoom{}
	for _, r := range plan.AllRooms() {
		rooms[r.ID] = r
	}
	var best Successor
	for _, ga := range advice.Groups {
		if !ga.Outgrown {
			continue
		}
		role, ok := PlannedRoleOfGroup(ga.Group)
		if !ok || !slices.Contains(successorRoles, role) {
			continue
		}
		shortfall := ga.Group.Area - ga.Capacity
		if least, ok := smallestStation(ga.Group, catalog); !ok || shortfall < least {
			continue
		}
		var old *PlannedRoom
		for _, id := range ga.Rooms {
			r := rooms[id]
			if !slices.Contains(successorRoles, r.Role) {
				continue
			}
			if old == nil || r.Region.Area() < old.Region.Area() || r.Region.Area() == old.Region.Area() && r.ID < old.ID {
				old = &r
			}
		}
		if old == nil || best.Any() && shortfall <= best.Shortfall {
			continue
		}
		best = Successor{Replaces: old.ID, Role: old.Role, Linked: len(plan.LinksOf(*old)) > 0, Floor: old.Region.Area(), Shortfall: shortfall}
		if block, placed := advice.Embedding.Block(ga.Group.ID); placed {
			best.Sizes = successorSizes(block, best.Floor+shortfall)
		}
	}
	return best
}

// smallestStation is the footprint plus the open cell the cluster keeps beside
// it, of the smallest station in g. False when no station of g has a shape.
func smallestStation(g RoomGroup, catalog StationCatalog) (int, bool) {
	least, found := 0, false
	for _, s := range g.Stations {
		st, ok := catalog.Stations[s.Def]
		if !ok || s.Count <= 0 || st.Shape.Size.X <= 0 || st.Shape.Size.Z <= 0 {
			continue
		}
		if cells := int(st.Shape.Size.X)*int(st.Shape.Size.Z) + 1; !found || cells < least {
			least, found = cells, true
		}
	}
	return least, found
}

// successorSizes are the interiors a successor of at least floor cells tries:
// the embedded block, its depth cut to the floor when the block holds more,
// and its turn.
func successorSizes(block Rectangle, floor int) [][2]int32 {
	w, h := block.Width, block.Height
	if w <= 0 || h <= 0 {
		return nil
	}
	if int(w)*int(h) > floor {
		h = int32(max((floor+int(w)-1)/int(w), 1))
	}
	sizes := [][2]int32{{w, h}}
	if w != h {
		sizes = append(sizes, [2]int32{h, w})
	}
	return sizes
}

// growSuccessor sites the successor of growth on plan, off the cells blocked,
// and marks the room it adds as replacing the old one. A successor that cannot
// be sited is returned as an *UnsitedSuccessor and nothing is added.
func growSuccessor(plan LayoutPlan, s Successor, sc *planScorer, blocked map[domain.Cell]bool) (LayoutPlan, bool, error) {
	if !s.Any() {
		return plan, false, nil
	}
	if !slices.ContainsFunc(plan.AllRooms(), func(r PlannedRoom) bool { return r.ID == s.Replaces }) || plan.superseded()[s.Replaces] {
		return plan, false, nil
	}
	if len(s.Sizes) == 0 {
		return plan, false, &UnsitedSuccessor{Successor: s, Reason: "the embedding placed no block for the group"}
	}
	siting := plan
	siting.Zones = coreWithout(plan.Zones, blocked)
	// Beside the rooms that open into the old one when a slot allows it, else
	// anywhere that keeps the successor's own Link.
	var accept func(PlannedRoom) bool
	if s.Linked {
		accept = func(r PlannedRoom) bool { return len(siting.LinksOf(r)) > 0 }
	}
	var sited LayoutPlan
	ok, err := false, error(nil)
	if linkers := linkersOf(plan, s.Replaces); len(linkers) > 0 {
		sited, ok, err = siteRoomWhere(siting, sc, s.Role, func(r PlannedRoom) bool {
			return (accept == nil || accept(r)) && !slices.ContainsFunc(linkers, func(n PlannedRoom) bool { _, shared := sharedDoor(n, r); return !shared })
		}, s.Sizes...)
	}
	if !ok {
		sited, ok, err = siteRoomWhere(siting, sc, s.Role, accept, s.Sizes...)
	}
	if !ok {
		reason := "no free core slot"
		if err != nil {
			reason = err.Error()
		}
		return plan, false, &UnsitedSuccessor{Successor: s, Reason: reason}
	}
	sited.Zones = plan.Zones
	sited.Rooms[len(sited.Rooms)-1].Replaces = s.Replaces
	return sited, true, nil
}

// linkersOf are the rooms of plan, other than the room id, whose Link door
// opens in its walls.
func linkersOf(plan LayoutPlan, id int32) []PlannedRoom {
	var old PlannedRoom
	for _, r := range plan.AllRooms() {
		if r.ID == id {
			old = r
		}
	}
	var out []PlannedRoom
	for _, r := range plan.Rooms {
		if r.ID != id && plan.linked(r, old) {
			out = append(out, r)
		}
	}
	return out
}

// sharedDoor is the door cell in the wall rooms a and b share, the middle one
// in reading order; false when their walls share no door cell.
func sharedDoor(a, b PlannedRoom) (domain.Cell, bool) {
	var cells []domain.Cell
	w := roomWalls(a)
	for x := w.X; x < w.X+w.Width; x++ {
		for z := w.Z; z < w.Z+w.Height; z++ {
			c := domain.Cell{X: x, Z: z}
			if inWall(a.Region.Bounds(), c) && inWall(b.Region.Bounds(), c) && c != a.Door && c != b.Door {
				cells = append(cells, c)
			}
		}
	}
	if len(cells) == 0 {
		return domain.Cell{}, false
	}
	return cells[len(cells)/2], true
}

// repointLinks moves, for each room of drop that a successor replaces, the
// Link doors that opened into it: to the wall the neighbour shares with the
// successor, or to none when they share no wall, so no door is left opening on
// the ground clearance takes.
func repointLinks(plan LayoutPlan, drop map[int32]bool) LayoutPlan {
	rooms := slices.Clone(plan.Rooms)
	for _, succ := range plan.AllRooms() {
		if succ.Replaces == 0 || !drop[succ.Replaces] {
			continue
		}
		for _, n := range linkersOf(plan, succ.Replaces) {
			i := slices.IndexFunc(rooms, func(r PlannedRoom) bool { return r.ID == n.ID })
			if i < 0 || drop[n.ID] {
				continue
			}
			rooms[i].Link = nil
			if c, ok := sharedDoor(n, succ); ok {
				rooms[i].Link = &c
			}
		}
	}
	plan.Rooms = rooms
	return plan
}
