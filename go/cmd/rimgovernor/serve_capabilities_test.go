package main

import (
	"slices"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Every family whose planner acts only while the development ranking
// selected its goal must declare that goal as a method capability; a family
// that wires the planner without declaring the goal ranks it
// method_unavailable on every review and never dispatches.
func TestRoundsCapabilitiesDeclareSelectedGoals(t *testing.T) {
	t.Parallel()
	cases := []struct {
		family string
		goal   policy.ConcernID
	}{
		{"repair", policy.MaintainEssentialRepairs},
		{"fire", policy.MaintainFireSafety},
		{"clean", policy.MaintainCleanFacilities},
		{"waste", policy.MaintainWaste},
		{"pollution", policy.ManagePollution},
		{"mechcharger", policy.EnsureMechCharger},
		{"genebank", policy.MaintainGeneBank},
		{"comfort", policy.EnsureComfort},
		{"expansion", policy.MaintainHousing},
		{"animal-feed", policy.MaintainAnimalFeed},
		{"medical", policy.MaintainMedicalReserves},
		{"home-coverage", policy.MaintainHomeCoverage},
		{"stone-shell", policy.MaintainStoneShell},
		{"firebreak", policy.MaintainFirebreak},
		{"psylink", policy.MaintainPsylink},
		{"creepjoiner", policy.ManageCreepJoiners},
		{"permits", policy.MaintainPermits},
		{"ideo-roles", policy.MaintainIdeoRoles},
		{"rituals", policy.MaintainRituals},
		{"equip", policy.EnsureBasicDefense},
		{"gear", policy.MaintainEquipment},
	}
	for _, tc := range cases {
		var c serveConfig
		found := false
		for _, f := range roundsFamilies(&c) {
			if f.Name == tc.family {
				*f.Enabled, found = true, true
			}
		}
		if !found {
			t.Fatalf("%s: unknown family", tc.family)
		}
		_, capabilities := roundsCapabilities(c)
		if !slices.Contains(capabilities.Methods, tc.goal) {
			t.Errorf("%s family does not declare %s: %v", tc.family, tc.goal, capabilities.Methods)
		}
		var none serveConfig
		if _, bare := roundsCapabilities(none); slices.Contains(bare.Methods, tc.goal) {
			t.Errorf("%s declared with no family enabled", tc.goal)
		}
	}
}

// DetectRounds refuses a capability declared twice, so a serve with every
// family and a resource target must declare each goal once (acquisition
// and resource targets both declare MaintainResource, #728).
func TestRoundsCapabilitiesDeclareEachGoalOnce(t *testing.T) {
	t.Parallel()
	var c serveConfig
	for _, f := range roundsFamilies(&c) {
		*f.Enabled = true
	}
	_, capabilities := roundsCapabilities(c)
	seen := map[policy.ConcernID]bool{}
	for _, goal := range capabilities.Methods {
		if seen[goal] {
			t.Error("declared twice:", goal)
		}
		seen[goal] = true
	}
}

// A serve composing every family must pass the reviewer's startup
// validation: every declared method is a goal DetectRounds recognizes on
// empty facts (#766, service/development died at serve start).
func TestRoundsCapabilitiesValidateAtStartup(t *testing.T) {
	t.Parallel()
	var c serveConfig
	for _, f := range roundsFamilies(&c) {
		*f.Enabled = true
	}
	thresholds, capabilities := roundsCapabilities(c)
	if _, err := policy.DetectRounds(policy.RoundsFacts{AvailableMethods: domain.Known(capabilities.Methods)}, policy.RoundsLatches{}, thresholds); err != nil {
		t.Fatal(err)
	}
}

// A composition without the Foothold families cannot climb the stage, so
// its staged families apply at every stage (light/dark stalled at Foothold
// with MaintainLighting never raised); the full autopilot keeps the ladder.
func TestRoundsCapabilitiesStageFloor(t *testing.T) {
	t.Parallel()
	lighting := serveConfig{roundsLightingPlans: true, roundsWorkPlans: true}
	if thresholds, _ := roundsCapabilities(lighting); thresholds.Stage.Floor != policy.StageDevelopment {
		t.Fatalf("lighting slice floor = %v, want Development", thresholds.Stage.Floor)
	}
	var full serveConfig
	for _, f := range roundsFamilies(&full) {
		*f.Enabled = true
	}
	if thresholds, _ := roundsCapabilities(full); thresholds.Stage.Floor != policy.StageFoothold {
		t.Fatalf("full autopilot floor = %v, want Foothold", thresholds.Stage.Floor)
	}
}
