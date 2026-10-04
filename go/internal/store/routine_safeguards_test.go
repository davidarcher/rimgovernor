package store

import (
	"context"
	"errors"
	"strings"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The Safeguards veto method admission during an emergency and a pause, with
// the Safeguard's reason, and admit once the emergency clears (#1017). The goal
// itself stays active throughout: priority orders work only.
func TestRoutineSafeguardsVetoAdmissionUntilEmergencyClears(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	defer s.Close()
	r := routineRequest()
	r.Facts.Upkeep.Fires = domain.Known([]policy.UpkeepFire{{ID: "fire", Home: true, Size: domain.Known(.5)}})
	out := reviewRoutine(t, s, &r)
	if len(out.Review.Emergency) != 1 || out.Review.Emergency[0] != policy.MaintainFireSafety {
		t.Fatal("review did not record the emergency", out.Review.Emergency)
	}
	loaded, err := s.LoadRoutineReview(ctx)
	if err != nil || len(loaded.Emergency) != 1 {
		t.Fatal("emergency not journalled with the review", loaded.Emergency, err)
	}
	g := routineGoal(t, out, policy.MaintainResource)
	if g.Goal.Status != domain.GoalActive {
		t.Fatal("an emergency must not change the goal's status", g.Goal.Status)
	}
	_, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "wood", plan(t, "p", "a"))
	if !errors.Is(err, ErrNotAdmitted) || !strings.Contains(err.Error(), "emergency MaintainFireSafety") {
		t.Fatal("emergency admitted a priority>=2 method", err)
	}
	r.Facts.Upkeep.Fires = domain.Known([]policy.UpkeepFire{})
	r.Tick++
	out = reviewRoutine(t, s, &r)
	if len(out.Review.Emergency) != 0 {
		t.Fatal("emergency did not clear", out.Review.Emergency)
	}
	g = routineGoal(t, out, policy.MaintainResource)
	if _, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "wood", plan(t, "p", "a")); err != nil {
		t.Fatal("cleared emergency still vetoed", err)
	}
	if _, err = s.Prepare(ctx, "p", "a", scope(), r.Tick); err != nil {
		t.Fatal(err)
	}
	r.Enabled = false
	r.Tick++
	out = reviewRoutine(t, s, &r)
	g = routineGoal(t, out, policy.MaintainResource)
	if reason := out.Review.Veto(g.Goal); reason != "control paused" {
		t.Fatal("pause did not veto routine work", reason)
	}
	if _, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "wood2", plan(t, "p2", "a2")); !errors.Is(err, ErrNotAdmitted) {
		t.Fatal("pause admitted a routine method", err)
	}
	if _, err = s.Dispatch(ctx, "p", "a", scope(), r.Tick); !errors.Is(err, ErrNotAdmitted) {
		t.Fatal("pause dispatched a prepared plan", err)
	}
}
