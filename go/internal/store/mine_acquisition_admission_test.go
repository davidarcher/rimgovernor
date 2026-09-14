package store

import (
	"context"
	"encoding/json"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func mineAcquisitionStoreFixture(t *testing.T) (*Store, string, MineAcquisitionAdmission) {
	t.Helper()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "mine-acquisition.db")
	s := open(t, path)
	acquisition, _ := domain.NewAcquisition("Rock1", "Steel", domain.Cell{X: 3, Z: 4})
	a, _ := domain.NewMineAcquisitionAction("mine-1", acquisition)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Revision: 1, Direction: 1, Native: 2}
	v := MineAcquisitionAdmission{Snapshot: snapshot, Tick: 12, Thing: "Rock1", SnapshotToken: "mine-cas"}
	return s, path, v
}

func TestMineAcquisitionAdmissionPrepareAndLoad(t *testing.T) {
	ctx := context.Background()
	s, _, v := mineAcquisitionStoreFixture(t)
	if _, err := s.PrepareMineAcquisition(ctx, "plan", "mine-1", v); err != nil {
		t.Fatal(err)
	}
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || len(state.MineAcquisitionAdmissions) != 1 || state.MineAcquisitionAdmissions[0].Admission != v || state.Progress[0].View().Stage != domain.Prepared {
		t.Fatal(state, err)
	}
	if _, err := s.Dispatch(ctx, "plan", "mine-1", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
}

func TestMineAcquisitionDispatchRequiresCurrentAdmission(t *testing.T) {
	ctx := context.Background()
	s, _, v := mineAcquisitionStoreFixture(t)
	if _, err := s.Dispatch(ctx, "plan", "mine-1", v.Snapshot, v.Tick); err == nil {
		t.Fatal("dispatch without admission accepted")
	}
	if _, err := s.Prepare(ctx, "plan", "mine-1", v.Snapshot, v.Tick); err == nil {
		t.Fatal("generic prepare accepted a mine acquisition action")
	}
}

func TestMineAcquisitionAdmissionTerminalRejectsFutureEvidence(t *testing.T) {
	for _, effect := range []domain.Effect{domain.EffectCompleted, domain.EffectUnsuccessful} {
		t.Run(string(effect), func(t *testing.T) {
			ctx := context.Background()
			s, _, v := mineAcquisitionStoreFixture(t)
			if _, err := s.PrepareMineAcquisition(ctx, "plan", "mine-1", v); err != nil {
				t.Fatal(err)
			}
			if _, err := s.Dispatch(ctx, "plan", "mine-1", v.Snapshot, v.Tick); err != nil {
				t.Fatal(err)
			}
			observation := domain.Observation{Action: "mine-1", Attempt: 1, Snapshot: v.Snapshot, Tick: v.Tick + 1, Causality: domain.AfterDispatch, Effect: effect}
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
			if _, err = s.db.Exec("UPDATE mine_acquisition_admissions SET payload=?", data); err != nil {
				t.Fatal(err)
			}
			if _, err = s.LoadPlan(ctx, "plan"); err == nil {
				t.Fatal("terminal action accepted future admission")
			}
		})
	}
}

func TestMineAcquisitionAdmissionPreparedRefreshThenCancelRetainsEvidence(t *testing.T) {
	ctx := context.Background()
	s, path, v := mineAcquisitionStoreFixture(t)
	if _, err := s.PrepareMineAcquisition(ctx, "plan", "mine-1", v); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "plan", "mine-1", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Observe(ctx, "plan", domain.Observation{Action: "mine-1", Attempt: 1, Snapshot: v.Snapshot, Tick: 13, Causality: domain.AfterDispatch, Effect: domain.EffectAbsent}, v.Snapshot); err != nil {
		t.Fatal(err)
	}
	v.Tick = 14
	if _, err := s.PrepareMineAcquisition(ctx, "plan", "mine-1", v); err != nil {
		t.Fatal(err)
	}
	v.Tick = 15
	v.SnapshotToken = "refreshed-after-absence"
	if _, err := s.PrepareMineAcquisition(ctx, "plan", "mine-1", v); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Cancel(ctx, "plan", "mine-1"); err != nil {
		t.Fatal(err)
	}
	if err := s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || state.Progress[0].View().Stage != domain.Cancelled || state.MineAcquisitionAdmissions[0].Admission != v {
		t.Fatal(state, err)
	}
}
