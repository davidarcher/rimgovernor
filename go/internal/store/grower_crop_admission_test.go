package store

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A grower crop patch persists through the same action row and patch
// admission record as a building temperature or bed medical patch: round trip, typed prepare,
// generic prepare refused.
func TestGrowerCropActionRoundTripAndAdmission(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "grower_crop.db"))
	bm, _ := domain.NewGrowerCrop("basin", "Plant_Potato", "before-cas")
	a, _ := domain.NewGrowerCropAction("crop", bm)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil {
		t.Fatal(err)
	}
	if got, ok := state.Spec.Actions()[0].GrowerCrop(); !ok || got != bm {
		t.Fatal("grower crop action did not round trip", state.Spec.Actions()[0])
	}
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Revision: 1, Native: 2}
	v := BuildingTemperatureAdmission{Snapshot: snapshot, Tick: 12, Thing: "basin", SnapshotToken: "before-cas"}
	if _, err := s.Prepare(ctx, "plan", "crop", v.Snapshot, v.Tick); err == nil {
		t.Fatal("generic prepare accepted a grower crop action")
	}
	if _, err := s.Dispatch(ctx, "plan", "crop", v.Snapshot, v.Tick); err == nil {
		t.Fatal("dispatch without admission accepted")
	}
	if _, err := s.PrepareBuildingTemperature(ctx, "plan", "crop", v); err != nil {
		t.Fatal(err)
	}
	state, err = s.LoadPlan(ctx, "plan")
	if err != nil || len(state.BuildingTemperatureAdmissions) != 1 || state.BuildingTemperatureAdmissions[0].Admission != v || state.Progress[0].View().Stage != domain.Prepared {
		t.Fatal(state, err)
	}
	if _, err := s.Dispatch(ctx, "plan", "crop", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
}
