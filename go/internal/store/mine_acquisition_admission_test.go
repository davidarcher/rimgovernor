package store

import (
	"context"
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
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Revision: 1, Native: 2}
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
