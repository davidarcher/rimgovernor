package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// coupleBedFixture: lovers a and b each own a single Bed in their own bedroom
// (Room_1 at x 0..4, Room_2 at x 10..14).
func coupleBedFixture() (SleepingObservation, []TidyRoom) {
	var rooms []TidyRoom
	for i, id := range []string{"Room_1", "Room_2"} {
		x := int32(i * 10)
		room := InteriorRoom{Role: RoomRoleBedroom, Interior: Rectangle{X: x, Z: 0, Width: 5, Height: 4}, Doors: []domain.Cell{{X: x, Z: -1}}}
		plan, _ := PlanInterior(room, InteriorPieceDef{})
		tidy := TidyRoom{ID: id, Room: room}
		for _, p := range plan.Pieces {
			if p.Slot == "bed" {
				tidy.Pieces = append(tidy.Pieces, TidyPiece{Thing: "Bed_" + id[5:], Def: p.Def, Size: p.Size, Rot: p.Rot, Rect: p.Rect})
			}
		}
		rooms = append(rooms, tidy)
	}
	person := func(id PawnID, bed string, partner PawnID) SleepingPerson {
		return SleepingPerson{ID: id, OwnedBed: domain.Known(bed), Partners: []PawnID{partner}, BedSharingAllowed: domain.Known(true)}
	}
	bed := func(id, room string, owner PawnID) SleepingBed {
		return SleepingBed{ID: id, Definition: "Bed", Humanlike: domain.Known(true), Room: domain.Known(room), Owners: []PawnID{owner}}
	}
	obs := SleepingObservation{Colonists: 2,
		People: []SleepingPerson{person("a", "Bed_1", "b"), person("b", "Bed_2", "a")},
		Beds:   []SleepingBed{bed("Bed_1", "Room_1", "a"), bed("Bed_2", "Room_2", "b")}}
	return obs, rooms
}

func TestCoupleBedPacksThenInstallsInTheBedSlot(t *testing.T) {
	obs, rooms := coupleBedFixture()
	step, ok := NextCoupleBed(obs, rooms, nil, true)
	if !ok || step.Kind != CouplePack || step.Room != "Room_1" || len(step.Pack) != 2 || step.Pack[0].Thing != "Bed_1" || step.Pack[1].Thing != "Bed_2" {
		t.Fatalf("pack = %+v %v", step, ok)
	}
	if _, ok := NextCoupleBed(obs, rooms, nil, false); ok {
		t.Fatal("packed with no DoubleBed buildable")
	}

	// Packed: nobody owns a bed; the pack's first cell names the room.
	packedAt := AnchorForRect(step.Pack[0].Rect, step.Pack[0].Size, step.Pack[0].Rot)
	obs.People[0].OwnedBed, obs.People[1].OwnedBed = domain.Known(""), domain.Known("")
	obs.Beds = nil
	rooms[0].Pieces, rooms[1].Pieces = nil, nil
	install, ok := NextCoupleBed(obs, rooms, []domain.Cell{packedAt}, false)
	if !ok || install.Kind != CoupleInstall || install.Room != "Room_1" || install.Anchor != step.Anchor || install.Rot != step.Rot {
		t.Fatalf("install = %+v %v (pack slot %v %v)", install, ok, step.Anchor, step.Rot)
	}
	// The slot is the bedroom template's DoubleBed bed slot.
	plan, _ := PlanInterior(rooms[0].Room, InteriorPieceDefFor("DoubleBed"))
	if plan.Pieces[0].Slot != "bed" || plan.Pieces[0].Def != "DoubleBed" || plan.Pieces[0].Anchor() != install.Anchor {
		t.Fatalf("slot %+v, install %+v", plan.Pieces[0], install)
	}
	if _, ok := NextCoupleBed(obs, rooms, nil, true); ok {
		t.Fatal("install without a packed room")
	}

	// The double bed stands (owned by one, or nobody): assignment's turn.
	obs.Beds = []SleepingBed{{ID: "Bed_3", Definition: "DoubleBed", Humanlike: domain.Known(true), Room: domain.Known("Room_1")}}
	if s, ok := NextCoupleBed(obs, rooms, []domain.Cell{packedAt}, true); ok {
		t.Fatalf("double bed standing, step %+v", s)
	}
}

func TestCoupleBedIgnoresSinglesAndOthersBeds(t *testing.T) {
	obs, rooms := coupleBedFixture()
	obs.People[1].Partners = nil // not reciprocal
	if s, ok := NextCoupleBed(obs, rooms, nil, true); ok {
		t.Fatalf("no couple, step %+v", s)
	}
	obs, rooms = coupleBedFixture()
	obs.Beds[1].Owners = []PawnID{"b", "c"} // shared with a third pawn
	s, ok := NextCoupleBed(obs, rooms, nil, true)
	if !ok || len(s.Pack) != 1 || s.Pack[0].Thing != "Bed_1" {
		t.Fatalf("pack = %+v %v", s, ok)
	}
}

// Packing the couple's beds empties their room, which native then reads as
// RoomRoleNone; the install step still finds it from the packed cell
// (#1557: the couple stayed unhoused and the DoubleBed was never placed).
func TestCoupleBedInstallsInTheRoomPackingEmptied(t *testing.T) {
	interior := Rectangle{X: 0, Z: 0, Width: 5, Height: 4}
	door := domain.Cell{X: 0, Z: -1}
	rooms := RoomObservation{Rooms: []Room{{ID: "Room_1", Role: domain.Known(RoomRoleNone), Enclosed: domain.Known(true), Cells: rectCells(interior)}}}
	census := CurrentConstruction{Colony: true}
	cells := []SiteCell{{Cell: door, Doorway: domain.Known(true)}}
	if got := TidyFurnitureRooms(rooms, census, cells); len(got) != 0 {
		t.Fatalf("tidy rooms %+v: a roleless room has no template", got)
	}
	person := func(id, partner PawnID) SleepingPerson {
		return SleepingPerson{ID: id, OwnedBed: domain.Known(""), Partners: []PawnID{partner}, BedSharingAllowed: domain.Known(true)}
	}
	obs := SleepingObservation{Colonists: 2, People: []SleepingPerson{person("a", "b"), person("b", "a")}}
	install, ok := NextCoupleBed(obs, CoupleBedRooms(rooms, census, cells), []domain.Cell{{X: 1, Z: 1}}, true)
	if !ok || install.Kind != CoupleInstall || install.Room != "Room_1" {
		t.Fatalf("install = %+v %v", install, ok)
	}
}
