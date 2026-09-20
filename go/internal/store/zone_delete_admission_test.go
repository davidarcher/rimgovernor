package store

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// A zone deletion (#611) persists through the same action row and patch
// admission record as a building temperature or bed medical patch: round trip, typed prepare,
// generic prepare refused.
func TestZoneDeleteActionRoundTripAndAdmission(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	s := open(t, filepath.Join(t.TempDir(), "zone_delete.db"))
	bm, _ := domain.NewZoneDelete("Zone_7", "before-cas")
	a, _ := domain.NewZoneDeleteAction("del", bm)
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
	if got, ok := state.Spec.Actions()[0].ZoneDelete(); !ok || got != bm {
		t.Fatal("zone delete action did not round trip", state.Spec.Actions()[0])
	}
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Revision: 1, Native: 2}
	v := BuildingTemperatureAdmission{Snapshot: snapshot, Tick: 12, Thing: "Zone_7", SnapshotToken: "before-cas"}
	if _, err := s.Prepare(ctx, "plan", "del", v.Snapshot, v.Tick); err == nil {
		t.Fatal("generic prepare accepted a zone delete action")
	}
	if _, err := s.Dispatch(ctx, "plan", "del", v.Snapshot, v.Tick); err == nil {
		t.Fatal("dispatch without admission accepted")
	}
	if _, err := s.PrepareBuildingTemperature(ctx, "plan", "del", v); err != nil {
		t.Fatal(err)
	}
	state, err = s.LoadPlan(ctx, "plan")
	if err != nil || len(state.BuildingTemperatureAdmissions) != 1 || state.BuildingTemperatureAdmissions[0].Admission != v || state.Progress[0].View().Stage != domain.Prepared {
		t.Fatal(state, err)
	}
	if _, err := s.Dispatch(ctx, "plan", "del", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
}
