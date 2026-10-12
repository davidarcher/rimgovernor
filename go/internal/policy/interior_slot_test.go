package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestInteriorSlotAcceptsFamily(t *testing.T) {
	stove := InteriorPiece{Def: testStove}
	bench := InteriorPiece{Def: testWorkshop}
	cases := []struct {
		slot InteriorPiece
		def  string
		want bool
	}{
		{stove, "FueledStove", true},
		{stove, "ElectricStove", true},
		{stove, "TableButcher", false},
		{stove, "ElectricSmithy", false},
		{bench, "ElectricSmithy", true},
		{bench, "HandTailoringBench", true},
		{bench, "ElectricStove", false},
		{bench, "TableButcher", false},
		{bench, "FabricationBench", false},
		{InteriorPiece{Def: testResearch}, "HiTechResearchBench", false},
		{InteriorPiece{Def: "Bed"}, "DoubleBed", false},
	}
	for _, c := range cases {
		if got := c.slot.Accepts(testShapes, c.def); got != c.want {
			t.Errorf("%s slot accepts %s = %v, want %v", c.slot.Def, c.def, got, c.want)
		}
	}
}

// A wide requested bench gets its own slots against the back wall.
func TestWideBenchesGetSlots(t *testing.T) {
	for _, c := range []struct {
		role RoomRole
		def  string
	}{{RoomRoleWorkshop, "FabricationBench"}, {RoomRoleLaboratory, "HiTechResearchBench"}} {
		piece := testShapes.Defs[c.def]
		room, plan := planAround(t, c.role, 11, 5, 1, piece)
		n := 0
		for _, p := range plan.Pieces {
			if p.Def == c.def {
				n++
				if p.Rect.Width != 5 || p.Rect.Height != 2 || !p.Accepts(testShapes, c.def) {
					t.Errorf("%s: slot %+v", c.def, p)
				}
			}
		}
		if n != 2 {
			t.Errorf("%s: %d slots, want 2 in %+v", c.def, n, room.Interior)
		}
	}
}

// The entrance is the door onto a hallway, whatever the cell order.
func TestInteriorEntrancePrefersTheHallwayDoor(t *testing.T) {
	interior := Rectangle{X: 0, Z: 0, Width: 5, Height: 4}
	inner, hall := domain.Cell{X: -1, Z: 1}, domain.Cell{X: 2, Z: -1}
	room := InteriorRoom{Shapes: testShapes, Role: RoomRoleBedroom, Interior: interior, Doors: []domain.Cell{inner, hall}, InnerDoors: []domain.Cell{inner}}
	plan, ok := PlanRoomInterior(room, InteriorPieceDef{})
	if !ok {
		t.Fatal("no plan")
	}
	if bed := plan.Pieces[0]; bed.Rect.Z+bed.Rect.Height != 4 {
		t.Errorf("bed %+v not against the wall facing the hallway door", bed.Rect)
	}

	// Census: a workshop west, a hallway (None) south.
	floor := rectCells(interior)
	var freezer, hallway []domain.Cell
	for x := int32(0); x < 5; x++ {
		freezer = append(freezer, domain.Cell{X: -2, Z: x})
		hallway = append(hallway, domain.Cell{X: x, Z: -2})
	}
	rooms := RoomObservation{Shapes: testShapes, Rooms: []Room{
		{ID: "k", Role: domain.Known(RoomRoleKitchen), Enclosed: domain.Known(true), Cells: floor},
		{ID: "f", Role: domain.Known(RoomRoleWorkshop), Enclosed: domain.Known(true), Cells: freezer},
		{ID: "h", Role: domain.Known(RoomRoleNone), Enclosed: domain.Known(true), Cells: hallway},
	}}
	cells := []SiteCell{{Cell: inner, Doorway: domain.Known(true)}, {Cell: hall, Doorway: domain.Known(true)}, {Cell: domain.Cell{X: 1, Z: 3}, Things: []Thing{{Def: "FueledStove", Category: ThingBuilding, Faction: FactionPlayer, Flags: FlagEdifice}}}}
	got := InteriorRoomsFor(FacilityRequirement{Role: RoomRoleKitchen}, rooms, cells)
	if len(got) != 1 || len(got[0].InnerDoors) != 1 || got[0].InnerDoors[0] != inner || len(got[0].Standing) != 1 || got[0].Standing[0] != "FueledStove" {
		t.Fatalf("rooms %+v", got)
	}
	// A pass-through room (dining, rec) is hallway-like: its door is an
	// entrance, not an inner door.
	for _, role := range []RoomRole{RoomRoleDiningRoom, RoomRoleRecRoom} {
		rooms.Rooms[1].Role = domain.Known(role)
		if got := InteriorRoomsFor(FacilityRequirement{Role: RoomRoleKitchen}, rooms, cells); len(got) != 1 || len(got[0].InnerDoors) != 0 {
			t.Errorf("%s: inner doors %+v", role, got)
		}
	}
	// A storeroom stays pass-through for every other role.
	rooms.Rooms[1].Role = domain.Known(RoomRoleStoreroom)
	rooms.Rooms[0].Role = domain.Known(RoomRoleWorkshop)
	if got := InteriorRoomsFor(FacilityRequirement{Role: RoomRoleWorkshop}, rooms, cells); len(got) != 1 || len(got[0].InnerDoors) != 0 {
		t.Errorf("workshop beside storeroom: inner doors %+v", got)
	}
}

// RimWorld reads a freezer as a Storeroom: a kitchen with a hallway door
// and a storeroom door faces the hallway and takes the storeroom door as its
// freezer door.
func TestKitchenStoreroomDoorIsTheFreezerDoor(t *testing.T) {
	interior := Rectangle{X: 0, Z: 0, Width: 6, Height: 5}
	freezerDoor, hall := domain.Cell{X: 6, Z: 1}, domain.Cell{X: 2, Z: -1}
	var freezer, hallway []domain.Cell
	for z := int32(0); z < 5; z++ {
		freezer = append(freezer, domain.Cell{X: 7, Z: z})
	}
	for x := int32(0); x < 6; x++ {
		hallway = append(hallway, domain.Cell{X: x, Z: -2})
	}
	rooms := RoomObservation{Shapes: testShapes, Rooms: []Room{
		{ID: "k", Role: domain.Known(RoomRoleKitchen), Enclosed: domain.Known(true), Cells: rectCells(interior)},
		{ID: "f", Role: domain.Known(RoomRoleStoreroom), Enclosed: domain.Known(true), Cells: freezer},
		{ID: "h", Role: domain.Known(RoomRoleNone), Enclosed: domain.Known(true), Cells: hallway},
	}}
	cells := []SiteCell{{Cell: hall, Doorway: domain.Known(true)}, {Cell: freezerDoor, Doorway: domain.Known(true)}}
	got := InteriorRoomsFor(FacilityRequirement{Role: RoomRoleKitchen}, rooms, cells)
	if len(got) != 1 || len(got[0].InnerDoors) != 1 || got[0].InnerDoors[0] != freezerDoor {
		t.Fatalf("rooms %+v", got)
	}
	plan, ok := PlanRoomInterior(got[0], InteriorPieceDef{})
	if !ok || len(plan.Pieces) == 0 {
		t.Fatal("no plan")
	}
	for _, p := range plan.Pieces {
		if p.Def != testStove {
			t.Errorf("%s is %s, not a stove", p.Slot, p.Def)
		}
	}
}
