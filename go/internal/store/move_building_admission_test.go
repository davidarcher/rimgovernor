package store

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func moveBuildingStoreFixture(t *testing.T) (*Store, string, domain.Action, MoveBuildingAdmission) {
	t.Helper()
	ctx := context.Background()
	path := filepath.Join(t.TempDir(), "move-building.db")
	s := open(t, path)
	move, _ := domain.NewMoveBuilding("Thing_Bed7", "Bed", domain.Cell{X: 5, Z: 9}, domain.East)
	a, _ := domain.NewMoveBuildingAction("move-1", move)
	plan, err := domain.NewPlan("plan", 1, []domain.Action{a})
	if err != nil {
		t.Fatal(err)
	}
	if err = s.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	snapshot := domain.GenerationSnapshot{Colony: "colony", Load: "load", Map: 0, Plan: "plan", Revision: 1, Native: 2}
	return s, path, a, MoveBuildingAdmission{Snapshot: snapshot, Tick: 12, Thing: "Thing_Bed7"}
}

func TestMoveBuildingActionRoundTripsAndPrepares(t *testing.T) {
	ctx := context.Background()
	s, path, a, v := moveBuildingStoreFixture(t)
	if _, err := s.Dispatch(ctx, "plan", "move-1", v.Snapshot, v.Tick); err == nil {
		t.Fatal("dispatch without admission accepted")
	}
	if _, err := s.Prepare(ctx, "plan", "move-1", v.Snapshot, v.Tick); err == nil {
		t.Fatal("generic prepare accepted a move action")
	}
	wrong := v
	wrong.Thing = "Thing_Other"
	if _, err := s.PrepareMoveBuilding(ctx, "plan", "move-1", wrong); err == nil {
		t.Fatal("admission for another building accepted")
	}
	if _, err := s.PrepareMoveBuilding(ctx, "plan", "move-1", v); err != nil {
		t.Fatal(err)
	}
	if _, err := s.Dispatch(ctx, "plan", "move-1", v.Snapshot, v.Tick); err != nil {
		t.Fatal(err)
	}
	if err := s.Close(); err != nil {
		t.Fatal(err)
	}
	s = open(t, path)
	state, err := s.LoadPlan(ctx, "plan")
	if err != nil || state.Spec.Actions()[0] != a || len(state.MoveBuildingAdmissions) != 1 || state.MoveBuildingAdmissions[0].Admission != v {
		t.Fatal(state, err)
	}
}
