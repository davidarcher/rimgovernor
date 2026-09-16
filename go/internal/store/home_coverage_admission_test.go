package store

import (
	"context"
	"encoding/json"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func homeCoverageStoreFixture(t *testing.T) (*Store, string, HomeCoverageAdmission) {
	t.Helper()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "home_coverage.db")
	s := open(t, path)
	coverage, _ := domain.NewHomeCoverage("building-1", "shape-token")
	a, _ := domain.NewHomeCoverageAction("home-coverage-1", coverage)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Revision: 1, Native: 2}
	v := HomeCoverageAdmission{Snapshot: snapshot, Tick: 12, Target: "building-1", Shape: "shape-token", Revision: 4, Missing: 3, Excluded: 0}
	return s, path, v
}

func TestHomeCoverageAdmissionPrepareAndLoad(t *testing.T) {
	ctx := context.Background()
	s, _, v := homeCoverageStoreFixture(t)
	if _, err := s.PrepareHomeCoverage(ctx, "plan", "home-coverage-1", v); err != nil {
		t.Fatal(err)
	}
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || len(state.HomeCoverageAdmissions) != 1 || state.HomeCoverageAdmissions[0].Admission != v || state.Progress[0].View().Stage != domain.Prepared {
		t.Fatal(state, err)
	}
	if _, err := s.Dispatch(ctx, "plan", "home-coverage-1", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
}

func TestHomeCoverageDispatchRequiresCurrentAdmission(t *testing.T) {
	ctx := context.Background()
	s, _, v := homeCoverageStoreFixture(t)
	if _, err := s.Dispatch(ctx, "plan", "home-coverage-1", v.Snapshot, v.Tick); err == nil {
		t.Fatal("dispatch without admission accepted")
	}
	if _, err := s.Prepare(ctx, "plan", "home-coverage-1", v.Snapshot, v.Tick); err == nil {
		t.Fatal("generic prepare accepted a home coverage action")
	}
}

func TestHomeCoverageAdmissionRejectsShapeMismatch(t *testing.T) {
	ctx := context.Background()
	s, _, v := homeCoverageStoreFixture(t)
	v.Shape = "different-shape"
	if _, err := s.PrepareHomeCoverage(ctx, "plan", "home-coverage-1", v); err == nil {
		t.Fatal("admission accepted a shape mismatched with the action")
	}
}

func TestHomeCoverageAdmissionRejectsInvalidCounts(t *testing.T) {
	ctx := context.Background()
	s, _, v := homeCoverageStoreFixture(t)
	v.Excluded = v.Missing + 1
	if _, err := s.PrepareHomeCoverage(ctx, "plan", "home-coverage-1", v); err == nil {
		t.Fatal("admission accepted excluded greater than missing")
	}
}

func TestHomeCoverageAdmissionTerminalRejectsFutureEvidence(t *testing.T) {
	for _, effect := range []domain.Effect{domain.EffectCompleted, domain.EffectUnsuccessful} {
		t.Run(string(effect), func(t *testing.T) {
			ctx := context.Background()
			s, _, v := homeCoverageStoreFixture(t)
			if _, err := s.PrepareHomeCoverage(ctx, "plan", "home-coverage-1", v); err != nil {
				t.Fatal(err)
			}
			if _, err := s.Dispatch(ctx, "plan", "home-coverage-1", v.Snapshot, v.Tick); err != nil {
				t.Fatal(err)
			}
			observation := domain.Observation{Action: "home-coverage-1", Attempt: 1, Snapshot: v.Snapshot, Tick: v.Tick + 1, Causality: domain.AfterDispatch, Effect: effect}
			if effect == domain.EffectUnsuccessful {
				observation.UnsuccessfulReason = domain.NativeFailure
			}
			if _, err := s.Observe(ctx, "plan", observation, v.Snapshot); err != nil {
				t.Fatal(err)
			}
			v.Tick = observation.Tick + 1
			data, err := json.Marshal(v)
			if err != nil {
				t.Fatal(err)
			}
			if _, err = s.db.Exec("UPDATE home_coverage_admissions SET payload=?", data); err != nil {
				t.Fatal(err)
			}
			if _, err = s.LoadPlan(ctx, "plan"); err == nil {
				t.Fatal("terminal action accepted future admission")
			}
		})
	}
}

func TestHomeCoverageAdmissionPreparedRefreshThenCancelRetainsEvidence(t *testing.T) {
	ctx := context.Background()
	s, path, v := homeCoverageStoreFixture(t)
	if _, err := s.PrepareHomeCoverage(ctx, "plan", "home-coverage-1", v); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "plan", "home-coverage-1", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Observe(ctx, "plan", domain.Observation{Action: "home-coverage-1", Attempt: 1, Snapshot: v.Snapshot, Tick: 13, Causality: domain.AfterDispatch, Effect: domain.EffectAbsent}, v.Snapshot); err != nil {
		t.Fatal(err)
	}
	v.Tick = 14
	if _, err := s.PrepareHomeCoverage(ctx, "plan", "home-coverage-1", v); err != nil {
		t.Fatal(err)
	}
	v.Tick = 15
	v.Missing = 2
	if _, err := s.PrepareHomeCoverage(ctx, "plan", "home-coverage-1", v); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Cancel(ctx, "plan", "home-coverage-1"); err != nil {
		t.Fatal(err)
	}
	if err := s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || state.Progress[0].View().Stage != domain.Cancelled || state.HomeCoverageAdmissions[0].Admission != v {
		t.Fatal(state, err)
	}
}
