package store

import (
	"context"
	"encoding/json"
	"path/filepath"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func recoveryRequest() RoutineReviewRequest {
	r := routineRequest()
	k := domain.Known(false)
	r.Facts.DisasterConditions = domain.Known([]policy.DisasterCondition{{ID: "fallout", Definition: "ToxicFallout"}})
	r.Facts.RecoverySafety = domain.Known(policy.RecoverySafety{RoofHazard: domain.Known(true), SafeAreas: []string{"roof"}, Restrictions: []policy.RecoveryRestriction{{Pawn: "pawn", Area: domain.Known("player-area")}}})
	r.Facts.RecoveryWorkers = domain.Known([]policy.RecoveryWorker{{Pawn: "pawn", Dead: k, Downed: k, Drafted: k, Mental: k, PlayerForced: k}})
	r.Facts.RecoveryBuildings = domain.Known([]policy.RecoveryBuilding{})
	return r
}
func TestRoutineRecoveryProposalRestartManualAndCancellation(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "recovery.db")
	s := open(t, path)
	r := recoveryRequest()
	out := reviewRoutine(t, s, &r)
	first := out.Review.Recovery
	if first == nil || first.Selection.Reason != policy.RecoveryAdmissionRequired || len(first.Selection.Candidates) != 1 {
		t.Fatal(first)
	}
	g := routineGoal(t, out, policy.RecoverDisasterServices)
	if g.Goal.Need != domain.NeedDeficit || g.Goal.Priority != 2 {
		t.Fatal(g)
	}
	s.Close()
	s = open(t, path)
	loaded, err := s.LoadRoutineReview(ctx)
	if err != nil || !reflect.DeepEqual(loaded.Recovery, first) {
		t.Fatal(loaded.Recovery, err)
	}
	r.Enabled = false
	out = reviewRoutine(t, s, &r)
	if out.Review.Recovery != nil || out.Review.Disaster == nil {
		t.Fatal("Manual retained proposals or lost history")
	}
	s.Close()
	s = open(t, path)
	defer s.Close()
	loaded, err = s.LoadRoutineReview(ctx)
	if err != nil || loaded.Recovery != nil {
		t.Fatal(loaded, err)
	}
	r.Enabled = true
	r.Current.Native++
	out = reviewRoutine(t, s, &r)
	if out.Review.Recovery == nil || out.Review.Recovery.Goal == first.Goal {
		t.Fatal("new direction reused prior executable identity")
	}
	g = routineGoal(t, out, policy.RecoverDisasterServices)
	if _, err = s.CancelGoal(ctx, g.Goal.ID, g.Revision); err != nil {
		t.Fatal(err)
	}
	out = reviewRoutine(t, s, &r)
	if out.Review.Recovery != nil {
		t.Fatal("cancelled goal proposed recovery", out.Review.Recovery)
	}
	if _, err = s.LoadRoutineReview(ctx); err != nil {
		t.Fatal(err)
	}
	r.Current.Load = "replacement"
	r.Facts.DisasterConditions = domain.Known([]policy.DisasterCondition{})
	out = reviewRoutine(t, s, &r)
	if out.Review.Disaster != nil || out.Review.Recovery != nil {
		t.Fatal("replacement world retained proposals")
	}
}
func TestRoutineRecoveryUnknownWorkerDoesNotBecomeAvailableAfterRestart(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "unknown.db")
	s := open(t, path)
	r := recoveryRequest()
	workers, _ := r.Facts.RecoveryWorkers.Value()
	workers[0].PlayerForced = domain.Unknown[bool]()
	r.Facts.RecoveryWorkers = domain.Known(workers)
	out := reviewRoutine(t, s, &r)
	if out.Review.Recovery.Selection.Reason != policy.RecoveryFactsUnknown {
		t.Fatal(out)
	}
	s.Close()
	s = open(t, path)
	defer s.Close()
	loaded, err := s.LoadRoutineReview(ctx)
	if err != nil || loaded.Recovery.Selection.Reason != policy.RecoveryFactsUnknown || (*loaded.Recovery.Workers)[0].PlayerForced != nil {
		t.Fatal(loaded, err)
	}
}
func TestRoutineRecoveryRejectsCorruptProposalInputs(t *testing.T) {
	t.Parallel()
	for _, name := range []string{"target", "worker", "restriction", "epoch", "goal", "disabled", "used"} {
		t.Run(name, func(t *testing.T) {
			s := open(t, filepath.Join(t.TempDir(), "corrupt.db"))
			defer s.Close()
			r := recoveryRequest()
			out := reviewRoutine(t, s, &r)
			v := out.Review
			switch name {
			case "target":
				v.Recovery.Selection.Candidates[0].Area = "outside"
			case "worker":
				value := true
				(*v.Recovery.Workers)[0].PlayerForced = &value
			case "restriction":
				value := "another"
				v.Recovery.Safety.Restrictions[0].Area = &value
			case "epoch":
				v.Recovery.Epoch++
			case "goal":
				v.Recovery.Goal = "other"
			case "disabled":
				v.Enabled = false
			case "used":
				v.Recovery.Used = append(v.Recovery.Used, v.Recovery.Selection.Candidates[0].ID)
			}
			data, err := json.Marshal(v)
			if err != nil {
				t.Fatal(err)
			}
			if _, err = s.db.Exec("UPDATE routine_review SET payload=? WHERE singleton=1", data); err != nil {
				t.Fatal(err)
			}
			if _, err = s.LoadRoutineReview(context.Background()); err == nil {
				t.Fatal("corrupt recovery accepted")
			}
		})
	}
}

func TestRoutineRecoverySkipsSharedGoalMethodHistory(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "methods.db"))
	defer s.Close()
	r := recoveryRequest()
	out := reviewRoutine(t, s, &r)
	candidate := out.Review.Recovery.Selection.Candidates[0]
	g := routineGoal(t, out, policy.RecoverDisasterServices)
	if _, err := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, candidate.ID, plan(t, "recovery-plan", "recovery-action")); err != nil {
		t.Fatal(err)
	}
	out = reviewRoutine(t, s, &r)
	if out.Review.Recovery == nil || len(out.Review.Recovery.Used) != 1 || out.Review.Recovery.Used[0] != candidate.ID || out.Review.Recovery.Selection.Reason != policy.RecoveryMethodsSeen {
		t.Fatal("shared method was proposed twice", out.Review.Recovery)
	}
	if _, err := s.LoadRoutineReview(ctx); err != nil {
		t.Fatal(err)
	}
}

func TestRoutineRecoveryEmergencySuspendsCandidatesUntilObservedClearance(t *testing.T) {
	t.Parallel()
	s := open(t, filepath.Join(t.TempDir(), "emergency.db"))
	defer s.Close()
	r := recoveryRequest()
	r.Facts.Hostiles = domain.Known(int64(1))
	out := reviewRoutine(t, s, &r)
	if routineGoal(t, out, policy.RecoverDisasterServices).Goal.Status != domain.GoalSuspended || out.Review.Recovery != nil {
		t.Fatal("emergency allowed recovery proposals", out.Review.Recovery)
	}
	r.Facts.Hostiles = domain.Known(int64(0))
	out = reviewRoutine(t, s, &r)
	if out.Review.Recovery == nil || out.Review.Recovery.Selection.Reason != policy.RecoveryAdmissionRequired {
		t.Fatal("observed clearance did not restore planning")
	}
}
