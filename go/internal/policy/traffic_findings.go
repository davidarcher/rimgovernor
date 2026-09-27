package policy

import (
	"fmt"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// Traffic findings (#817) check the layout rules against where pawns really
// walk. They have no action of their own yet: the service log carries them
// so a flagged room is visible before a planner acts on it.

// TrafficFindingKind names what a finding flags.
type TrafficFindingKind string

const (
	// TrafficThoroughfare is a room that should be a dead end (bedroom,
	// barracks, kitchen, hospital, lab, prison) carrying real colonist or
	// visitor through-traffic.
	TrafficThoroughfare TrafficFindingKind = "thoroughfare"
	// TrafficAnimalInCleanRoom is colony animal traffic through a kitchen
	// or hospital: tighten the allowed area or build the missing pen.
	TrafficAnimalInCleanRoom TrafficFindingKind = "animal_in_clean_room"
)

// Busiest-cell step counts a room must reach before it is flagged. A
// resident walking to their own bed stays well under the colonist bound on
// the two-day half-life; visitors have no business in these rooms at all.
const (
	thoroughfareColonistSteps = 150
	thoroughfareVisitorSteps  = 10
	animalCleanRoomSteps      = 10
)

// TrafficFinding is one flagged room with the busiest offending cell count.
type TrafficFinding struct {
	Kind  TrafficFindingKind
	Room  string
	Role  RoomRole
	Layer TrafficLayer
	Steps uint32
}

func (f TrafficFinding) String() string {
	return fmt.Sprintf("%s room=%s role=%s layer=%s steps=%d", f.Kind, f.Room, f.Role, f.Layer, f.Steps)
}

var thoroughfareRoles = map[RoomRole]bool{RoomRoleBedroom: true, RoomRoleBarracks: true, RoomRoleKitchen: true, RoomRoleHospital: true, RoomRoleLaboratory: true, RoomRolePrisonCell: true, RoomRolePrisonBarracks: true}

// TrafficFindings flags the census's rooms against the traffic layers, one
// finding per room, kind and layer, in room order.
func TrafficFindings(v FlooringObservation) []TrafficFinding {
	type key struct {
		room  int
		layer TrafficLayer
	}
	room := map[domain.Cell]int{}
	for i, r := range v.Rooms {
		for _, c := range r.Cells {
			room[c.Cell] = i
		}
	}
	busiest := map[key]uint32{}
	for _, t := range v.Traffic {
		i, ok := room[t.Cell]
		if !ok {
			continue
		}
		k := key{i, t.Layer}
		busiest[k] = max(busiest[k], t.Samples)
	}
	var out []TrafficFinding
	for k, steps := range busiest {
		role, known := v.Rooms[k.room].Role.Value()
		if !known {
			continue
		}
		f := TrafficFinding{Room: v.Rooms[k.room].ID, Role: role, Layer: k.layer, Steps: steps}
		switch {
		case thoroughfareRoles[role] && (k.layer == TrafficColonist && steps >= thoroughfareColonistSteps || k.layer == TrafficVisitor && steps >= thoroughfareVisitorSteps):
			f.Kind = TrafficThoroughfare
		case (role == RoomRoleKitchen || role == RoomRoleHospital) && k.layer == TrafficAnimal && steps >= animalCleanRoomSteps:
			f.Kind = TrafficAnimalInCleanRoom
		default:
			continue
		}
		out = append(out, f)
	}
	sort.Slice(out, func(i, j int) bool {
		if out[i].Room != out[j].Room {
			return out[i].Room < out[j].Room
		}
		if out[i].Kind != out[j].Kind {
			return out[i].Kind < out[j].Kind
		}
		return out[i].Layer < out[j].Layer
	})
	return out
}
