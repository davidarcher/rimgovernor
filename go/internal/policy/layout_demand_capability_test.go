package policy

import (
	"slices"
	"testing"
)

// TestCoreRoomsOwedByCapability: a planned shelter hosts research, so no
// laboratory is demanded; with no host, or no free capacity, the room is owed.
func TestCoreRoomsOwedByCapability(t *testing.T) {
	bare := corePlan(coreTestZones(), 0, TechTierCamp)
	bare.Rooms = slices.DeleteFunc(slices.Clone(bare.Rooms), func(r PlannedRoom) bool { return r.Role == PlannedShelter || r.Role == PlannedLab })
	hosted := bare
	hosted.Rooms = append(slices.Clone(bare.Rooms), PlannedRoom{Role: PlannedShelter})
	wanted := []PlannedRole{PlannedLab, PlannedHospital}
	if got := CoreRoomsOwed(bare, wanted); !slices.Equal(got, []PlannedRole{PlannedHospital, PlannedLab}) {
		t.Fatalf("no host: owed %v", got)
	}
	if got := CoreRoomsOwed(hosted, wanted); !slices.Equal(got, []PlannedRole{PlannedHospital}) {
		t.Fatalf("shelter hosts research: owed %v", got)
	}
	if got := CoreRoomsOwed(hosted, wanted, CapResearch); !slices.Equal(got, []PlannedRole{PlannedHospital, PlannedLab}) {
		t.Fatalf("host full: owed %v", got)
	}
}
