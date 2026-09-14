package store

import (
	"context"
	"encoding/json"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func surgeryStoreFixture(t *testing.T) (*Store, string, SurgeryAdmission) {
	t.Helper()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "surgery.db")
	s := open(t, path)
	surgery, _ := domain.NewSurgery("patient", "RemoveBodyPart", 3)
	a, _ := domain.NewSurgeryAction("surgery", surgery)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Revision: 1, Direction: 1, Native: 2}
	v := SurgeryAdmission{Snapshot: snapshot, Tick: 12, Patient: "patient", Recipe: "RemoveBodyPart", Part: 3, PatientSnapshotToken: "patient-cas", HealthToken: "health-cas", Care: domain.MedicalCareNormalOrWorse}
	return s, path, v
}

func TestSurgeryAdmissionPrepareAndLoad(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _, v := surgeryStoreFixture(t)
	if _, err := s.PrepareSurgery(ctx, "plan", "surgery", v); err != nil {
		t.Fatal(err)
	}
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || len(state.SurgeryAdmissions) != 1 || state.SurgeryAdmissions[0].Admission != v || state.Progress[0].View().Stage != domain.Prepared {
		t.Fatal(state, err)
	}
	if _, err := s.Dispatch(ctx, "plan", "surgery", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
}

func TestSurgeryDispatchRequiresCurrentAdmission(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, _, v := surgeryStoreFixture(t)
	if _, err := s.Dispatch(ctx, "plan", "surgery", v.Snapshot, v.Tick); err == nil {
		t.Fatal("dispatch without admission accepted")
	}
	if _, err := s.Prepare(ctx, "plan", "surgery", v.Snapshot, v.Tick); err == nil {
		t.Fatal("generic prepare accepted a surgery action")
	}
}

func TestSurgeryAdmissionTerminalRejectsFutureEvidence(t *testing.T) {
	t.Parallel()
	for _, effect := range []domain.Effect{domain.EffectCompleted, domain.EffectUnsuccessful} {
		t.Run(string(effect), func(t *testing.T) {
			ctx := context.Background()
			s, _, v := surgeryStoreFixture(t)
			if _, err := s.PrepareSurgery(ctx, "plan", "surgery", v); err != nil {
				t.Fatal(err)
			}
			if _, err := s.Dispatch(ctx, "plan", "surgery", v.Snapshot, v.Tick); err != nil {
				t.Fatal(err)
			}
			observation := domain.Observation{Action: "surgery", Attempt: 1, Snapshot: v.Snapshot, Tick: v.Tick + 1, Causality: domain.AfterDispatch, Effect: effect}
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
			if _, err = s.db.Exec("UPDATE surgery_admissions SET payload=?", data); err != nil {
				t.Fatal(err)
			}
			if _, err = s.LoadPlan(ctx, "plan"); err == nil {
				t.Fatal("terminal action accepted future admission")
			}
		})
	}
}

func TestSurgeryAdmissionPreparedRefreshThenCancelRetainsEvidence(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s, path, v := surgeryStoreFixture(t)
	if _, err := s.PrepareSurgery(ctx, "plan", "surgery", v); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "plan", "surgery", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Observe(ctx, "plan", domain.Observation{Action: "surgery", Attempt: 1, Snapshot: v.Snapshot, Tick: 13, Causality: domain.AfterDispatch, Effect: domain.EffectAbsent}, v.Snapshot); err != nil {
		t.Fatal(err)
	}
	v.Tick = 14
	if _, err := s.PrepareSurgery(ctx, "plan", "surgery", v); err != nil {
		t.Fatal(err)
	}
	v.Tick = 15
	v.HealthToken = "refreshed-after-absence"
	if _, err := s.PrepareSurgery(ctx, "plan", "surgery", v); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Cancel(ctx, "plan", "surgery"); err != nil {
		t.Fatal(err)
	}
	if err := s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || state.Progress[0].View().Stage != domain.Cancelled || state.SurgeryAdmissions[0].Admission != v {
		t.Fatal(state, err)
	}
}
