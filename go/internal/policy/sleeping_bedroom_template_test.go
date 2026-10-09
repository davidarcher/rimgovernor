package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A bedroom step's template is the bed in the bedroom template's bed slot, for
// a suite as for a bedroom; a room without a door has none.
func TestBedroomTemplateIsTheBedSlot(t *testing.T) {
	for _, role := range []PlannedRole{PlannedBedroom, PlannedSuite, PlannedShelter} {
		room := PlannedRoom{Role: role, Interior: Rectangle{X: 0, Z: 0, Width: 5, Height: 4}, Door: domain.Cell{X: 0, Z: -1}}
		template, ok := BedroomTemplate(room, testShapes, "Bed")
		if !ok || len(template) != 1 || template[0].DefName != "Bed" || template[0].Slot != "bed" {
			t.Fatalf("%s template = %+v %v", role, template, ok)
		}
		plan, _ := PlanInterior(InteriorRoom{Shapes: testShapes, Role: RoomRoleBedroom, Interior: room.Interior, Doors: []domain.Cell{room.Door}}, testShapes.Defs["Bed"])
		if template[0].Anchor() != plan.Pieces[0].Anchor() || template[0].Rot != plan.Pieces[0].Rot {
			t.Fatalf("%s template %+v, slot %+v", role, template[0], plan.Pieces[0])
		}
	}
	if _, ok := BedroomTemplate(PlannedRoom{Role: PlannedBedroom, Interior: Rectangle{Width: 5, Height: 4}}, testShapes, "Bed"); ok {
		t.Fatal("template for a room with no door")
	}
}
