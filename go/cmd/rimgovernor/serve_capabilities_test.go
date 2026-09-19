package main

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Every family whose planner acts only while the development ranking
// selected its goal must declare that goal as a method capability; a family
// that wires the planner without declaring the goal ranks it
// method_unavailable on every review and never dispatches (the secure-supplies
// family shipped that way, #2).
func TestRoutineCapabilitiesDeclareSelectedGoals(t *testing.T) {
	t.Parallel()
	cases := []struct {
		family string
		goal   policy.GoalID
	}{
		{"secure-supplies", policy.SecureSupplies},
		{"repair", policy.MaintainEssentialRepairs},
		{"fire", policy.MaintainFireSafety},
		{"clean", policy.MaintainCleanFacilities},
		{"haul", policy.MaintainStorage},
		{"waste", policy.MaintainWaste},
		{"comfort", policy.EnsureComfort},
		{"expansion", policy.EnsureExpansion},
		{"animal-feed", policy.MaintainAnimalFeed},
		{"medical", policy.MaintainMedicalReserves},
		{"home-coverage", policy.MaintainHomeCoverage},
		{"stone-shell", policy.MaintainStoneShell},
		{"equip", policy.EnsureBasicDefense},
	}
	for _, tc := range cases {
		var c serveConfig
		c.routineProjectLimit = 2
		found := false
		for _, f := range routineFamilies(&c) {
			if f.Name == tc.family {
				*f.Enabled, found = true, true
			}
		}
		if !found {
			t.Fatalf("%s: unknown family", tc.family)
		}
		_, capabilities := routineCapabilities(c)
		if !slices.Contains(capabilities.Methods, tc.goal) {
			t.Errorf("%s family does not declare %s: %v", tc.family, tc.goal, capabilities.Methods)
		}
		var none serveConfig
		none.routineProjectLimit = 2
		if _, bare := routineCapabilities(none); slices.Contains(bare.Methods, tc.goal) {
			t.Errorf("%s declared with no family enabled", tc.goal)
		}
	}
}
