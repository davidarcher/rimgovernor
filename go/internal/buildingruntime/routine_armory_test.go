package buildingruntime

import (
	"context"
	"testing"
)

// TestRoutineArmoryPlannerAssessesWithoutActions: after a review the armory
// skeleton reads the colony, selects a tier and admits no plan (#1201).
func TestRoutineArmoryPlannerAssessesWithoutActions(t *testing.T) {
	t.Parallel()
	reviewer, db, _, _, native := routineFixture(t)
	n := &gearProductionNative{gearTestNative: &gearTestNative{equipTestNative: &equipTestNative{routineNative: native}}}
	reviewer.native = n
	ctx := context.Background()
	if _, err := reviewer.Step(ctx); err != nil {
		t.Fatal(err)
	}
	before, err := db.LoadPlans(ctx, 256)
	if err != nil {
		t.Fatal(err)
	}
	planner, err := NewRoutineArmoryPlanner(reviewer, n)
	if err != nil {
		t.Fatal(err)
	}
	got, err := planner.step(ctx, ctx, nil)
	if err != nil {
		t.Fatal(err)
	}
	if got.Reason != BuildingMethodNoDeficit {
		t.Fatalf("reason = %v, want %v", got.Reason, BuildingMethodNoDeficit)
	}
	after, err := db.LoadPlans(ctx, 256)
	if err != nil {
		t.Fatal(err)
	}
	if len(after) != len(before) {
		t.Fatalf("armory admitted plans: %d -> %d", len(before), len(after))
	}
	t.Logf("assessment %+v", got.Assessment)
}
