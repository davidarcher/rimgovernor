package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func medicineView(role RoomRole, medical bool) StorageRequest {
	var cells []SiteCell
	var ward []domain.Cell
	for x := int32(0); x < 20; x++ {
		for z := int32(0); z < 20; z++ {
			cell := domain.Cell{X: x, Z: z}
			cells = append(cells, SiteCell{Cell: cell, Roofed: domain.Known(true), Walkable: domain.Known(true), Occupied: domain.Known(false), Zone: domain.Known(false), StorageEmpty: domain.Known(true)})
			if x >= 1 && x < 19 && z >= 1 && z < 19 {
				ward = append(ward, cell)
			}
		}
	}
	beds := []domain.Cell{{X: 16, Z: 16}, {X: 17, Z: 16}}
	bed := func(id string, at domain.Cell) SleepingBed {
		return SleepingBed{ID: id, Medical: domain.Known(medical), Room: domain.Known("ward"), Cell: at}
	}
	rooms := RoomObservation{Shapes: testShapes, Rooms: []Room{{ID: "ward", Role: domain.Known(role), Cells: ward, Beds: []string{"b1", "b2"}}}}
	sleeping := SleepingObservation{Beds: []SleepingBed{bed("b1", beds[0]), bed("b2", beds[1])}}
	return StorageRequest{Bounds: Bounds{Width: 20, Height: 20}, Cells: cells, Protected: beds, Rooms: &rooms, Sleeping: &sleeping}
}

// A hospital room gets one medicine zone nearest its medical beds; without
// a medical bed, a hospital role or the bed census there is none.
func TestPlanStorageMedicineBesideMedicalBeds(t *testing.T) {
	t.Parallel()
	plan := PlanStorage(medicineView(RoomRoleHospital, true))
	if len(plan.Sites) != 1 {
		t.Fatalf("%+v", plan.Sites)
	}
	site := plan.Sites[0]
	if site.Role != domain.MedicineRolePrefix+"ward" || site.Priority != domain.ImportantPriority || len(site.Candidates) == 0 {
		t.Fatalf("%+v", site)
	}
	for _, c := range site.Candidates[0] {
		if c.X < 13 || c.Z < 13 {
			t.Fatal("medicine must sit beside the medical beds", site.Candidates[0])
		}
	}
	if f := site.Filter; f.Base() != domain.BaseNothing || len(f.Allow()) != 1 {
		t.Fatal(f)
	}
	if got := PlanStorage(medicineView(RoomRoleHospital, false)).Sites; len(got) != 0 {
		t.Fatalf("no medical bed planned %+v", got)
	}
	if got := PlanStorage(medicineView(RoomRoleKitchen, true)).Sites; len(got) != 0 {
		t.Fatalf("a kitchen planned %+v", got)
	}
	view := medicineView(RoomRoleHospital, true)
	view.Sleeping = nil
	if got := PlanStorage(view).Sites; len(got) != 0 {
		t.Fatalf("unknown beds planned %+v", got)
	}
}
