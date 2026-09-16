package store

import (
	"context"
	"encoding/json"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func tendStoreFixture(t *testing.T) (*Store, string, TendAdmission) {
	t.Helper()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "tend.db")
	s := open(t, path)
	tend, _ := domain.NewTend("doctor", "patient")
	a, _ := domain.NewTendAction("tend", tend)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Revision: 1, Native: 2}
	v := TendAdmission{Snapshot: snapshot, Tick: 12, Doctor: "doctor", Patient: "patient", DoctorSnapshotToken: "doctor-cas", PatientSnapshotToken: "patient-cas"}
	return s, path, v
}

func TestTendAdmissionPrepareAndLoad(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _, v := tendStoreFixture(t)
	if _, err := s.PrepareTend(ctx, "plan", "tend", v); err != nil {
		t.Fatal(err)
	}
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || len(state.TendAdmissions) != 1 || state.TendAdmissions[0].Admission != v || state.Progress[0].View().Stage != domain.Prepared {
		t.Fatal(state, err)
	}
	if _, err := s.Dispatch(ctx, "plan", "tend", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
}

func TestTendDispatchRequiresCurrentAdmission(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _, v := tendStoreFixture(t)
	if _, err := s.Dispatch(ctx, "plan", "tend", v.Snapshot, v.Tick); err == nil {
		t.Fatal("dispatch without admission accepted")
	}
	if _, err := s.Prepare(ctx, "plan", "tend", v.Snapshot, v.Tick); err == nil {
		t.Fatal("generic prepare accepted a tend action")
	}
}

func TestTendAdmissionTerminalRejectsFutureEvidence(t *testing.T) {
	t.Parallel()
	for _, effect := range []domain.Effect{domain.EffectCompleted, domain.EffectUnsuccessful} {
		t.Run(string(effect), func(t *testing.T) {
			ctx := context.Background()
			s, _, v := tendStoreFixture(t)
			if _, err := s.PrepareTend(ctx, "plan", "tend", v); err != nil {
				t.Fatal(err)
			}
			if _, err := s.Dispatch(ctx, "plan", "tend", v.Snapshot, v.Tick); err != nil {
				t.Fatal(err)
			}
			observation := domain.Observation{Action: "tend", Attempt: 1, Snapshot: v.Snapshot, Tick: v.Tick + 1, Causality: domain.AfterDispatch, Effect: effect}
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
			if _, err = s.db.Exec("UPDATE tend_admissions SET payload=?", data); err != nil {
				t.Fatal(err)
			}
			if _, err = s.LoadPlan(ctx, "plan"); err == nil {
				t.Fatal("terminal action accepted future admission")
			}
		})
	}
}

func TestTendAdmissionPreparedRefreshThenCancelRetainsEvidence(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, v := tendStoreFixture(t)
	if _, err := s.PrepareTend(ctx, "plan", "tend", v); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "plan", "tend", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Observe(ctx, "plan", domain.Observation{Action: "tend", Attempt: 1, Snapshot: v.Snapshot, Tick: 13, Causality: domain.AfterDispatch, Effect: domain.EffectAbsent}, v.Snapshot); err != nil {
		t.Fatal(err)
	}
	v.Tick = 14
	if _, err := s.PrepareTend(ctx, "plan", "tend", v); err != nil {
		t.Fatal(err)
	}
	v.Tick = 15
	v.DoctorSnapshotToken = "refreshed-after-absence"
	if _, err := s.PrepareTend(ctx, "plan", "tend", v); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Cancel(ctx, "plan", "tend"); err != nil {
		t.Fatal(err)
	}
	if err := s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || state.Progress[0].View().Stage != domain.Cancelled || state.TendAdmissions[0].Admission != v {
		t.Fatal(state, err)
	}
}
