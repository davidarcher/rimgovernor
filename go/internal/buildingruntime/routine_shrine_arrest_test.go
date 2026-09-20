package buildingruntime

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	"path/filepath"
	"testing"
)

func TestShrineArrestPlanPersistsCoupledDraftAndExactBed(t *testing.T) {
	plan, err := shrineArrestPlan("arrest-plan", "warden", "ancient", "prison")
	if err != nil {
		t.Fatal(err)
	}
	actions := plan.Actions()
	if len(actions) != 2 || len(plan.Dependencies()) != 1 || !plan.Dependencies()[0].Coupled {
		t.Fatal(plan)
	}
	capture, ok := actions[1].Capture()
	if !ok || !capture.Arrest() || capture.Bed() != "prison" || capture.Patient() != "ancient" {
		t.Fatal(capture)
	}
	if _, err := domain.NewPlan("invalid", 1, actions); err == nil {
		t.Fatal("arrest without coupled draft accepted")
	}
	db, err := store.Open(context.Background(), filepath.Join(t.TempDir(), "journal.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	ctx := context.Background()
	if err := db.CreatePlan(ctx, plan); err != nil {
		t.Fatal(err)
	}
	loaded, err := db.LoadPlan(ctx, plan.ID())
	if err != nil {
		t.Fatal(err)
	}
	if loaded.Spec.Actions()[1] != actions[1] || loaded.Spec.Dependencies()[0] != plan.Dependencies()[0] {
		t.Fatal("arrest intent changed on journal round trip")
	}
}
