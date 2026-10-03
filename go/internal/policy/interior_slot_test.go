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

// A wide requested bench gets its own slots, regular and repeatable.
func TestWideBenchesGetSlots(t *testing.T) {
	for _, c := range []struct {
		role RoomRole
		def  string
	}{{RoomRoleWorkshop, "FabricationBench"}, {RoomRoleLaboratory, "HiTechResearchBench"}} {
		piece := testShapes.Defs[c.def]
		rooms := interiorRoomsAround(c.role, 11, 5, 1)
		first, ok := PlanInterior(rooms[0], piece)
		if !ok {
			t.Fatalf("%s: no plan", c.def)
		}
		assertInteriorRegular(t, first)
		n := 0
		for _, p := range first.Canonical {
			if p.Def == c.def {
				n++
				if p.Rect.Width != 5 || p.Rect.Height != 2 || p.Rect.Z+p.Rect.Height != first.Frame.Depth || !p.Accepts(testShapes, c.def) {
					t.Errorf("%s: slot %+v", c.def, p)
				}
			}
		}
		if n != 2 {
			t.Errorf("%s: %d slots, want 2", c.def, n)
		}
		for _, room := range rooms[1:] {
			if plan, ok := PlanInterior(room, piece); !ok || len(plan.Canonical) != len(first.Canonical) {
				t.Errorf("%s: %+v plans differently", c.def, room)
			}
		}
	}
}

// A room with a wide bench standing plans narrow benches at the wide pitch,
// centred in the same slots, so the row stays one line with even spacing.
func TestMixedBenchWidthsShareOneRow(t *testing.T) {
	room := interiorRoomsAround(RoomRoleWorkshop, 11, 5, 1)[0]
	wide, _ := PlanInterior(room, testShapes.Defs["FabricationBench"])
	room.Standing = []string{"FabricationBench"}
	narrow, ok := PlanInterior(room, testShapes.Defs["ElectricSmithy"])
	if !ok {
		t.Fatal("no plan")
	}
	assertInteriorRegular(t, narrow)
	centres := func(plan InteriorPlan, def string) []int32 {
		var out []int32
		for _, p := range plan.Canonical {
			if p.Def == def {
				out = append(out, 2*p.Rect.X+p.Rect.Width)
			}
		}
		return out
	}
	w, n := centres(wide, "FabricationBench"), centres(narrow, "ElectricSmithy")
	if len(w) == 0 || len(w) != len(n) {
		t.Fatalf("wide %v narrow %v", w, n)
	}
	for i := range w {
		if w[i] != n[i] {
			t.Errorf("slot %d: centre %d, wide %d", i, n[i], w[i])
		}
	}
}

// The entrance is the door onto a hallway, whatever the cell order.
func TestInteriorEntrancePrefersTheHallwayDoor(t *testing.T) {
	interior := Rectangle{X: 0, Z: 0, Width: 5, Height: 4}
	inner, hall := domain.Cell{X: -1, Z: 1}, domain.Cell{X: 2, Z: -1}
	room := InteriorRoom{Shapes: testShapes, Role: RoomRoleBedroom, Interior: interior, Doors: []domain.Cell{inner, hall}, InnerDoors: []domain.Cell{inner}}
	plan, ok := PlanInterior(room, InteriorPieceDef{})
	if !ok {
		t.Fatal("no plan")
	}
	if bed := plan.Pieces[0]; bed.Rect.Z+bed.Rect.Height != 4 || bed.Rot != domain.South {
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
	cells := []SiteCell{{Cell: inner, Doorway: domain.Known(true)}, {Cell: hall, Doorway: domain.Known(true)}, {Cell: domain.Cell{X: 1, Z: 3}, PlayerEdifice: domain.Known("FueledStove")}}
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
// and a storeroom door faces the hallway and lines its stoves on the
// storeroom wall.
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
	plan, ok := PlanInterior(got[0], InteriorPieceDef{})
	if !ok || len(plan.Pieces) == 0 {
		t.Fatal("no plan")
	}
	for _, p := range plan.Pieces {
		if p.Def != testStove || p.Rot != domain.East || p.Rect.X+p.Rect.Width != 6 {
			t.Errorf("%s %+v %s not on the storeroom wall", p.Slot, p.Rect, p.Rot)
		}
	}
}
