package policy

import (
	"slices"
	"testing"
)

func TestEveryPlannedRoleHasACapabilitySet(t *testing.T) {
	for role := range plannedRoleTier {
		if _, ok := roleCapabilities[role]; !ok {
			t.Errorf("planned role %s has no capability entry", role)
		}
	}
	if len(roleCapabilities) != len(plannedRoleTier) {
		t.Errorf("%d capability rows for %d planned roles", len(roleCapabilities), len(plannedRoleTier))
	}
}

func TestCapabilityTableAgreesWithFacilityCatalog(t *testing.T) {
	for role, caps := range roleCapabilities {
		room, ok := moduleRoomRoles[role]
		for c, home := range capabilityHomes {
			f, err := Facility(home)
			if err != nil {
				t.Fatalf("capability %s home %s: %v", c, home, err)
			}
			want := ok && f.Hosts(room)
			if role == PlannedShelter && slices.Contains(shelterCapabilities, c) {
				want = true
			}
			if got := slices.Contains(caps, c); got != want {
				t.Errorf("%s hosts %s = %v, catalog says %v", role, c, got, want)
			}
		}
	}
	if !slices.Contains(CapabilitiesOf(PlannedLab), CapResearch) || !slices.Contains(CapabilitiesOf(PlannedWorkshop), CapResearch) {
		t.Error("lab and workshop should host research")
	}
	if !slices.Contains(CapabilitiesOf(PlannedShelter), CapHeat) {
		t.Error("shelter should host heat")
	}
}
