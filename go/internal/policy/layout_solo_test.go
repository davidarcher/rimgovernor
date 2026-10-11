package policy

import (
	"slices"
	"testing"
)

// TestSoloColonyOwesNoLaboratory: a solo colony's shelter hosts sleep, craft,
// research and heat, so a missing research bench demands no laboratory while the
// shelter has capacity, and a full shelter (the high-tech bench) owes one.
func TestSoloColonyOwesNoLaboratory(t *testing.T) {
	for _, c := range []Capability{CapSleep, CapCraft, CapResearch, CapHeat} {
		if !ShelterHosts(c) {
			t.Fatalf("the shelter does not host %s", c)
		}
	}
	solo := LayoutPlan{Cold: true, Rooms: []PlannedRoom{{Role: PlannedShelter}}}
	if got := CoreRoomsOwed(solo, []PlannedRole{PlannedLab}); len(got) != 0 {
		t.Fatalf("a solo cold colony owes %v", got)
	}
	if got := CoreRoomsOwed(solo, []PlannedRole{PlannedLab}, CapResearch); !slices.Equal(got, []PlannedRole{PlannedLab}) {
		t.Fatalf("a full shelter owes %v, want the laboratory", got)
	}
}
