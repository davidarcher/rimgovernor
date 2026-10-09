package policy

import "testing"

// TestPlanCorePlansButcheryApartFromKitchen: the butchery is grown on demand and
// that does not crowd the kitchen.
func TestPlanCorePlansButcheryApartFromKitchen(t *testing.T) {
	p := corePlan(coreTestZones(), 0, TechTierCamp)
	p, grown, err := growDemandRooms(p, MapSurvey{}, nil, []PlannedRole{PlannedButchery})
	if err != nil || !grown {
		t.Fatalf("butchery grown=%v err=%v", grown, err)
	}
	var butchery, kitchen *PlannedRoom
	for i, r := range p.Rooms {
		switch r.Role {
		case PlannedButchery:
			butchery = &p.Rooms[i]
		case PlannedKitchen:
			kitchen = &p.Rooms[i]
		}
	}
	if butchery == nil || kitchen == nil {
		t.Fatalf("butchery %v kitchen %v", butchery, kitchen)
	}
	if overlapsRooms(*butchery, []PlannedRoom{*kitchen}) {
		t.Fatal("butchery crowds the kitchen")
	}
	t.Logf("butchery %+v link %v", butchery.Interior, butchery.Link)
}
