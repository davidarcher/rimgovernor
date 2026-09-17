package store

import (
	"context"
	"errors"
	"path/filepath"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func routineRequest() RoutineReviewRequest {
	return RoutineReviewRequest{Current: scope(), Tick: 10, Enabled: true, Policy: policy.DefaultRoutinePolicy(), Facts: policy.RoutineFacts{Workers: domain.Known(2), Wood: domain.Known(int64(100)), Hostiles: domain.Known(int64(0)), CriticalPatients: domain.Known(int64(0)), CleanupPawns: domain.Known(false), ColonyNaming: domain.Known(false)}}
}
func routineGoal(t *testing.T, r RoutineReviewResult, need domain.GoalID) GoalState {
	t.Helper()
	for i, b := range r.Review.Goals {
		if b.Need == need {
			return r.Goals[i]
		}
	}
	t.Fatal("missing routine goal", need)
	return GoalState{}
}
func reviewRoutine(t *testing.T, s *Store, r *RoutineReviewRequest) RoutineReviewResult {
	t.Helper()
	out, err := s.ReviewRoutine(context.Background(), *r)
	if err != nil {
		t.Fatal(err)
	}
	r.Revision = out.Review.Revision
	r.Tick++
	return out
}

func TestRoutineReviewRestartUnknownRecoveryAndRenewal(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "routine.db")
	s := open(t, path)
	r := routineRequest()
	out := reviewRoutine(t, s, &r)
	initial := routineGoal(t, out, policy.MaintainWood)
	if initial.Goal.Need != domain.NeedDeficit || !out.Review.Latches.Wood {
		t.Fatal(out)
	}
	s.Close()
	s = open(t, path)
	loaded, err := s.LoadRoutineReview(ctx)
	if err != nil || !reflect.DeepEqual(out.Review, loaded) {
		t.Fatal(loaded, err)
	}
	r.Facts.Wood = domain.Known(int64(200))
	out = reviewRoutine(t, s, &r)
	if routineGoal(t, out, policy.MaintainWood).Goal.Need != domain.NeedDeficit {
		t.Fatal("restart lost recovery threshold")
	}
	r.Facts.Wood = domain.Unknown[int64]()
	out = reviewRoutine(t, s, &r)
	g := routineGoal(t, out, policy.MaintainWood)
	if g.Goal.Need != domain.NeedUnknown || !out.Review.Latches.Wood {
		t.Fatal(g)
	}
	if _, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "unknown", plan(t, "p", "a")); err == nil {
		t.Fatal("unknown need admitted method")
	}
	r.Facts.Wood = domain.Known(int64(400))
	out = reviewRoutine(t, s, &r)
	if routineGoal(t, out, policy.MaintainWood).Goal.Status != domain.GoalSatisfied {
		t.Fatal(out)
	}
	r.Facts.Wood = domain.Known(int64(100))
	out = reviewRoutine(t, s, &r)
	g = routineGoal(t, out, policy.MaintainWood)
	if g.Goal.Epoch != 1 || g.Goal.ID != initial.Goal.ID || g.Goal.Need != domain.NeedDeficit {
		t.Fatal(g)
	}
}

func TestRoutineReviewSuspendsOrInvalidatesLinkedWorkAndPreservesCancellation(t *testing.T) {
	t.Parallel()
	for _, change := range []string{"manual", "load", "map", "rewind"} {
		t.Run(change, func(t *testing.T) {
			ctx := context.Background()
			s := open(t, filepath.Join(t.TempDir(), "routine.db"))
			r := routineRequest()
			out := reviewRoutine(t, s, &r)
			g := routineGoal(t, out, policy.MaintainWood)
			if _, err := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "wood", plan(t, "p", "a")); err != nil {
				t.Fatal(err)
			}
			if _, err := s.Prepare(ctx, "p", "a", scope(), 10); err != nil {
				t.Fatal(err)
			}
			if _, err := s.Dispatch(ctx, "p", "a", scope(), 10); err != nil {
				t.Fatal(err)
			}
			cancelled := routineGoal(t, out, policy.EnsureCooking)
			if _, err := s.CancelGoal(ctx, cancelled.Goal.ID, cancelled.Revision); err != nil {
				t.Fatal(err)
			}
			switch change {
			case "manual":
				r.Enabled = false
				r.Policy = policy.RoutinePolicy{}
				r.Facts.Wood = domain.Known(int64(-1))
			case "load":
				r.Current.Load = "replacement"
			case "map":
				r.Current.Map++
			case "rewind":
				r.Tick = 1
			}
			reviewRoutine(t, s, &r)
			p, err := s.LoadPlan(ctx, "p")
			if err != nil {
				t.Fatal(err)
			}
			old, err := s.LoadGoal(ctx, g.Goal.ID)
			if err != nil {
				t.Fatal(err)
			}
			if change == "manual" {
				// Manual suspends: dispatched work stays open for the resumed goal.
				if p.Progress[0].View().Stage != domain.Dispatched || old.Goal.Status != domain.GoalSuspended {
					t.Fatal(p.Progress[0].View().Stage, old.Goal.Status)
				}
				if _, err := s.Prepare(ctx, "p", "a", scope(), 10); err == nil {
					t.Fatal("suspended goal admitted work")
				}
			} else if p.Progress[0].View().Stage != domain.Cancelled || !p.Progress[0].View().Unresolved || old.Goal.Status != domain.GoalInvalidated {
				t.Fatal(p, old)
			}
			r.Enabled = true
			r.Policy = policy.DefaultRoutinePolicy()
			r.Facts.Wood = domain.Known(int64(100))
			out = reviewRoutine(t, s, &r)
			resumed := routineGoal(t, out, policy.MaintainWood)
			if change == "manual" {
				if resumed.Goal.ID != g.Goal.ID || resumed.Goal.Status != domain.GoalActive {
					t.Fatal("resume replaced the suspended goal", resumed)
				}
			} else if resumed.Goal.ID == g.Goal.ID {
				t.Fatal("reused invalidated goal")
			}
			if routineGoal(t, out, policy.EnsureCooking).Goal.Status != domain.GoalCancelled {
				t.Fatal("routine review revived player cancellation")
			}
		})
	}
}

func TestRoutineReviewTransactionRollbackAndStaleCursor(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "routine.db"))
	r := routineRequest()
	out := reviewRoutine(t, s, &r)
	stale := r
	stale.Revision--
	if _, err := s.ReviewRoutine(ctx, stale); !errors.Is(err, ErrConflict) {
		t.Fatal(err)
	}
	if _, err := s.db.ExecContext(ctx, `CREATE TRIGGER fail_review BEFORE UPDATE ON routine_review BEGIN SELECT RAISE(ABORT,'review failure'); END`); err != nil {
		t.Fatal(err)
	}
	r.Enabled = false
	if _, err := s.ReviewRoutine(ctx, r); err == nil {
		t.Fatal("injected failure ignored")
	}
	loaded, err := s.LoadRoutineReview(ctx)
	if err != nil || !reflect.DeepEqual(out.Review, loaded) {
		t.Fatal("review partially committed", loaded, err)
	}
	for _, before := range out.Goals {
		after, err := s.LoadGoal(ctx, before.Goal.ID)
		if err != nil || !reflect.DeepEqual(before, after) {
			t.Fatal("goal changed despite rollback", after, err)
		}
	}
}

func TestRoutineEmergencyHoldsSharedMethodUntilObservedRecovery(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "routine.db"))
	r := routineRequest()
	out := reviewRoutine(t, s, &r)
	g := routineGoal(t, out, policy.MaintainWood)
	if _, err := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "wood", plan(t, "p", "a")); err != nil {
		t.Fatal(err)
	}
	r.Facts.Hostiles = domain.Unknown[int64]()
	out = reviewRoutine(t, s, &r)
	if routineGoal(t, out, policy.MaintainWood).Goal.Status != domain.GoalSuspended {
		t.Fatal(out)
	}
	if _, err := s.Prepare(ctx, "p", "a", scope(), r.Tick); err == nil {
		t.Fatal("unknown threat allowed routine work")
	}
	r.Facts.Hostiles = domain.Known(int64(0))
	reviewRoutine(t, s, &r)
	if _, err := s.Prepare(ctx, "p", "a", scope(), r.Tick); err != nil {
		t.Fatal("observed safety did not resume shared method", err)
	}
}

func TestRoutineDirectionAndManualDoNotEraseRecoveryTarget(t *testing.T) {
	t.Parallel()
	s := open(t, filepath.Join(t.TempDir(), "routine.db"))
	r := routineRequest()
	reviewRoutine(t, s, &r)
	r.Facts.Wood = domain.Known(int64(200))
	r.Current.Native++
	out := reviewRoutine(t, s, &r)
	if !out.Review.Latches.Wood || routineGoal(t, out, policy.MaintainWood).Goal.Need != domain.NeedDeficit {
		t.Fatal("direction erased known recovery target")
	}
	r.Enabled = false
	reviewRoutine(t, s, &r)
	r.Enabled = true
	out = reviewRoutine(t, s, &r)
	if !out.Review.Latches.Wood || routineGoal(t, out, policy.MaintainWood).Goal.Need != domain.NeedDeficit {
		t.Fatal("Manual erased known recovery target")
	}
}
