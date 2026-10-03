package policy

import "testing"

// TestPlanCorePlansButcheryApartFromKitchen: the plan holds a butcher room
// that does not crowd the kitchen.
func TestPlanCorePlansButcheryApartFromKitchen(t *testing.T) {
	p := PlanCore(coreTestZones(), 0, BuildTierCamp)
	var butchery, kitchen *LayoutRoom
	for i, r := range p.Rooms {
		switch r.Role {
		case ModuleButchery:
			butchery = &p.Rooms[i]
		case ModuleKitchen:
			kitchen = &p.Rooms[i]
		}
	}
	if butchery == nil || kitchen == nil {
		t.Fatalf("butchery %v kitchen %v", butchery, kitchen)
	}
	if overlapsRooms(*butchery, []LayoutRoom{*kitchen}) {
		t.Fatal("butchery crowds the kitchen")
	}
	t.Logf("butchery %+v link %v", butchery.Interior, butchery.Link)
}
