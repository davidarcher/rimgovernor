package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// A planner refusal filed on a record with no method names the block at
// once and survives the next review; a clear drops it back to no_method.
func TestRecordPlannerReasonsNamesTheRefusal(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	defer s.Close()
	r := routineRequest()
	reviewRoutine(t, s, &r)
	changed, err := s.RecordPlannerReasons(ctx, map[policy.GoalID]string{policy.MaintainResource: "insufficient_verified_space"})
	if err != nil || !changed {
		t.Fatal(changed, err)
	}
	if changed, err = s.RecordPlannerReasons(ctx, map[policy.GoalID]string{policy.MaintainResource: "insufficient_verified_space"}); err != nil || changed {
		t.Fatal("unchanged reason rewrote the review", err)
	}
	second := reviewRoutine(t, s, &r)
	wood := progressRecord(t, second.Review, policy.MaintainResource)
	if wood.Planner != "insufficient_verified_space" || wood.Blocked != policy.BlockedPlanner("insufficient_verified_space") {
		t.Fatalf("wood record %+v", wood)
	}
	if _, err = s.RecordPlannerReasons(ctx, map[policy.GoalID]string{policy.MaintainResource: ""}); err != nil {
		t.Fatal(err)
	}
	review, err := s.LoadRoutineReview(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if wood = progressRecord(t, review, policy.MaintainResource); wood.Planner != "" || wood.Blocked != policy.BlockedNoMethod {
		t.Fatalf("cleared wood record %+v", wood)
	}
}
