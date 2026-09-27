package store

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestCorpseBillSurvivesJournalReload(t *testing.T) {
	s := open(t, filepath.Join(t.TempDir(), "corpse.db"))
	bill, err := domain.NewCorpseBill("crematorium", domain.CremateRecipe, "tok", domain.CorpseStranger)
	if err != nil {
		t.Fatal(err)
	}
	action, err := domain.NewProductionBillAction("cremate", bill)
	if err != nil {
		t.Fatal(err)
	}
	plan, _ := domain.NewPlan("cremation", 1, []domain.Action{action})
	if err = s.CreatePlan(context.Background(), plan); err != nil {
		t.Fatal(err)
	}
	read, err := s.LoadPlan(context.Background(), plan.ID())
	if err != nil {
		t.Fatal(err)
	}
	if got, _ := read.Spec.Actions()[0].ProductionBill(); got != bill {
		t.Fatal(got, bill)
	}
}
