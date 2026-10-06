package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func hospitalView(shapes PieceShapes) (StoreView, PlannedRoom) {
	hospital := PlannedRoom{Role: PlannedHospital, Interior: Rectangle{X: 10, Z: 10, Width: 7, Height: 5}, Door: domain.Cell{X: 13, Z: 15}, DoorRot: domain.South}
	return StoreView{Layout: &LayoutPlan{Rooms: []PlannedRoom{hospital}}, Shapes: shapes}, hospital
}

// The medicine store is declared from the planned hospital at plan time,
// before walls and roof: an Important 2x2 inside the hospital, off the planned
// beds and monitors and beside the template's bed slots, not at the door.
func TestMedicalStoreBesideThePlannedBeds(t *testing.T) {
	t.Parallel()
	view, hospital := hospitalView(testShapes)
	edit, ok := storeCreates(view)[plannedKey(domain.MedicineRolePrefix, hospital.Interior)]
	if !ok || len(edit.Cells) != 4 || edit.Priority != domain.ImportantPriority || edit.Filter != domain.MedicineFilter() || !withinRect(edit.Cells, hospital.Interior) {
		t.Fatalf("%+v", edit)
	}
	in, ok := InteriorRoomFromLayout(hospital, testShapes)
	if !ok {
		t.Fatal("no hospital room")
	}
	plan, ok := PlanInterior(in, InteriorPieceDef{})
	if !ok {
		t.Fatal("no hospital plan")
	}
	taken := map[domain.Cell]bool{}
	for _, p := range plan.Pieces {
		for _, c := range rectCells(p.Rect) {
			taken[c] = true
		}
	}
	for _, c := range edit.Cells {
		if taken[c] {
			t.Fatal("medicine on a planned bed or monitor", c)
		}
		if c.Z > 13 {
			t.Fatal("medicine at the door, not beside the beds", edit.Cells)
		}
	}
	if f := edit.Filter; f.Base() != domain.BaseNothing || len(f.Allow()) != 1 {
		t.Fatal(f)
	}
}

// Without bed slots (no piece shapes) the store sits nearest the hospital's
// door; with no planned hospital there is none.
func TestMedicalStoreFallsBackToTheDoorAndNeedsAHospital(t *testing.T) {
	t.Parallel()
	view, hospital := hospitalView(PieceShapes{})
	edit, ok := storeCreates(view)[plannedKey(domain.MedicineRolePrefix, hospital.Interior)]
	if !ok || len(edit.Cells) != 4 || !withinRect(edit.Cells, hospital.Interior) {
		t.Fatalf("%+v", edit)
	}
	for _, c := range edit.Cells {
		if c.Z < 13 {
			t.Fatal("medicine away from the door", edit.Cells)
		}
	}
	if got := storeCreates(StoreView{Layout: &LayoutPlan{}}); len(got) != 0 {
		t.Fatalf("no hospital planned %+v", got)
	}
}
