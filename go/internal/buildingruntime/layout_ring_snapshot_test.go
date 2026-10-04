package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
)

// layout/ring (#1271): the expansion-phase MaintainHousing step that raised
// the Masonry capacity ring, recorded from `acceptance run layout/ring`
// (tick 15, issue-1271 branch on 8130fd810). The ring is one stone block definition throughout, a
// stone Door at the planned door cell, exactly the planned room's walls,
// its door onto a spine hallway.
func TestLayoutRingStepIsMasonry(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	r := loadRecorded(t, "layout-ring-review")
	if r.Review.Latches.Housing != policy.HousingExpansion {
		t.Fatalf("housing phase %q, want expansion", r.Review.Latches.Housing)
	}
	step := loadStep(t, "layout-ring-step", policy.MaintainHousing)
	facts := step.Projection
	if tier := styleTier(facts); tier != policy.BuildTierMasonry {
		t.Fatalf("build tier %v, want Masonry", tier)
	}
	planner := &RoundsBuildingPlanner{reviewer: &Rounder{policy: r.Policy}, concern: policy.MaintainHousing, phase: policy.HousingExpansion, shelter: true, definition: "Wall"}
	rooms, shells := planner.plannedRooms(facts)
	if len(rooms) == 0 {
		t.Fatal("no planned room for the capacity ring")
	}
	room, shell := rooms[0], shells[0]
	placements := shell.StyledPlacements(shellStyle(facts))
	if len(placements) != len(shell.Walls()) {
		t.Fatalf("%d placements for %d planned walls", len(placements), len(shell.Walls()))
	}
	walls := map[any]bool{}
	for _, w := range shell.Walls() {
		walls[w] = true
	}
	stuffs := map[string]bool{}
	for _, b := range placements {
		if !walls[b.Cell()] {
			t.Fatalf("ring cell %v lies off the planned room's walls", b.Cell())
		}
		if !policy.StoneBlockResource(policy.Resource(b.Stuff())) {
			t.Fatalf("%s at %v is %s, not stone blocks", b.Definition(), b.Cell(), b.Stuff())
		}
		stuffs[b.Stuff()] = true
		if b.Cell() == room.Door && b.Definition() != "Door" {
			t.Fatalf("door cell %v holds %s, want a stone Door", room.Door, b.Definition())
		}
	}
	if len(stuffs) != 1 {
		t.Fatalf("ring mixes block definitions %v", stuffs)
	}
	if placements[0].Cell() != room.Door || placements[0].Definition() != "Door" {
		t.Fatalf("ring door %s at %v, planned door %v", placements[0].Definition(), placements[0].Cell(), room.Door)
	}
	plan, _ := facts.LayoutPlan.Value()
	threshold, half := shell.Threshold(), policy.SpineWidth/2
	for _, s := range plan.Hallways() {
		if threshold.X >= min(s.From.X, s.To.X)-half && threshold.X <= max(s.From.X, s.To.X)+half &&
			threshold.Z >= min(s.From.Z, s.To.Z)-half && threshold.Z <= max(s.From.Z, s.To.Z)+half {
			return
		}
	}
	t.Fatalf("door %v opens onto %v, off every hallway %v", room.Door, threshold, plan.Hallways())
}
