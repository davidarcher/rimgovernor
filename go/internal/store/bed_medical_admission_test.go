package store

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A bed medical patch persists through the same action row and patch
// admission record as a building temperature: round trip, typed prepare,
// generic prepare refused.
func TestBedMedicalActionRoundTripAndAdmission(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "bed_medical.db"))
	bm, _ := domain.NewBedMedical("bed", true, "before-cas")
	a, _ := domain.NewBedMedicalAction("medical", bm)
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
	if got, ok := state.Spec.Actions()[0].BedMedical(); !ok || got != bm {
		t.Fatal("bed medical action did not round trip", state.Spec.Actions()[0])
	}
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Revision: 1, Native: 2}
	v := BuildingTemperatureAdmission{Snapshot: snapshot, Tick: 12, Thing: "bed", SnapshotToken: "before-cas"}
	if _, err := s.Prepare(ctx, "plan", "medical", v.Snapshot, v.Tick); err == nil {
		t.Fatal("generic prepare accepted a bed medical action")
	}
	if _, err := s.Dispatch(ctx, "plan", "medical", v.Snapshot, v.Tick); err == nil {
		t.Fatal("dispatch without admission accepted")
	}
	if _, err := s.PrepareBuildingTemperature(ctx, "plan", "medical", v); err != nil {
		t.Fatal(err)
	}
	state, err = s.LoadPlan(ctx, "plan")
	if err != nil || len(state.BuildingTemperatureAdmissions) != 1 || state.BuildingTemperatureAdmissions[0].Admission != v || state.Progress[0].View().Stage != domain.Prepared {
		t.Fatal(state, err)
	}
	if _, err := s.Dispatch(ctx, "plan", "medical", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
}
