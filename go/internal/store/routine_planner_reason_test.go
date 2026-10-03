package store

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

const spaceText = "no space found for it (verified space)"

// A planner refusal filed on a record with no method names the block at
// once and survives the next review; a clear drops it back to no_method.
func TestRecordPlannerReasonsNamesTheRefusal(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	defer s.Close()
	r := routineRequest()
	reviewRoutine(t, s, &r)
	refusal := policy.PlannerNote{Text: spaceText}
	changed, err := s.RecordPlannerReasons(ctx, map[policy.GoalID]policy.PlannerNote{policy.MaintainResource: refusal})
	if err != nil || !changed {
		t.Fatal(changed, err)
	}
	if changed, err = s.RecordPlannerReasons(ctx, map[policy.GoalID]policy.PlannerNote{policy.MaintainResource: refusal}); err != nil || changed {
		t.Fatal("unchanged reason rewrote the review", err)
	}
	second := reviewRoutine(t, s, &r)
	wood := progressRecord(t, second.Review, policy.MaintainResource)
	if wood.Planner != spaceText || wood.Blocked != policy.BlockedPlanner(spaceText) || !wood.Blocked.Actionable() {
		t.Fatalf("wood record %+v", wood)
	}
	if _, err = s.RecordPlannerReasons(ctx, map[policy.GoalID]policy.PlannerNote{policy.MaintainResource: {}}); err != nil {
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

// A wait files as a wait, survives the next review as one (not as a
// refusal), is replaced by a refusal, and clears when the planner admits.
func TestRecordPlannerReasonsFilesWaitsAndClearsThem(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	defer s.Close()
	r := routineRequest()
	reviewRoutine(t, s, &r)
	const waitText = "waiting on the shelter's open bunks"
	wait := policy.PlannerNote{Text: waitText, Waiting: true}
	if changed, err := s.RecordPlannerReasons(ctx, map[policy.GoalID]policy.PlannerNote{policy.MaintainResource: wait}); err != nil || !changed {
		t.Fatal(changed, err)
	}
	wood := progressRecord(t, reviewRoutine(t, s, &r).Review, policy.MaintainResource)
	if wood.Planner != waitText || !wood.PlannerWaiting || wood.Blocked != policy.BlockedWaiting(waitText) || wood.Blocked.Actionable() || !wood.Blocked.Waiting() {
		t.Fatalf("waiting wood record %+v", wood)
	}
	if _, err := s.RecordPlannerReasons(ctx, map[policy.GoalID]policy.PlannerNote{policy.MaintainResource: {Text: spaceText}}); err != nil {
		t.Fatal(err)
	}
	review, err := s.LoadRoutineReview(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if wood = progressRecord(t, review, policy.MaintainResource); wood.PlannerWaiting || wood.Blocked != policy.BlockedPlanner(spaceText) {
		t.Fatalf("refused wood record %+v", wood)
	}
	if _, err = s.RecordPlannerReasons(ctx, map[policy.GoalID]policy.PlannerNote{policy.MaintainResource: wait}); err != nil {
		t.Fatal(err)
	}
	if _, err = s.RecordPlannerReasons(ctx, map[policy.GoalID]policy.PlannerNote{policy.MaintainResource: {}}); err != nil {
		t.Fatal(err)
	}
	if review, err = s.LoadRoutineReview(ctx); err != nil {
		t.Fatal(err)
	}
	if wood = progressRecord(t, review, policy.MaintainResource); wood.Planner != "" || wood.PlannerWaiting || wood.Blocked != policy.BlockedNoMethod {
		t.Fatalf("cleared wood record %+v", wood)
	}
}
