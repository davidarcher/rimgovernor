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
	r := roundsRequest()
	reviewRounds(t, s, &r)
	refusal := policy.PlannerNote{Cause: policy.CauseNoSpace, Subject: "wood"}
	changed, _, err := s.RecordPlannerReasons(ctx, map[policy.ConcernID]policy.PlannerNote{policy.MaintainResource: refusal})
	if err != nil || !changed {
		t.Fatal(changed, err)
	}
	if changed, _, err = s.RecordPlannerReasons(ctx, map[policy.ConcernID]policy.PlannerNote{policy.MaintainResource: refusal}); err != nil || changed {
		t.Fatal("unchanged reason rewrote the review", err)
	}
	second := reviewRounds(t, s, &r)
	wood := progressRecord(t, second.Review, policy.MaintainResource)
	if wood.Planner != policy.CauseNoSpace || wood.PlannerSubject != "wood" || wood.Blocked != policy.BlockedReason(policy.CauseNoSpace) || wood.BlockedSubject() != "wood" || !wood.Blocked.Actionable() {
		t.Fatalf("wood record %+v", wood)
	}
	if _, _, err = s.RecordPlannerReasons(ctx, map[policy.ConcernID]policy.PlannerNote{policy.MaintainResource: {}}); err != nil {
		t.Fatal(err)
	}
	review, err := s.LoadRounds(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if wood = progressRecord(t, review, policy.MaintainResource); wood.Planner != "" || wood.Blocked != policy.BlockedNoMethod {
		t.Fatalf("cleared wood record %+v", wood)
	}
}

// A cause outside the closed set is refused, not filed.
func TestRecordPlannerReasonsRejectsAnUnknownCause(t *testing.T) {
	t.Parallel()
	s := open(t, memoryPath(t))
	defer s.Close()
	r := roundsRequest()
	reviewRounds(t, s, &r)
	if _, _, err := s.RecordPlannerReasons(context.Background(), map[policy.ConcernID]policy.PlannerNote{policy.MaintainResource: {Cause: "no site found"}}); err == nil {
		t.Fatal("unknown cause filed")
	}
}

// A wait files as a wait, survives the next review as one (not as a
// refusal), is replaced by a refusal, and clears when the planner admits.
func TestRecordPlannerReasonsFilesWaitsAndClearsThem(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	defer s.Close()
	r := roundsRequest()
	reviewRounds(t, s, &r)
	wait := policy.PlannerNote{Cause: policy.CauseBunksOpen}
	if changed, _, err := s.RecordPlannerReasons(ctx, map[policy.ConcernID]policy.PlannerNote{policy.MaintainResource: wait}); err != nil || !changed {
		t.Fatal(changed, err)
	}
	wood := progressRecord(t, reviewRounds(t, s, &r).Review, policy.MaintainResource)
	if wood.Planner != policy.CauseBunksOpen || wood.Blocked != policy.BlockedReason(policy.CauseBunksOpen) || wood.Blocked.Actionable() || !wood.Blocked.Waiting() {
		t.Fatalf("waiting wood record %+v", wood)
	}
	if _, _, err := s.RecordPlannerReasons(ctx, map[policy.ConcernID]policy.PlannerNote{policy.MaintainResource: {Cause: policy.CauseNoSpace, Subject: "wood"}}); err != nil {
		t.Fatal(err)
	}
	review, err := s.LoadRounds(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if wood = progressRecord(t, review, policy.MaintainResource); wood.Blocked != policy.BlockedReason(policy.CauseNoSpace) {
		t.Fatalf("refused wood record %+v", wood)
	}
	if _, _, err = s.RecordPlannerReasons(ctx, map[policy.ConcernID]policy.PlannerNote{policy.MaintainResource: wait}); err != nil {
		t.Fatal(err)
	}
	if _, _, err = s.RecordPlannerReasons(ctx, map[policy.ConcernID]policy.PlannerNote{policy.MaintainResource: {}}); err != nil {
		t.Fatal(err)
	}
	if review, err = s.LoadRounds(ctx); err != nil {
		t.Fatal(err)
	}
	if wood = progressRecord(t, review, policy.MaintainResource); wood.Planner != "" || wood.Blocked != policy.BlockedNoMethod {
		t.Fatalf("cleared wood record %+v", wood)
	}
}
