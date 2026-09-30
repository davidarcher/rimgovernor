package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// Replaces layout/grid's capacity-ring half (#637, #787, #1262), over the
// same recording as TestLayoutGridFieldFillsPlanFieldBlocks (1caf938d9,
// tick 15, Stonecutting finished, stone blocks stocked, 5 spots for 8
// colonists). The live run never raised the ring because the ring is
// MaintainHousing's expansion phase, which opens only once the shelter
// phase recovers (and from StageReserves): the 5-for-8 deficit is the
// shelter phase, whose tick-15 step staged spots in the planned storeroom
// that the builders=0 fixture never built, so the goal stayed in flight on
// that plan and every later step read no_active_deficit. This test asserts the ring
// the expansion planner would raise over that read: one stone block
// definition throughout, a stone Door, exactly a planned room's walls, its
// door onto a spine hallway.
func TestLayoutGridCapacityRingIsMasonry(t *testing.T) {
	r := loadRecorded(t, "layout-grid-review")
	if r.Review.Latches.Housing != policy.HousingShelter {
		t.Fatalf("housing phase %q, want shelter (5 spots for 8 colonists)", r.Review.Latches.Housing)
	}
	facts := *r.Projection
	if tier := styleTier(facts); tier != policy.BuildTierMasonry {
		t.Fatalf("build tier %v, want Masonry", tier)
	}
	planner := &RoutineBuildingPlanner{reviewer: &RoutineReviewer{policy: r.Policy}, goal: policy.MaintainHousing, phase: policy.HousingExpansion, shelter: true, definition: "Wall"}
	rooms, shells := planner.plannedRooms(facts)
	if len(rooms) == 0 {
		t.Fatal("no planned Barracks room for the capacity ring")
	}
	room, shell := rooms[0], shells[0]
	placements := shell.StyledPlacements(shellStyle(facts))
	if len(placements) != len(shell.Walls()) {
		t.Fatalf("%d placements for %d planned walls", len(placements), len(shell.Walls()))
	}
	stuffs := map[string]bool{}
	walls := map[any]bool{}
	for _, w := range shell.Walls() {
		walls[w] = true
	}
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
