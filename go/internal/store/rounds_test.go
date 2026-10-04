package store

import (
	"context"
	"errors"
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/slowtest"
)

func roundsRequest() RoundsRequest {
	return RoundsRequest{Current: scope(), Tick: 10, Enabled: true, Policy: policy.DefaultRoundsPolicy(), Facts: policy.RoundsFacts{Workers: domain.Known(2), Wood: domain.Known(int64(100)), Hostiles: domain.Known(int64(0)), CriticalPatients: domain.Known(int64(0)), CleanupPawns: domain.Known(false), ColonyNaming: domain.Known(false), ChoiceDialog: domain.Known(false)}}
}

// A review whose facts carry the Biotech pollution read binds the same goals
// the loader derives from empty facts, so the stored review loads back.
func TestRoundsWithPollutionFactsLoads(t *testing.T) {
	t.Parallel()
	s := open(t, memoryPath(t))
	r := roundsRequest()
	r.Facts.Pollution = domain.Known(policy.PollutionFacts{UncoveredCells: domain.Known(uint32(0))})
	r.Facts.MechChargerOwed = domain.Known(false)
	r.Facts.GeneBankOwed = domain.Known(false)
	out := reviewRounds(t, s, &r)
	loaded, err := s.LoadRounds(context.Background())
	if err != nil || !reflect.DeepEqual(out.Review, loaded) {
		t.Fatal(loaded, err)
	}
}

func roundsGoal(t *testing.T, r RoundsResult, need domain.ConcernID) StandardState {
	t.Helper()
	for i, b := range r.Review.Standards {
		if b.Concern == need {
			return r.Standards[i]
		}
	}
	t.Fatal("missing routine goal", need)
	return StandardState{}
}

// roundsIncident is the review's open colony-wide occurrence of kind.
func roundsIncident(t *testing.T, r RoundsResult, kind domain.ConcernID) IncidentState {
	t.Helper()
	for i, b := range r.Review.Incidents {
		if b.Kind == kind && b.Subject == "" {
			return r.Incidents[i]
		}
	}
	t.Fatal("missing routine incident", kind)
	return IncidentState{}
}

// roundsIncidentNeed is the need the review bound kind's occurrence at.
func roundsIncidentNeed(t *testing.T, r RoundsResult, kind domain.ConcernID) domain.Situation {
	t.Helper()
	b, ok := r.Review.Incident(kind)
	if !ok {
		t.Fatal("missing routine incident", kind)
	}
	return b.Situation
}

func reviewRounds(t *testing.T, s *Store, r *RoundsRequest) RoundsResult {
	t.Helper()
	out, err := s.ReviewRounds(context.Background(), *r)
	if err != nil {
		t.Fatal(err)
	}
	r.Revision = out.Review.Revision
	r.Tick++
	return out
}

func TestRoundsRestartUnknownRecoveryAndRenewal(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	path := memoryPath(t)
	s := open(t, path)
	r := roundsRequest()
	out := reviewRounds(t, s, &r)
	initial := roundsGoal(t, out, policy.MaintainResource)
	if initial.Standard.Finding != domain.FindingUnmet || !out.Review.Latches.Wood {
		t.Fatal(out)
	}
	s.Close()
	s = open(t, path)
	loaded, err := s.LoadRounds(ctx)
	if err != nil || !reflect.DeepEqual(out.Review, loaded) {
		t.Fatal(loaded, err)
	}
	r.Facts.Wood = domain.Known(int64(200))
	out = reviewRounds(t, s, &r)
	if roundsGoal(t, out, policy.MaintainResource).Standard.Finding != domain.FindingUnmet {
		t.Fatal("restart lost recovery threshold")
	}
	r.Facts.Wood = domain.Unknown[int64]()
	out = reviewRounds(t, s, &r)
	g := roundsGoal(t, out, policy.MaintainResource)
	if g.Standard.Finding != domain.FindingUnclear || !out.Review.Latches.Wood {
		t.Fatal(g)
	}
	if _, err = s.CommitMethod(ctx, g.Standard.ID, g.Revision, "unknown", plan(t, "p", "a")); err == nil {
		t.Fatal("unknown need admitted method")
	}
	r.Facts.Wood = domain.Known(int64(400))
	out = reviewRounds(t, s, &r)
	if roundsGoal(t, out, policy.MaintainResource).Standard.Status != domain.StandardSettled {
		t.Fatal(out)
	}
	r.Facts.Wood = domain.Known(int64(100))
	out = reviewRounds(t, s, &r)
	g = roundsGoal(t, out, policy.MaintainResource)
	if g.Standard.Episode != 1 || g.Standard.ID != initial.Standard.ID || g.Standard.Finding != domain.FindingUnmet {
		t.Fatal(g)
	}
}

func TestRoundsSuspendsOrInvalidatesLinkedWork(t *testing.T) {
	slowtest.Skip(t, "runs under cmd/test -full and nightly")
	t.Parallel()
	for _, change := range []string{"manual", "load", "map", "rewind"} {
		t.Run(change, func(t *testing.T) {
			ctx := context.Background()
			s := open(t, memoryPath(t))
			r := roundsRequest()
			out := reviewRounds(t, s, &r)
			g := roundsGoal(t, out, policy.MaintainResource)
			if _, err := s.CommitMethod(ctx, g.Standard.ID, g.Revision, "wood", plan(t, "p", "a")); err != nil {
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
				r.Policy = policy.RoundsPolicy{}
				r.Facts.Wood = domain.Known(int64(-1))
			case "load":
				r.Current.Load = "replacement"
			case "map":
				r.Current.Map++
			case "rewind":
				r.Tick = 1
			}
			reviewRounds(t, s, &r)
			p, err := s.LoadPlan(ctx, "p")
			if err != nil {
				t.Fatal(err)
			}
			old, err := s.LoadStandard(ctx, g.Standard.ID)
			if err != nil {
				t.Fatal(err)
			}
			if change == "manual" {
				// Manual vetoes new work: dispatched work stays open for the resumed goal.
				if p.Progress[0].View().Stage != domain.Dispatched || old.Standard.Status != domain.StandardOpen {
					t.Fatal(p.Progress[0].View().Stage, old.Standard.Status)
				}
				if _, err := s.Prepare(ctx, "p", "a", scope(), 10); err == nil {
					t.Fatal("suspended goal admitted work")
				}
			} else if p.Progress[0].View().Stage != domain.Cancelled || !p.Progress[0].View().Unresolved || old.Standard.Status != domain.StandardVoided {
				t.Fatal(p, old)
			}
			r.Enabled = true
			r.Policy = policy.DefaultRoundsPolicy()
			r.Facts.Wood = domain.Known(int64(100))
			out = reviewRounds(t, s, &r)
			resumed := roundsGoal(t, out, policy.MaintainResource)
			if change == "manual" {
				if resumed.Standard.ID != g.Standard.ID || resumed.Standard.Status != domain.StandardOpen {
					t.Fatal("resume replaced the suspended goal", resumed)
				}
			} else if resumed.Standard.ID == g.Standard.ID {
				t.Fatal("reused invalidated goal")
			}
		})
	}
}

func TestRoundsTransactionRollbackAndStaleCursor(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := roundsRequest()
	out := reviewRounds(t, s, &r)
	stale := r
	stale.Revision--
	if _, err := s.ReviewRounds(ctx, stale); !errors.Is(err, ErrConflict) {
		t.Fatal(err)
	}
	if _, err := s.db.ExecContext(ctx, `CREATE TRIGGER fail_review BEFORE UPDATE ON rounds BEGIN SELECT RAISE(ABORT,'review failure'); END`); err != nil {
		t.Fatal(err)
	}
	r.Enabled = false
	if _, err := s.ReviewRounds(ctx, r); err == nil {
		t.Fatal("injected failure ignored")
	}
	loaded, err := s.LoadRounds(ctx)
	if err != nil || !reflect.DeepEqual(out.Review, loaded) {
		t.Fatal("review partially committed", loaded, err)
	}
	for _, before := range out.Standards {
		after, err := s.LoadStandard(ctx, before.Standard.ID)
		if err != nil || !reflect.DeepEqual(before, after) {
			t.Fatal("goal changed despite rollback", after, err)
		}
	}
}

func TestRoundsEmergencyHoldsSharedMethodUntilObservedRecovery(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, memoryPath(t))
	r := roundsRequest()
	out := reviewRounds(t, s, &r)
	g := roundsGoal(t, out, policy.MaintainResource)
	if _, err := s.CommitMethod(ctx, g.Standard.ID, g.Revision, "wood", plan(t, "p", "a")); err != nil {
		t.Fatal(err)
	}
	r.Facts.Hostiles = domain.Unknown[int64]()
	out = reviewRounds(t, s, &r)
	if out.Review.Veto(roundsGoal(t, out, policy.MaintainResource).Standard) == "" {
		t.Fatal(out)
	}
	if _, err := s.Prepare(ctx, "p", "a", scope(), r.Tick); err == nil {
		t.Fatal("unknown threat allowed routine work")
	}
	r.Facts.Hostiles = domain.Known(int64(0))
	reviewRounds(t, s, &r)
	if _, err := s.Prepare(ctx, "p", "a", scope(), r.Tick); err != nil {
		t.Fatal("observed safety did not resume shared method", err)
	}
}

func TestRoundsDirectionAndManualDoNotEraseRecoveryTarget(t *testing.T) {
	t.Parallel()
	s := open(t, memoryPath(t))
	r := roundsRequest()
	reviewRounds(t, s, &r)
	r.Facts.Wood = domain.Known(int64(200))
	r.Current.Native++
	out := reviewRounds(t, s, &r)
	if !out.Review.Latches.Wood || roundsGoal(t, out, policy.MaintainResource).Standard.Finding != domain.FindingUnmet {
		t.Fatal("direction erased known recovery target")
	}
	r.Enabled = false
	reviewRounds(t, s, &r)
	r.Enabled = true
	out = reviewRounds(t, s, &r)
	if !out.Review.Latches.Wood || roundsGoal(t, out, policy.MaintainResource).Standard.Finding != domain.FindingUnmet {
		t.Fatal("Manual erased known recovery target")
	}
}
