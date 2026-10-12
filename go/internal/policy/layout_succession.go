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
// and workshop (#2863), and the kitchen, freezer, butchery, dining and
// recreation rooms and the bedrooms of the bedroom wings (#2864). Bills and
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
// (packer.against). A Link of another room into the room
// that leaves stays in the wall the old room shared and opens on cleared
// ground once clearance takes the old room.
//
// A bedroom is replaced by wing: the wing holding the outgrown bedroom turns
// Retiring (WingBedroomsRetiring), the next bedroom wing is sited at the
// successor's room size (WingSize), and the wing's sleepers move into it one
// pawn a step, each straight to a vacant bed (NextMigrateStep) while the old
// wing keeps every bed until no pawn owns one in it (EmptiedRetiringWings),
// so no night is spent unbedded.

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
// room is built; its ground goes to clearance.
func retireSuperseded(plan LayoutPlan, built map[int32]bool, drop map[int32]bool) {
	for _, r := range plan.AllRooms() {
		if r.Replaces != 0 && built[r.ID] {
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
	// Bedrooms are replaced by wing, one wing at a time: only a room of an
	// active wing, and none while a wing is still emptying.
	wingRooms := map[int32]bool{}
	retiring := false
	for _, w := range plan.Wings {
		retiring = retiring || w.Purpose == WingBedroomsRetiring
		for _, r := range w.Rooms {
			wingRooms[r.ID] = w.Purpose == WingBedrooms
		}
	}
	var best Successor
	for _, ga := range advice.Groups {
		if !ga.Outgrown {
			continue
		}
		role, ok := PlannedRoleOfGroup(ga.Group)
		if !ok || !slices.Contains(successorRoles, role) || role == PlannedBedroom && retiring {
			continue
		}
		shortfall := ga.Group.Area - ga.Capacity
		if least, ok := smallestStation(ga.Group, catalog); !ok || shortfall < least {
			continue
		}
		var old *PlannedRoom
		for _, id := range ga.Rooms {
			r := rooms[id]
			if !slices.Contains(successorRoles, r.Role) || r.Role == PlannedBedroom && !wingRooms[r.ID] {
				continue
			}
			if old == nil || r.Region.Area() < old.Region.Area() || r.Region.Area() == old.Region.Area() && r.ID < old.ID {
				old = &r
			}
		}
		if old == nil || best.Any() && shortfall <= best.Shortfall {
			continue
		}
		best = Successor{Replaces: old.ID, Role: old.Role, Linked: old.Link != nil, Floor: old.Region.Area(), Shortfall: shortfall}
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
	if s.Role == PlannedBedroom {
		next, retired := retireBedroomWing(plan, s)
		return next, retired, nil
	}
	siting := plan
	siting.Zones = coreWithout(plan.Zones, blocked)
	var accept func(PlannedRoom) bool
	if s.Linked {
		accept = func(r PlannedRoom) bool { return r.Link != nil }
	}
	sited, ok, err := siteRoomWhere(siting, sc, s.Role, accept, s.Sizes...)
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

// retireBedroomWing turns the bedroom wing holding the replaced room Retiring:
// its sleepers migrate, and the next wing sited takes the successor's room size
// (Successor.WingSize). False, and the plan as it came, when no active wing
// holds the room.
func retireBedroomWing(plan LayoutPlan, s Successor) (LayoutPlan, bool) {
	wings := slices.Clone(plan.Wings)
	for i, w := range wings {
		if w.Purpose == WingBedrooms && slices.ContainsFunc(w.Rooms, func(r PlannedRoom) bool { return r.ID == s.Replaces }) {
			wings[i].Purpose = WingBedroomsRetiring
			plan.Wings = wings
			return plan, true
		}
	}
	return plan, false
}

// WingSize is the room size (width along the corridor, depth away from it) the
// next bedroom wing is sited at for a bedroom successor: its block's interior,
// never smaller than the tier's WingRoomSize and no deeper than the core's
// cross-section. The zero size is the tier's, for every other successor.
func (s Successor) WingSize(tier TechTier) [2]int32 {
	if s.Role != PlannedBedroom {
		return [2]int32{}
	}
	size := WingRoomSize(tier)
	for _, got := range s.Sizes {
		if got[1] <= coreMaxDepth && got[0]*got[1] > size[0]*size[1] {
			return got
		}
	}
	return size
}
