package store

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func TestColonyPlanReplansAndForgetsOnRewind(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	db, err := Open(ctx, filepath.Join(t.TempDir(), "state.sqlite"))
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	w := extentWorld("colony", "load", 3)
	first := []policy.PlanModule{{Role: policy.ModulePlaza}, {U: 1, Role: policy.ModuleStorage}}
	grown := append(first, policy.PlanModule{U: 4, V: -4, Role: policy.ModuleKillbox})
	if err := db.RecordColonyPlan(ctx, w, 100, 3, first); err != nil {
		t.Fatal(err)
	}
	if err := db.RecordColonyPlan(ctx, w, 200, 4, grown); err != nil {
		t.Fatal(err)
	}
	if r, ok, err := db.ColonyPlan(ctx, w, 250); err != nil || !ok || r.Radius != 4 || len(r.Modules) != 3 || r.Tick != 200 {
		t.Fatal(r, ok, err)
	}
	// A same-load rewind past the replan forgets it.
	if r, ok, err := db.ColonyPlan(ctx, w, 150); err != nil || !ok || r.Radius != 3 || len(r.Modules) != 2 {
		t.Fatal(r, ok, err)
	}
	if r, ok, err := db.ColonyPlan(ctx, w, 250); err != nil || !ok || r.Radius != 3 {
		t.Fatal("the rewind kept the replan", r, ok, err)
	}
	if err := db.RecordColonyPlan(ctx, w, 300, 3, []policy.PlanModule{{U: 5, Role: policy.ModulePlaza}}); err == nil {
		t.Fatal("recorded a module outside the radius")
	}
}
