package store

import (
	"context"
	"errors"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func routineRequest() RoutineReviewRequest {
	return RoutineReviewRequest{Current: scope(), Tick: 10, Enabled: true, Policy: policy.DefaultRoutinePolicy(), Facts: policy.RoutineFacts{Workers: domain.Known(2), Wood: domain.Known(int64(100)), Hostiles: domain.Known(int64(0)), CriticalPatients: domain.Known(int64(0)), CleanupPawns: domain.Known(false), ColonyNaming: domain.Known(false), ChoiceDialog: domain.Known(false)}}
}

// A review whose facts carry the Biotech pollution read binds the same goals
// the loader derives from empty facts, so the stored review loads back.
func TestRoutineReviewWithPollutionFactsLoads(t *testing.T) {
	t.Parallel()
	s := open(t, memoryPath(t))
	r := routineRequest()
	r.Facts.Pollution = domain.Known(policy.PollutionFacts{UncoveredCells: domain.Known(uint32(0))})
	r.Facts.MechChargerOwed = domain.Known(false)
	r.Facts.GeneBankOwed = domain.Known(false)
	out := reviewRoutine(t, s, &r)
	loaded, err := s.LoadRoutineReview(context.Background())
	if err != nil || !reflect.DeepEqual(out.Review, loaded) {
		t.Fatal(loaded, err)
	}
}

func routineGoal(t *testing.T, r RoutineReviewResult, need domain.ConcernID) GoalState {
	t.Helper()
	for i, b := range r.Review.Goals {
		if b.Need == need {
			return r.Goals[i]
		}
	}
	t.Fatal("missing routine goal", need)
	return GoalState{}
}

// routineIncident is the review's open colony-wide occurrence of kind.
func routineIncident(t *testing.T, r RoutineReviewResult, kind domain.ConcernID) IncidentState {
	t.Helper()
	for i, b := range r.Review.Incidents {
		if b.Kind == kind && b.Subject == "" {
			return r.Incidents[i]
		}
	}
	t.Fatal("missing routine incident", kind)
	return IncidentState{}
}

// routineIncidentNeed is the need the review bound kind's occurrence at.
func routineIncidentNeed(t *testing.T, r RoutineReviewResult, kind domain.ConcernID) domain.NeedState {
	t.Helper()
	b, ok := r.Review.Incident(kind)
	if !ok {
		t.Fatal("missing routine incident", kind)
	}
	return b.Need
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
	path := memoryPath(t)
	s := open(t, path)
	r := routineRequest()
	out := reviewRoutine(t, s, &r)
	initial := routineGoal(t, out, policy.MaintainResource)
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
	if routineGoal(t, out, policy.MaintainResource).Goal.Need != domain.NeedDeficit {
		t.Fatal("restart lost recovery threshold")
	}
	r.Facts.Wood = domain.Unknown[int64]()
	out = reviewRoutine(t, s, &r)
	g := routineGoal(t, out, policy.MaintainResource)
	if g.Goal.Need != domain.NeedUnknown || !out.Review.Latches.Wood {
		t.Fatal(g)
	}
	if _, err = s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "unknown", plan(t, "p", "a")); err == nil {
		t.Fatal("unknown need admitted method")
	}
	r.Facts.Wood = domain.Known(int64(400))
	out = reviewRoutine(t, s, &r)
	if routineGoal(t, out, policy.MaintainResource).Goal.Status != domain.GoalSatisfied {
		t.Fatal(out)
	}
	r.Facts.Wood = domain.Known(int64(100))
	out = reviewRoutine(t, s, &r)
	g = routineGoal(t, out, policy.MaintainResource)
	if g.Goal.Epoch != 1 || g.Goal.ID != initial.Goal.ID || g.Goal.Need != domain.NeedDeficit {
		t.Fatal(g)
	}
}

func TestRoutineReviewSuspendsOrInvalidatesLinkedWork(t *testing.T) {
	t.Parallel()
	for _, change := range []string{"manual", "load", "map", "rewind"} {
		t.Run(change, func(t *testing.T) {
			ctx := context.Background()
			s := open(t, memoryPath(t))
			r := routineRequest()
			out := reviewRoutine(t, s, &r)
			g := routineGoal(t, out, policy.MaintainResource)
			if _, err := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "wood", plan(t, "p", "a")); err != nil {
				t.Fatal(err)
			}
			if _, err := s.Prepare(ctx, "p", "a", scope(), 10); err != nil {
				t.Fatal(err)
			}
			if _, err := s.Dispatch(ctx, "p", "a", scope(), 10); err != nil {
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
				// Manual vetoes new work: dispatched work stays open for the resumed goal.
				if p.Progress[0].View().Stage != domain.Dispatched || old.Goal.Status != domain.GoalActive {
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
			resumed := routineGoal(t, out, policy.MaintainResource)
			if change == "manual" {
				if resumed.Goal.ID != g.Goal.ID || resumed.Goal.Status != domain.GoalActive {
					t.Fatal("resume replaced the suspended goal", resumed)
				}
			} else if resumed.Goal.ID == g.Goal.ID {
				t.Fatal("reused invalidated goal")
			}
		})
	}
}

func TestRoutineReviewTransactionRollbackAndStaleCursor(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
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
	s := open(t, memoryPath(t))
	r := routineRequest()
	out := reviewRoutine(t, s, &r)
	g := routineGoal(t, out, policy.MaintainResource)
	if _, err := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "wood", plan(t, "p", "a")); err != nil {
		t.Fatal(err)
	}
	r.Facts.Hostiles = domain.Unknown[int64]()
	out = reviewRoutine(t, s, &r)
	if out.Review.Veto(routineGoal(t, out, policy.MaintainResource).Goal) == "" {
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
	s := open(t, memoryPath(t))
	r := routineRequest()
	reviewRoutine(t, s, &r)
	r.Facts.Wood = domain.Known(int64(200))
	r.Current.Native++
	out := reviewRoutine(t, s, &r)
	if !out.Review.Latches.Wood || routineGoal(t, out, policy.MaintainResource).Goal.Need != domain.NeedDeficit {
		t.Fatal("direction erased known recovery target")
	}
	r.Enabled = false
	reviewRoutine(t, s, &r)
	r.Enabled = true
	out = reviewRoutine(t, s, &r)
	if !out.Review.Latches.Wood || routineGoal(t, out, policy.MaintainResource).Goal.Need != domain.NeedDeficit {
		t.Fatal("Manual erased known recovery target")
	}
}
