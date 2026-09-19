package store

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"path/filepath"
	"testing"
)

func TestMealReplacementSurvivesJournalReload(t *testing.T) {
	s := open(t, filepath.Join(t.TempDir(), "replacement.db"))
	bill, _ := domain.NewProductionBill("stove", "CookMealSimple", "current-stack", domain.FoodTarget, 12)
	bill, err := bill.ReplaceOwnedBill("old-fine")
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewProductionBillAction("replace", bill)
	if err != nil {
		t.Fatal(err)
	}
	plan, _ := domain.NewPlan("replacement", 1, []domain.Action{action})
	if err = s.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	read, err := s.LoadPlan(context.Background(), plan.ID())
	if err != nil {
		t.Fatal(err)
	}
	got, _ := read.Spec.Actions()[0].ProductionBill()
	if got != bill {
		t.Fatal(got, bill)
	}
}
